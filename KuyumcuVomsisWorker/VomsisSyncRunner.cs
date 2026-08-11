namespace KuyumcuVomsisWorker;

public sealed class VomsisSyncRunner
{
    private readonly VomsisApiClient _vomsis;
    private readonly ErpImportClient _erp;
    private readonly ErpWorkerConfigClient _configClient;
    private readonly ILogger<VomsisSyncRunner> _logger;

    public VomsisSyncRunner(
        VomsisApiClient vomsis,
        ErpImportClient erp,
        ErpWorkerConfigClient configClient,
        ILogger<VomsisSyncRunner> logger)
    {
        _vomsis = vomsis;
        _erp = erp;
        _configClient = configClient;
        _logger = logger;
    }

    public async Task<VomsisSyncRunResult> RunOnceAsync(VomsisSyncRunRequest? request, CancellationToken ct)
    {
        var remote = await _configClient.FetchAsync(request, ct);
        if (remote is null || !remote.IsEnabled)
        {
            return VomsisSyncRunResult.Fail("Banka sync profili bulunamadı veya devre dışı.");
        }

        _vomsis.Configure(remote.VomsisAppKey!, remote.VomsisAppSecret!);
        _erp.Configure(remote);

        // WPF "Vomsis Dekont" butonu bu id'leri kuyruğa yazar — import'tan bağımsız doğrudan uygula.
        var pendingApplied = await ProcessPendingEnrichmentsAsync(remote, ct);

        var lookbackDays = Math.Clamp(remote.LookbackDays, 1, 30);
        var endUtc = DateTime.UtcNow;
        var beginUtc = endUtc.AddDays(-lookbackDays);

        _logger.LogInformation("Vomsis hareketleri çekiliyor: {Begin} - {End}", beginUtc, endUtc);
        var raw = await _vomsis.GetTransactionsAsync(beginUtc, endUtc, ct);
        if (raw.Count == 0)
        {
            var empty = new VomsisSyncRunResult
            {
                Success = true,
                FetchedFromVomsis = 0,
                Imported = pendingApplied,
                SummaryMessage = pendingApplied > 0
                    ? $"Bekleyen dekont zenginleştirmesi uygulandı: {pendingApplied}."
                    : "Vomsis'te seçilen tarih aralığında hareket bulunamadı.",
                PollIntervalMinutes = remote.PollIntervalMinutes
            };
            // Son sync zamanını her döngüde güncelle; aksi halde aralık bozulur.
            await _erp.CompleteManualSyncAsync(empty, ct);
            return empty;
        }

        // Önce listeyi ERP'ye yaz — dekont detayı beklenmeden hareketler görünsün.
        var mappedQuick = raw.Select(VomsisTransactionMapper.ToErp).ToList();
        var import = await _erp.ImportAsync(mappedQuick, ct)
            ?? throw new InvalidOperationException("ERP import yanıtı boş.");

        // Sonra TCKN/şube için dekont zenginleştir; mevcut satırlara patch uygula.
        await EnrichMissingTaxFromDetailsAsync(raw, ct);
        var mappedEnriched = raw.Select(VomsisTransactionMapper.ToErp).ToList();
        var directApplied = await PushEnrichmentsToErpAsync(mappedEnriched, ct);

        var runResult = new VomsisSyncRunResult
        {
            Success = true,
            FetchedFromVomsis = raw.Count,
            Received = import.Received,
            Imported = import.Imported + pendingApplied + directApplied,
            SkippedDuplicate = import.SkippedDuplicate,
            SkippedFilter = import.SkippedFilter,
            DraftCreated = import.DraftCreated,
            PendingReview = import.PendingReview,
            MissingTaxId = import.MissingTaxId,
            NoCustomerMatch = import.NoCustomerMatch,
            SummaryMessage =
                $"Vomsis: {raw.Count} hareket, ERP'ye {import.Imported} kayıt, " +
                $"dekont patch: {pendingApplied + directApplied} " +
                $"(taslak: {import.DraftCreated}, bekleyen: {import.PendingReview}, atlandı: {import.SkippedFilter}).",
            PollIntervalMinutes = remote.PollIntervalMinutes
        };

        await _erp.CompleteManualSyncAsync(runResult, ct);
        return runResult;
    }

