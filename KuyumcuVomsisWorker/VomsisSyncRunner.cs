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
                SummaryMessage = "Vomsis'te seçilen tarih aralığında hareket bulunamadı.",
                PollIntervalMinutes = remote.PollIntervalMinutes
            };
            if (remote.ManualSyncRequestedUtc.HasValue)
                await _erp.CompleteManualSyncAsync(empty, ct);
            return empty;
        }

        var mapped = raw.Select(VomsisTransactionMapper.ToErp).ToList();
        var import = await _erp.ImportAsync(mapped, ct)
            ?? throw new InvalidOperationException("ERP import yanıtı boş.");

        return new VomsisSyncRunResult
        {
            Success = true,
            FetchedFromVomsis = raw.Count,
            Received = import.Received,
            Imported = import.Imported,
            SkippedDuplicate = import.SkippedDuplicate,
            SkippedFilter = import.SkippedFilter,
            DraftCreated = import.DraftCreated,
            PendingReview = import.PendingReview,
            MissingTaxId = import.MissingTaxId,
            NoCustomerMatch = import.NoCustomerMatch,
            SummaryMessage =
                $"Vomsis: {raw.Count} hareket, ERP'ye {import.Imported} kayıt " +
                $"(taslak: {import.DraftCreated}, bekleyen: {import.PendingReview}, atlandı: {import.SkippedFilter}).",
            PollIntervalMinutes = remote.PollIntervalMinutes
        };
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