    public async Task<VomsisTaxEnrichResult> EnrichTaxAsync(
        VomsisSyncRunRequest request,
        long externalId,
        CancellationToken ct)
    {
        var remote = await _configClient.FetchAsync(request, ct);
        if (remote is null || !remote.IsEnabled)
            return VomsisTaxEnrichResult.Fail("Banka sync profili bulunamadı veya devre dışı.");

        _vomsis.Configure(remote.VomsisAppKey!, remote.VomsisAppSecret!);
        var detail = await _vomsis.GetTransactionDetailAsync(externalId, ct);
        if (detail is null)
            return VomsisTaxEnrichResult.Fail("Vomsis dekont/detay yanıtı alınamadı.");

        var tax = VomsisTransactionMapper.ResolveCounterpartyTaxNo(detail);
        var isOutgoing = VomsisTransactionMapper.IsOutgoingTransfer(detail);
        var title = isOutgoing
            ? FirstNonEmpty(detail.OpponentTitle, detail.RelatedTitle, detail.IlgiliUnvan, detail.SenderTitle, detail.SenderName)
            : FirstNonEmpty(detail.RelatedTitle, detail.IlgiliUnvan, detail.SenderName, detail.SenderTitle);
        var branchName = VomsisTransactionMapper.ResolveBranchName(detail);
        var (city, district) = VomsisTransactionMapper.ParseCityDistrictFromBranchName(branchName);
        var ok = !string.IsNullOrWhiteSpace(tax) || !string.IsNullOrWhiteSpace(branchName);
        return new VomsisTaxEnrichResult
        {
            Success = ok,
            CounterpartyTaxNo = tax,
            CounterpartyName = title,
            BankBranchName = branchName,
            BankBranchCity = city,
            BankBranchDistrict = district,
            Message = !string.IsNullOrWhiteSpace(tax)
                ? "TCKN/VKN ve şube bilgisi Vomsis dekontundan alındı."
                : (!string.IsNullOrWhiteSpace(branchName)
                    ? "Şube bilgisi alındı; TCKN/VKN dekontta yok."
                    : "Vomsis dekontunda geçerli TCKN/VKN veya şube bulunamadı.")
        };
    }

    private async Task<int> ProcessPendingEnrichmentsAsync(RemoteWorkerConfig remote, CancellationToken ct)
    {
        var pendingIds = remote.PendingEnrichExternalIds ?? [];
        if (pendingIds.Length == 0)
            return 0;

        _logger.LogInformation("Bekleyen dekont zenginleştirmesi: {Count} id", pendingIds.Length);
        var applied = 0;
        foreach (var externalId in pendingIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var detail = await _vomsis.GetTransactionDetailAsync(externalId, ct);
                if (detail is null)
                {
                    _logger.LogWarning("Pending enrich: dekont alınamadı id={Id}", externalId);
                    continue;
                }

                var erpTx = VomsisTransactionMapper.ToErp(detail);
                if (await _erp.ApplyEnrichmentAsync(erpTx, ct))
                    applied++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pending enrich hata id={Id}", externalId);
            }
        }

        return applied;
    }

    private async Task<int> PushEnrichmentsToErpAsync(
        IReadOnlyList<ErpImportTransaction> mapped,
        CancellationToken ct)
    {
        var applied = 0;
        foreach (var tx in mapped)
        {
            if (string.IsNullOrWhiteSpace(tx.SenderTaxNo) &&
                string.IsNullOrWhiteSpace(tx.BankBranchName) &&
                string.IsNullOrWhiteSpace(tx.BankBranchCity))
                continue;

            try
            {
                if (await _erp.ApplyEnrichmentAsync(tx, ct))
                    applied++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Apply-enrichment hata id={Id}", tx.ExternalId);
            }
        }

        return applied;
    }

    private async Task EnrichMissingTaxFromDetailsAsync(
        IReadOnlyList<VomsisTransaction> raw,
        CancellationToken ct)
    {
        const int maxDetailPerCycle = 25;

        // Önce eksik TCKN/şube olanlar; en yeni hareketler önce (gecikmeyi azaltır).
        var candidates = raw
            .Where(tx => IsTryTransfer(tx) && NeedsDetailEnrichment(tx))
            .OrderByDescending(tx => VomsisTransactionMapper.ToErp(tx).TransactionDateUtc ?? DateTime.MinValue)
            .ThenByDescending(tx => tx.Id)
            .Take(maxDetailPerCycle)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogInformation("Dekont zenginleştirmesi: aday yok (TL havale değil veya tamam).");
            return;
        }

        _logger.LogInformation(
            "TCKN/VKN+şube için {Count}/{Total} hareketin dekont detayı çekilecek (döngü limiti {Limit}).",
            candidates.Count, raw.Count, maxDetailPerCycle);

        var enrichedTax = 0;
        var enrichedBranch = 0;
        foreach (var tx in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var detail = await _vomsis.GetTransactionDetailAsync(tx.Id, ct);
                if (detail is null)
                    continue;

                MergeDetail(tx, detail);
                var resolvedTax = VomsisTransactionMapper.ResolveCounterpartyTaxNo(tx);
                if (!string.IsNullOrWhiteSpace(resolvedTax))
                {
                    tx.SenderTaxno = resolvedTax;
                    enrichedTax++;
                }
                if (!string.IsNullOrWhiteSpace(VomsisTransactionMapper.ResolveBranchName(tx)))
                    enrichedBranch++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dekont detayı alınamadı (id={Id})", tx.Id);
            }
        }

        _logger.LogInformation(
            "Dekont detayı: TCKN/VKN={TaxCount}/{Total}, şube={BranchCount}/{Total}",
            enrichedTax, candidates.Count, enrichedBranch, candidates.Count);
    }

    private static bool NeedsDetailEnrichment(VomsisTransaction tx)
    {
        if (!IsTryTransfer(tx))
            return false;

        var existingTax = VomsisTransactionMapper.ResolveCounterpartyTaxNo(tx);
        var branch = VomsisTransactionMapper.ResolveBranchName(tx);
        var hasBranch = !string.IsNullOrWhiteSpace(branch);
        if (string.IsNullOrWhiteSpace(existingTax) || !hasBranch)
            return true;

        // İl/ilçe yalnızca "İlçe / İl" formatındaki şube adlarından beklenir.
        if (branch!.Contains('/'))
        {
            var (city, district) = VomsisTransactionMapper.ParseCityDistrictFromBranchName(branch);
            if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(district))
                return true;
        }

        return false;
    }

    private static bool IsTryTransfer(VomsisTransaction tx)
    {
        var currency = (tx.FecName ?? "TRY").Trim().ToUpperInvariant();
        if (currency is "TL") currency = "TRY";
        if (!string.Equals(currency, "TRY", StringComparison.OrdinalIgnoreCase))
            return false;

        var type = (tx.Type ?? "").Trim().ToLowerInvariant()
            .Replace('ı', 'i').Replace('İ', 'i');
        return type.Contains("alacakli", StringComparison.Ordinal) ||
               type.Contains("borclu", StringComparison.Ordinal) ||
               type.Contains("havale", StringComparison.Ordinal) ||
               type.Contains("eft", StringComparison.Ordinal);
    }

    private static void MergeDetail(VomsisTransaction target, VomsisTransaction detail)
    {
        if (string.IsNullOrWhiteSpace(target.SenderTaxno)) target.SenderTaxno = detail.SenderTaxno;
        if (string.IsNullOrWhiteSpace(target.PayerTaxNo)) target.PayerTaxNo = detail.PayerTaxNo;
        if (string.IsNullOrWhiteSpace(target.RelatedVkn)) target.RelatedVkn = detail.RelatedVkn;
        if (string.IsNullOrWhiteSpace(target.IlgiliVkn)) target.IlgiliVkn = detail.IlgiliVkn;
        if (string.IsNullOrWhiteSpace(target.IlgiliTckn)) target.IlgiliTckn = detail.IlgiliTckn;
        if (string.IsNullOrWhiteSpace(target.RelatedTckn)) target.RelatedTckn = detail.RelatedTckn;
        if (string.IsNullOrWhiteSpace(target.SenderTckn)) target.SenderTckn = detail.SenderTckn;
        if (string.IsNullOrWhiteSpace(target.PayerTckn)) target.PayerTckn = detail.PayerTckn;
        if (string.IsNullOrWhiteSpace(target.TcKimlikNo)) target.TcKimlikNo = detail.TcKimlikNo;
        if (string.IsNullOrWhiteSpace(target.ReceiverTaxno)) target.ReceiverTaxno = detail.ReceiverTaxno;
        if (string.IsNullOrWhiteSpace(target.ReceiverTaxNoAlt)) target.ReceiverTaxNoAlt = detail.ReceiverTaxNoAlt;
        if (string.IsNullOrWhiteSpace(target.RelatedTitle)) target.RelatedTitle = detail.RelatedTitle;
        if (string.IsNullOrWhiteSpace(target.IlgiliUnvan)) target.IlgiliUnvan = detail.IlgiliUnvan;
        if (string.IsNullOrWhiteSpace(target.SenderTitle)) target.SenderTitle = detail.SenderTitle;
        if (string.IsNullOrWhiteSpace(target.SenderName)) target.SenderName = detail.SenderName;
        if (string.IsNullOrWhiteSpace(target.SenderIban)) target.SenderIban = detail.SenderIban;
        if (string.IsNullOrWhiteSpace(target.Description)) target.Description = detail.Description;
        if (string.IsNullOrWhiteSpace(target.BranchName)) target.BranchName = detail.BranchName;
        if (string.IsNullOrWhiteSpace(target.SubeAdi)) target.SubeAdi = detail.SubeAdi;
        if (string.IsNullOrWhiteSpace(target.SenderBranch)) target.SenderBranch = detail.SenderBranch;
        if (string.IsNullOrWhiteSpace(target.ReceiverBranch)) target.ReceiverBranch = detail.ReceiverBranch;
        if (string.IsNullOrWhiteSpace(target.OpponentTitle)) target.OpponentTitle = detail.OpponentTitle;
        if (string.IsNullOrWhiteSpace(target.OpponentTaxNo)) target.OpponentTaxNo = detail.OpponentTaxNo;
        if (string.IsNullOrWhiteSpace(target.IdentityNumber)) target.IdentityNumber = detail.IdentityNumber;
        if (string.IsNullOrWhiteSpace(target.SenderIdentityNumber)) target.SenderIdentityNumber = detail.SenderIdentityNumber;

        // Karşı taraf TCKN/VKN'ini tek alana yaz ki ERP import her zaman dolu gitsin.
        var resolvedTax = VomsisTransactionMapper.ResolveCounterpartyTaxNo(target);
        if (!string.IsNullOrWhiteSpace(resolvedTax))
            target.SenderTaxno = resolvedTax;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }
}

public sealed class VomsisSyncRunRequest
{
    public Guid? TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public string? ErpApiBaseUrl { get; set; }
}

public sealed class VomsisSyncRunResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int FetchedFromVomsis { get; set; }
    public int Received { get; set; }
    public int Imported { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedFilter { get; set; }
    public int DraftCreated { get; set; }
    public int PendingReview { get; set; }
    public int MissingTaxId { get; set; }
    public int NoCustomerMatch { get; set; }
    public string? SummaryMessage { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;

    public static VomsisSyncRunResult Fail(string message) => new() { Success = false, Error = message };
}

public sealed class VomsisTaxEnrichResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? CounterpartyTaxNo { get; set; }
    public string? CounterpartyName { get; set; }
    public string? BankBranchName { get; set; }
    public string? BankBranchCity { get; set; }
    public string? BankBranchDistrict { get; set; }

    public static VomsisTaxEnrichResult Fail(string message) => new() { Success = false, Message = message };
}
