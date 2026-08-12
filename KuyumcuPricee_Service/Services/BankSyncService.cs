using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public interface IBankSyncService
{
    Task<BankSyncImportResult> ImportVomsisTransactionsAsync(
        Guid tenantId,
        Guid branchId,
        IReadOnlyList<VomsisTransactionImportDto> transactions,
        CancellationToken ct);

    Task<BankImportListResult> ListAsync(
        Guid tenantId,
        Guid branchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<BankImportActionResult> MatchAndCreateDraftAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        Guid customerId,
        CancellationToken ct);

    Task<BankImportActionResult> CreateDraftAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        CreateBankImportDraftOptions options,
        CancellationToken ct);

    Task<BankImportActionResult> RejectAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        string? reason,
        CancellationToken ct);

    Task<BankSyncPullResult> PullFromVomsisAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct);

    Task<BankImportTaxRefreshResult> RefreshVomsisTaxAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        CancellationToken ct);

    Task<BankImportTaxRefreshResult> ApplyVomsisEnrichmentAsync(
        Guid tenantId,
        Guid branchId,
        VomsisEnrichmentApplyDto enrichment,
        CancellationToken ct);
}

public sealed class VomsisEnrichmentApplyDto
{
    public long ExternalId { get; set; }
    public string? ExternalKey { get; set; }
    public string? SenderName { get; set; }
    public string? SenderTitle { get; set; }
    public string? SenderTaxNo { get; set; }
    public string? BankBranchName { get; set; }
    public string? BankBranchCity { get; set; }
    public string? BankBranchDistrict { get; set; }
}

public sealed class BankImportTaxRefreshResult
{
    public bool Success { get; set; }
    public bool Queued { get; set; }
    public string? Message { get; set; }
    public string? CounterpartyTaxNo { get; set; }
    public string? CounterpartyName { get; set; }
    public string? BankBranchName { get; set; }
    public string? BankBranchCity { get; set; }
    public string? BankBranchDistrict { get; set; }
    public string? Status { get; set; }
}

public sealed class BankSyncPullResult
{
    public int FetchedFromVomsis { get; set; }
    public int AfterAccountFilter { get; set; }
    public int Received { get; set; }
    public int Imported { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedFilter { get; set; }
    public int DraftCreated { get; set; }
    public int PendingReview { get; set; }
    public int MissingTaxId { get; set; }
    public int NoCustomerMatch { get; set; }
    public string? DetectedAccountIds { get; set; }
    public string? SummaryMessage { get; set; }
    public bool Queued { get; set; }
}

public sealed class BankSyncImportResult
{
    public int Received { get; set; }
    public int Imported { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedFilter { get; set; }
    public int DraftCreated { get; set; }
    public int PendingReview { get; set; }
    public int MissingTaxId { get; set; }
    public int NoCustomerMatch { get; set; }
}

public sealed class BankImportListResult
{
    public int Total { get; set; }
    public List<BankImportTransactionDto> Items { get; set; } = new();
}

public sealed class BankImportTransactionDto
{
    public Guid Id { get; set; }
    public long ExternalId { get; set; }
    public string ExternalKey { get; set; } = "";
    public int? VomsisAccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public string TransactionType { get; set; } = "";
    public string? Description { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyTaxNo { get; set; }
    public DateTime TransactionDateUtc { get; set; }
    public string Status { get; set; } = "";
    public string? StatusMessage { get; set; }
    public Guid? MatchedCustomerId { get; set; }
    public string? MatchedCustomerName { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? EInvoiceDocumentId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BankImportActionResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? EInvoiceDocumentId { get; set; }
    public string? Status { get; set; }
}

public sealed class CreateBankImportDraftOptions
{
    public Guid? CustomerId { get; set; }
    public Guid? SupplierId { get; set; }
    public string? ManualTaxNo { get; set; }
    public string? ManualBuyerName { get; set; }
    public bool UseNihaiTuketici { get; set; }
}

public sealed class VomsisTransactionImportDto
{
    public long ExternalId { get; set; }
    public string ExternalKey { get; set; } = "";
    public int? VomsisAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
    public string? Type { get; set; }
    public string? Description { get; set; }
    public DateTime? TransactionDateUtc { get; set; }
    public string? SenderName { get; set; }
    public string? SenderTitle { get; set; }
    public string? SenderTaxNo { get; set; }
    public string? SenderIban { get; set; }
    public string? BankBranchName { get; set; }
    public string? BankBranchCity { get; set; }
    public string? BankBranchDistrict { get; set; }
}

public sealed class BankSyncService : IBankSyncService
{
    private const string ProviderVomsis = "Vomsis";
    private readonly AppDbContext _db;
    private readonly IEInvoiceWorkflowService _workflow;
    private readonly VomsisApiClient _vomsis;
    private readonly VomsisWorkerProxyClient _workerProxy;
    private readonly IConfiguration _config;
    private readonly BankSyncSchemaEnsurer _schema;
    private readonly EInvoiceProfileSchemaEnsurer _einvoiceSchema;
    private readonly ExpenseSlipSchemaEnsurer _expenseSlipSchema;
    private readonly IBankSyncProfileService _profileService;
    private readonly IWebHostEnvironment _env;
    private readonly ICounterpartyTaxResolver _taxResolver;
    private readonly ExchangeRateService _rates;

    public BankSyncService(
        AppDbContext db,
        IEInvoiceWorkflowService workflow,
        VomsisApiClient vomsis,
        VomsisWorkerProxyClient workerProxy,
        IConfiguration config,
        BankSyncSchemaEnsurer schema,
        EInvoiceProfileSchemaEnsurer einvoiceSchema,
        ExpenseSlipSchemaEnsurer expenseSlipSchema,
        IBankSyncProfileService profileService,
        IWebHostEnvironment env,
        ICounterpartyTaxResolver taxResolver,
        ExchangeRateService rates)
    {
        _db = db;
        _workflow = workflow;
        _vomsis = vomsis;
        _workerProxy = workerProxy;
        _config = config;
        _schema = schema;
        _einvoiceSchema = einvoiceSchema;
        _expenseSlipSchema = expenseSlipSchema;
        _profileService = profileService;
        _env = env;
        _taxResolver = taxResolver;
        _rates = rates;
    }

    public async Task<BankSyncPullResult> PullFromVomsisAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        await _einvoiceSchema.EnsureAsync(ct);
        var profile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("Vomsis ayarları bulunamadı. Önce Vomsis Ayarları sekmesinden kaydedin.");

        if (!profile.IsEnabled)
            throw new InvalidOperationException("Banka sync profili devre dışı.");
        if (string.IsNullOrWhiteSpace(profile.VomsisAppKey) || string.IsNullOrWhiteSpace(profile.VomsisAppSecret))
            throw new InvalidOperationException("Vomsis App Key / Secret eksik.");

        var preferWorker = _config.GetValue("BankSync:PreferWorkerForVomsisPull", true);
        var useWorker = ShouldUseWorkerProxy(profile, preferWorker);
        if (useWorker)
        {
            if (_config.GetValue("BankSync:UseWorkerHttpProxy", false) && _workerProxy.IsConfigured)
            {
                try
                {
                    var erpTarget = ResolveErpBaseUrlForWorker(profile);
                    return await _workerProxy.TriggerSyncAsync(tenantId, branchId, erpTarget, ct);
                }
                catch (Exception ex) when (IsWorkerConnectivityError(ex))
                {
                    // HTTP worker yok — kuyruk moduna düş.
                }
            }

            return await QueueWorkerSyncAndWaitAsync(tenantId, branchId, ct);
        }

        try
        {
            return await PullFromVomsisDirectAsync(tenantId, branchId, profile, ct);
        }
        catch (InvalidOperationException ex) when (VomsisIpErrorHelper.IsIpBlockedError(ex.Message))
        {
            if (ShouldFallbackToWorker(profile, preferWorker))
            {
                var fallbackTarget = ResolveProductionErpBaseUrl(profile);
                if (!string.IsNullOrWhiteSpace(fallbackTarget) && !IsLocalhostUrl(fallbackTarget))
                    return await QueueWorkerSyncAndWaitAsync(tenantId, branchId, ct);
            }

            throw new InvalidOperationException(VomsisIpErrorHelper.BuildBlockedMessage(ex.Message), ex);
        }
    }

    private async Task EnqueueMissingTaxRowsForWorkerAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        // Atlandı (komisyon/BSMV) kuyruğa alınmaz — import'u geciktirir.
        // Yalnızca gerçekten TCKN/şube eksik anlamlı satırlar.
        var missingIds = await _db.BankImportTransactions.AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.Provider == ProviderVomsis &&
                !x.IsDeleted &&
                x.ExternalId > 0 &&
                x.Status != BankImportStatuses.Skipped &&
                x.Status != BankImportStatuses.Rejected &&
                x.Amount >= 1m &&
                (x.Status == BankImportStatuses.MissingTaxId ||
                 x.Status == BankImportStatuses.NoCustomerMatch ||
                 x.Status == BankImportStatuses.Pending ||
                 x.Status == BankImportStatuses.DraftCreated ||
                 x.Status == BankImportStatuses.AutoSendQueued) &&
                ((x.CounterpartyTaxNo == null || x.CounterpartyTaxNo == "") ||
                 (x.BankBranchName == null || x.BankBranchName == "")))
            .OrderByDescending(x => x.TransactionDateUtc)
            .Select(x => x.ExternalId)
            .Take(10)
            .ToListAsync(ct);

        foreach (var id in missingIds)
            await _profileService.EnqueuePendingEnrichAsync(tenantId, branchId, id, ct);
    }

    private async Task<BankSyncPullResult> QueueWorkerSyncAndWaitAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        var requestedAt = DateTime.UtcNow;
        await EnqueueMissingTaxRowsForWorkerAsync(tenantId, branchId, ct);
        await _profileService.RequestManualSyncAsync(tenantId, branchId, ct);

        var blockingWait = _config.GetValue("BankSync:WorkerQueueBlockingWait", false);
        if (!blockingWait)
        {
            return new BankSyncPullResult
            {
                Queued = true,
                SummaryMessage =
                    "Vomsis senkron talebi Azure VM worker'a iletildi. " +
                    "Worker işlemi tamamlanınca liste otomatik güncellenecek (yaklaşık 1-2 dakika)."
            };
        }

        var timeoutSec = Math.Clamp(_config.GetValue("BankSync:WorkerQueueWaitSeconds", 120), 30, 300);
        var pollSec = Math.Clamp(_config.GetValue("BankSync:WorkerQueuePollSeconds", 5), 2, 15);

        while ((DateTime.UtcNow - requestedAt).TotalSeconds < timeoutSec)
        {
            await Task.Delay(TimeSpan.FromSeconds(pollSec), ct);

            var synced = await _db.BankSyncProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
            if (synced is null)
                continue;

            if (synced.ManualSyncRequestedUtc is null &&
                synced.LastWorkerSyncUtc.HasValue &&
                synced.LastWorkerSyncUtc.Value >= requestedAt.AddSeconds(-5))
            {
                return new BankSyncPullResult
                {
                    FetchedFromVomsis = synced.LastWorkerSyncFetched ?? 0,
                    Imported = synced.LastWorkerSyncImported ?? 0,
                    SummaryMessage = synced.LastWorkerSyncMessage ??
                        $"Vomsis worker: {synced.LastWorkerSyncFetched ?? 0} hareket işlendi."
                };
            }
        }

        throw new InvalidOperationException(
            "Vomsis senkron talebi Azure VM worker'a iletildi ancak belirlenen sürede tamamlanmadı. " +
            "VM'de KuyumcuVomsisWorker servisinin çalıştığını doğrulayın (5080 portu gerekmez). " +
            "Birkaç dakika bekleyip Yenile ile listeyi kontrol edin.");
    }

    private bool ShouldUseWorkerProxy(BankSyncProfile profile, bool preferWorker)
    {
        if (!preferWorker || !_workerProxy.IsConfigured)
            return false;
        if (!_config.GetValue("BankSync:UseWorkerProxy", true))
            return false;
        if (_env.IsDevelopment())
            return false;
        if (IsLocalhostUrl(profile.ErpApiBaseUrl))
            return false;

        var erpTarget = ResolveErpBaseUrlForWorker(profile);
        return !IsLocalhostUrl(erpTarget ?? "");
    }

    private bool ShouldFallbackToWorker(BankSyncProfile profile, bool preferWorker)
    {
        if (!preferWorker || !_workerProxy.IsConfigured)
            return false;
        if (!_config.GetValue("BankSync:UseWorkerProxy", true))
            return false;
        if (_env.IsDevelopment())
            return false;
        if (IsLocalhostUrl(profile.ErpApiBaseUrl))
            return false;
        return true;
    }

    private async Task<BankSyncPullResult> TriggerWorkerSyncAsync(
        Guid tenantId,
        Guid branchId,
        string? erpApiBaseUrl,
        CancellationToken ct)
    {
        try
        {
            return await _workerProxy.TriggerSyncAsync(tenantId, branchId, erpApiBaseUrl, ct);
        }
        catch (Exception ex) when (IsWorkerConnectivityError(ex))
        {
            var workerUrl = (_config["BankSync:WorkerTriggerUrl"] ?? "172.213.185.78:5080").Trim();
            throw new InvalidOperationException(
                $"Vomsis worker erişilemiyor ({workerUrl}). " +
                "Azure VM'de KuyumcuVomsisWorker servisinin çalıştığını ve NSG'de 5080/TCP portunun açık olduğunu doğrulayın. " +
                "Worker henüz kurulmadıysa birkaç dakika bekleyip Yenile ile listeyi güncelleyin (arka plan senkronu). " +
                "Detay: " + ex.Message, ex);
        }
    }

    private static bool IsWorkerConnectivityError(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException or TaskCanceledException or TimeoutException)
                return true;
            var msg = cur.Message;
            if (msg.Contains("172.213.185.78", StringComparison.Ordinal) ||
                msg.Contains(":5080", StringComparison.Ordinal) ||
                msg.Contains("erişilemeyen bir ağ", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("yanıt vermedi", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private string? ResolveProductionErpBaseUrl(BankSyncProfile profile)
    {
        var fromConfig = (_config["BankSync:DefaultErpApiBaseUrl"] ?? _config["Hosting:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(fromConfig) && !IsLocalhostUrl(fromConfig))
            return fromConfig;

        var fromProfile = profile.ErpApiBaseUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(fromProfile) && !IsLocalhostUrl(fromProfile))
            return fromProfile;

        return null;
    }

    private static bool IsVomsisIpError(string message)
        => VomsisIpErrorHelper.IsIpBlockedError(message);

    private string? ResolveErpBaseUrlForWorker(BankSyncProfile profile)
    {
        var fromProfile = profile.ErpApiBaseUrl?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(fromProfile) && !IsLocalhostUrl(fromProfile))
            return fromProfile;

        var fromConfig = (_config["BankSync:DefaultErpApiBaseUrl"] ?? _config["Hosting:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(fromConfig) ? fromProfile : fromConfig;
    }

    private static bool IsLocalhostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback ||
               string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BankSyncPullResult> PullFromVomsisDirectAsync(
        Guid tenantId,
        Guid branchId,
        BankSyncProfile profile,
        CancellationToken ct)
    {
        _vomsis.Configure(profile.VomsisAppKey!, profile.VomsisAppSecret!);

        var lookbackDays = Math.Clamp(profile.LookbackDays, 1, 30);
        var endUtc = DateTime.UtcNow;
        var beginUtc = endUtc.AddDays(-lookbackDays);

        var raw = await _vomsis.GetTransactionsAsync(beginUtc, endUtc, ct);
        var allowed = ParseAccountIds(profile.AllowedAccountIds).ToHashSet();
        var detectedAccountIds = raw
            .Select(tx => tx.BankAccountId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var mapped = raw.Select(VomsisTransactionMapper.ToImportDto).ToList();

        var import = await ImportVomsisTransactionsAsync(tenantId, branchId, mapped, ct);
        var eligibleCount = mapped.Count(tx =>
            allowed.Count == 0 || (tx.VomsisAccountId.HasValue && allowed.Contains(tx.VomsisAccountId.Value)));
        var pull = new BankSyncPullResult
        {
            FetchedFromVomsis = raw.Count,
            AfterAccountFilter = eligibleCount,
            Received = import.Received,
            Imported = import.Imported,
            SkippedDuplicate = import.SkippedDuplicate,
            SkippedFilter = import.SkippedFilter,
            DraftCreated = import.DraftCreated,
            PendingReview = import.PendingReview,
            MissingTaxId = import.MissingTaxId,
            NoCustomerMatch = import.NoCustomerMatch,
            DetectedAccountIds = detectedAccountIds.Length == 0 ? null : string.Join(", ", detectedAccountIds),
            SummaryMessage = BuildPullSummary(raw.Count, eligibleCount, allowed, detectedAccountIds, import)
        };
        return pull;
    }

    private static string BuildPullSummary(
        int fetched,
        int eligibleForProcessing,
        HashSet<int> allowedAccounts,
        int[] detectedAccountIds,
        BankSyncImportResult import)
    {
        if (fetched == 0)
            return "Vomsis'te seçilen tarih aralığında hareket bulunamadı.";

        if (import.Imported == 0 && import.SkippedDuplicate == fetched)
            return $"{fetched} hareket zaten ERP'de kayıtlı (mükerrer).";

        if (eligibleForProcessing == 0 && import.Imported > 0)
        {
            return $"Vomsis'ten {fetched} hareket ERP'ye kaydedildi ({import.SkippedFilter} adet atlandı — TL hesap dışı/EUR). " +
                   "Listede 'Atlandı' olarak görünür; e-fatura taslağı yalnızca TL gelen havaleler için oluşturulur.";
        }

        if (eligibleForProcessing == 0)
        {
            var allowedText = string.Join(", ", allowedAccounts.OrderBy(x => x));
            var detectedText = detectedAccountIds.Length == 0 ? "bilinmiyor" : string.Join(", ", detectedAccountIds);
            return $"Vomsis'ten {fetched} hareket geldi; işlenecek TL hesap ({allowedText}) hareketi yok. " +
                   $"Gelen hareketler hesap {detectedText} üzerinde.";
        }

        return $"Vomsis: {fetched} hareket, ERP'ye {import.Imported} kayıt " +
               $"(taslak: {import.DraftCreated}, bekleyen: {import.PendingReview}, atlandı: {import.SkippedFilter}).";
    }

    private static int[] ParseAccountIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [46];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .ToArray();
    }

    public async Task<BankSyncImportResult> ImportVomsisTransactionsAsync(
        Guid tenantId,
        Guid branchId,
        IReadOnlyList<VomsisTransactionImportDto> transactions,
        CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        await _einvoiceSchema.EnsureAsync(ct);
        var result = new BankSyncImportResult { Received = transactions?.Count ?? 0 };
        if (transactions is null || transactions.Count == 0)
            return result;

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId, ct);

        var bankProfile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        var allowedAccounts = ParseAccountIds(bankProfile?.AllowedAccountIds).ToHashSet();

        var customerRowsCache = await _db.Customers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.CariTip == 0)
            .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
            .ToListAsync(ct);

        var externalKeys = transactions
            .Where(tx => !string.IsNullOrWhiteSpace(tx.ExternalKey))
            .Select(tx => tx.ExternalKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // externalKeys.Contains → SQL Server'da OPENJSON ... WITH üretip batch hatasına düşebilir.
        // Tarih penceresini geniş tut: aksi halde mevcut MissingTaxId satırları bulunamaz, TCKN yazılmaz.
        var minTransactionDate = DateTime.UtcNow.AddDays(-90);
        var externalKeySet = externalKeys.ToHashSet(StringComparer.Ordinal);
        var externalIdSet = transactions.Select(x => x.ExternalId).Where(x => x > 0).ToHashSet();
        var recentRows = externalKeySet.Count == 0 && externalIdSet.Count == 0
            ? new List<BankImportTransaction>()
            : await _db.BankImportTransactions
                .Where(x => x.TenantId == tenantId &&
                            x.BranchId == branchId &&
                            x.Provider == ProviderVomsis &&
                            !x.IsDeleted &&
                            x.TransactionDateUtc >= minTransactionDate)
                .ToListAsync(ct);
        var matchedRows = recentRows
            .Where(x => externalKeySet.Contains(x.ExternalKey) || externalIdSet.Contains(x.ExternalId))
            .ToList();
        var existingByKey = matchedRows
            .Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey))
            .GroupBy(x => x.ExternalKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var existingByExternalId = matchedRows
            .GroupBy(x => x.ExternalId)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var tx in transactions)
        {
            if (string.IsNullOrWhiteSpace(tx.ExternalKey))
            {
                result.SkippedFilter++;
                continue;
            }

            existingByKey.TryGetValue(tx.ExternalKey, out var existingEntity);
            if (existingEntity is null)
                existingByExternalId.TryGetValue(tx.ExternalId, out existingEntity);
            if (existingEntity is not null)
            {
                ApplyVomsisTransactionDate(existingEntity, tx);
                var existingType = NormalizeVomsisTxType(existingEntity.TransactionType ?? tx.Type);
                var existingCurrency = NormalizeCurrency(existingEntity.Currency ?? tx.Currency);
                var isOutgoingExisting =
                    string.Equals(existingType, "borclu", StringComparison.OrdinalIgnoreCase) &&
                    IsTryCurrency(existingCurrency);

                if (ShouldAttemptExistingUpgrade(existingEntity, bankProfile))
                {
                    var upgraded = isOutgoingExisting
                        ? await TryUpgradeExistingOutgoingAsync(
                            existingEntity, tx, tenantId, branchId, bankProfile, result, ct)
                        : await TryUpgradeExistingIncomingAsync(
                            existingEntity, tx, tenantId, branchId, profile, bankProfile, result, ct);
                    if (upgraded)
                        result.Imported++;
                }
                result.SkippedDuplicate++;
                continue;
            }

            var currency = NormalizeCurrency(tx.Currency);
            var txType = (tx.Type ?? "").Trim().ToLowerInvariant();
            var amount = decimal.Round(Math.Abs(tx.Amount), 2, MidpointRounding.AwayFromZero);

            if (ShouldSkipForAccountFilter(tx.VomsisAccountId, allowedAccounts, txType, currency))
            {
                var skippedAccount = CreateEntity(tenantId, branchId, tx, currency, txType, amount);
                ApplyCounterpartyFields(skippedAccount, tx);
                skippedAccount.Status = BankImportStatuses.Skipped;
                skippedAccount.StatusMessage = tx.VomsisAccountId.HasValue
                    ? $"Hesap {tx.VomsisAccountId} izin dışı (yalnızca TL hesap {string.Join(",", allowedAccounts.OrderBy(x => x))})."
                    : "Hesap bilgisi yok; TL hesap filtresi uygulanamadı.";
                _db.BankImportTransactions.Add(skippedAccount);
                result.SkippedFilter++;
                result.Imported++;
                continue;
            }

            if (string.Equals(txType, "borclu", StringComparison.OrdinalIgnoreCase) && IsTryCurrency(currency))
            {
                await ImportOutgoingTransactionAsync(
                    tenantId, branchId, bankProfile, tx, currency, txType, amount, result, ct);
                continue;
            }

            if (!IsQualifyingIncomingTransfer(txType, amount, currency, tx.Description))
            {
                var skipped = CreateEntity(tenantId, branchId, tx, currency, txType, amount);
                ApplyCounterpartyFields(skipped, tx);
                skipped.Status = BankImportStatuses.Skipped;
                skipped.StatusMessage = IsTryCurrency(currency)
                    ? "Filtre dışı hareket (TL gelen havale değil)."
                    : $"Filtre dışı hareket ({currency} — yalnızca TL gelen havale işlenir).";
                _db.BankImportTransactions.Add(skipped);
                result.SkippedFilter++;
                result.Imported++;
                continue;
            }

            // Yalnızca işlenecek TL gelen havaleler için dekont detayı çekilir (toplu import hızını korur).
            var enrichedTx = await EnsureTaxEnrichedAsync(tx, bankProfile, ct);

            var entity = CreateEntity(tenantId, branchId, enrichedTx, currency, txType, amount);
            ApplyCounterpartyFields(entity, enrichedTx);

            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                entity.CounterpartyName,
                entity.CounterpartyTaxNo,
                entity.CounterpartyIban,
                entity.Description,
                IsIncomingTransfer: true,
                AllowNihaiTuketici: false), ct);

            if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.TaxNo) || IsPlaceholderTax(resolved.TaxNo))
            {
                entity.Status = BankImportStatuses.MissingTaxId;
                entity.StatusMessage = resolved.Message ?? "TCKN/VKN bulunamadı.";
                if (resolved.CustomerId.HasValue)
                    entity.MatchedCustomerId = resolved.CustomerId;
                result.MissingTaxId++;
                result.PendingReview++;
                _db.BankImportTransactions.Add(entity);
                result.Imported++;
                continue;
            }

            entity.CounterpartyTaxNo = resolved.TaxNo;
            if (!string.IsNullOrWhiteSpace(resolved.DisplayName) || !string.IsNullOrWhiteSpace(enrichedTx.SenderTitle))
            {
                entity.CounterpartyName = BankMovementParser.PreferCounterpartyDisplayName(
                    enrichedTx.SenderTitle ?? enrichedTx.SenderName,
                    entity.CounterpartyName,
                    resolved.DisplayName,
                    resolved.TaxNo) ?? entity.CounterpartyName;
            }

            CustomerMatchRow? customer = null;
            if (resolved.CustomerId.HasValue)
            {
                customer = await _db.Customers.AsNoTracking()
                    .Where(x => x.Id == resolved.CustomerId.Value && !x.IsDeleted)
                    .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                customer = ResolveCustomer(customerRowsCache, entity.CounterpartyName, entity.CounterpartyTaxNo);
            }

            if (customer is not null)
            {
                entity.MatchedCustomerId = customer.Id;
                var taxNo = entity.CounterpartyTaxNo!;

                if (ShouldAutoSendIncoming(bankProfile, amount))
                {
                    var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                    if (workmanshipError is not null)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = workmanshipError;
                        result.PendingReview++;
                    }
                    else
                    {
                        try
                        {
                            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                                tenantId, branchId, customer, taxNo, amount, entity, enrichedTx, bankProfile, ct);
                            await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                            await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                            entity.Status = BankImportStatuses.AutoSendQueued;
                            entity.StatusMessage = resolved.IsNihaiTuketici
                                ? $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL)."
                                : $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL, {resolved.Source}).";
                            entity.InvoiceId = invoiceId;
                            entity.EInvoiceDocumentId = documentId;
                            result.DraftCreated++;
                        }
                        catch (Exception ex)
                        {
                            entity.Status = BankImportStatuses.Pending;
                            entity.StatusMessage = "Otomatik gönderim başarısız: " + ex.Message;
                            result.PendingReview++;
                        }
                    }
                }
                else if (ShouldCreateIncomingBankDraft(profile, bankProfile, amount))
                {
                    var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                    if (workmanshipError is not null)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = workmanshipError;
                        result.PendingReview++;
                    }
                    else
                    {
                        try
                        {
                            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                                tenantId, branchId, customer, taxNo, amount, entity, enrichedTx, bankProfile, ct);
                            entity.Status = BankImportStatuses.DraftCreated;
                            entity.StatusMessage = resolved.IsNihaiTuketici
                                ? "Otomatik taslak (nihai tüketici)."
                                : $"Otomatik taslak oluşturuldu ({resolved.Source}).";
                            entity.InvoiceId = invoiceId;
                            entity.EInvoiceDocumentId = documentId;
                            result.DraftCreated++;
                        }
                        catch (Exception ex)
                        {
                            entity.Status = BankImportStatuses.Pending;
                            entity.StatusMessage = "Taslak oluşturulamadı: " + ex.Message;
                            result.PendingReview++;
                        }
                    }
                }
                else
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = IncomingBelowThresholdMessage(bankProfile);
                    result.PendingReview++;
                }
            }
            else if (resolved.IsNihaiTuketici)
            {
                if (ShouldAutoSendIncoming(bankProfile, amount))
                {
                    var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                    if (workmanshipError is not null)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = workmanshipError;
                        result.PendingReview++;
                    }
                    else
                    {
                        try
                        {
                            var nihaiCustomer = await EnsureNihaiCustomerAsync(tenantId, branchId, ct);
                            entity.MatchedCustomerId = nihaiCustomer.Id;
                            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                                tenantId, branchId, nihaiCustomer, resolved.TaxNo!, amount, entity, enrichedTx, bankProfile, ct);
                            await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                            await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                            entity.Status = BankImportStatuses.AutoSendQueued;
                            entity.StatusMessage = $"Otomatik e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL, nihai tüketici).";
                            entity.InvoiceId = invoiceId;
                            entity.EInvoiceDocumentId = documentId;
                            result.DraftCreated++;
                        }
                        catch (Exception ex)
                        {
                            entity.Status = BankImportStatuses.Pending;
                            entity.StatusMessage = "Nihai tüketici taslağı oluşturulamadı: " + ex.Message;
                            result.PendingReview++;
                        }
                    }
                }
                else if (ShouldCreateIncomingBankDraft(profile, bankProfile, amount))
                {
                    var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                    if (workmanshipError is not null)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = workmanshipError;
                        result.PendingReview++;
                    }
                    else
                    {
                        try
                        {
                            var nihaiCustomer = await EnsureNihaiCustomerAsync(tenantId, branchId, ct);
                            entity.MatchedCustomerId = nihaiCustomer.Id;
                            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                                tenantId, branchId, nihaiCustomer, resolved.TaxNo!, amount, entity, enrichedTx, bankProfile, ct);
                            entity.Status = BankImportStatuses.DraftCreated;
                            entity.StatusMessage = "Otomatik taslak (nihai tüketici).";
                            entity.InvoiceId = invoiceId;
                            entity.EInvoiceDocumentId = documentId;
                            result.DraftCreated++;
                        }
                        catch (Exception ex)
                        {
                            entity.Status = BankImportStatuses.Pending;
                            entity.StatusMessage = "Nihai tüketici taslağı oluşturulamadı: " + ex.Message;
                            result.PendingReview++;
                        }
                    }
                }
                else
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = IncomingBelowThresholdMessage(bankProfile);
                    result.PendingReview++;
                }
            }
            else
            {
                if (ShouldAutoSendIncoming(bankProfile, amount) &&
                    IsValidTaxNo(NormalizeTaxNo(entity.CounterpartyTaxNo)))
                {
                    var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                    if (workmanshipError is not null)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = workmanshipError;
                        result.PendingReview++;
                    }
                    else
                    {
                        try
                        {
                            var counterparty = new CustomerMatchRow(
                                Guid.Empty,
                                entity.CounterpartyName ?? resolved.DisplayName ?? "Karşı Taraf",
                                entity.CounterpartyTaxNo!,
                                null,
                                entity.BankBranchCity,
                                entity.BankBranchDistrict,
                                null);
                            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                                tenantId, branchId, counterparty, entity.CounterpartyTaxNo!, amount, entity, enrichedTx, bankProfile, ct);
                            await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                            await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                            entity.Status = BankImportStatuses.AutoSendQueued;
                            entity.StatusMessage =
                                $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL, cari eşleşmesiz).";
                            entity.InvoiceId = invoiceId;
                            entity.EInvoiceDocumentId = documentId;
                            result.DraftCreated++;
                        }
                        catch (Exception ex)
                        {
                            entity.Status = BankImportStatuses.Pending;
                            entity.StatusMessage = "Otomatik gönderim başarısız: " + ex.Message;
                            result.PendingReview++;
                        }
                    }
                }
                else
                {
                    entity.Status = BankImportStatuses.NoCustomerMatch;
                    entity.StatusMessage = resolved.Message ?? "Karşı taraf cari kayıtlarda bulunamadı.";
                    result.NoCustomerMatch++;
                    result.PendingReview++;
                }
            }

            _db.BankImportTransactions.Add(entity);
            result.Imported++;
        }

        await _db.SaveChangesAsync(ct);

        // Worker her import sonrası sync zamanını güncellesin (manuel istek olmasa da).
        await _profileService.CompleteManualSyncAsync(tenantId, branchId, new BankSyncPullResult
        {
            FetchedFromVomsis = result.Received,
            Received = result.Received,
            Imported = result.Imported,
            SkippedDuplicate = result.SkippedDuplicate,
            SkippedFilter = result.SkippedFilter,
            DraftCreated = result.DraftCreated,
            PendingReview = result.PendingReview,
            SummaryMessage =
                $"Vomsis worker: {result.Received} hareket, ERP'ye {result.Imported} kayıt " +
                $"(taslak: {result.DraftCreated}, bekleyen: {result.PendingReview})."
        }, ct);

        return result;
    }

    public async Task<BankImportListResult> ListAsync(
        Guid tenantId,
        Guid branchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var q = _db.BankImportTransactions
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status.Trim());

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(x => x.TransactionDateUtc)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.ExternalId,
                x.ExternalKey,
                x.VomsisAccountId,
                x.Amount,
                x.Currency,
                x.TransactionType,
                x.Description,
                x.CounterpartyName,
                x.CounterpartyTaxNo,
                x.TransactionDateUtc,
                x.Status,
                x.StatusMessage,
                x.MatchedCustomerId,
                x.InvoiceId,
                x.EInvoiceDocumentId,
                x.CreatedAt
            })
            .ToListAsync(ct);

        var customerIds = rows
            .Where(x => x.MatchedCustomerId.HasValue)
            .Select(x => x.MatchedCustomerId!.Value)
            .Distinct()
            .ToList();
        var customerIdSet = customerIds.ToHashSet();
        var customerNames = customerIdSet.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _db.Customers.AsNoTracking()
                .Where(c => c.TenantId == tenantId && c.BranchId == branchId && !c.IsDeleted)
                .Select(c => new { c.Id, c.FullName })
                .ToListAsync(ct))
                .Where(c => customerIdSet.Contains(c.Id))
                .ToDictionary(c => c.Id, c => c.FullName ?? "");

        return new BankImportListResult
        {
            Total = total,
            Items = rows.Select(x => new BankImportTransactionDto
            {
                Id = x.Id,
                ExternalId = x.ExternalId,
                ExternalKey = x.ExternalKey,
                VomsisAccountId = x.VomsisAccountId,
                Amount = x.Amount,
                Currency = x.Currency,
                TransactionType = x.TransactionType,
                Description = x.Description,
                CounterpartyName = x.CounterpartyName,
                CounterpartyTaxNo = x.CounterpartyTaxNo,
                TransactionDateUtc = x.TransactionDateUtc,
                Status = x.Status,
                StatusMessage = x.StatusMessage,
                MatchedCustomerId = x.MatchedCustomerId,
                MatchedCustomerName = x.MatchedCustomerId.HasValue &&
                                      customerNames.TryGetValue(x.MatchedCustomerId.Value, out var name)
                    ? name
                    : null,
                InvoiceId = x.InvoiceId,
                EInvoiceDocumentId = x.EInvoiceDocumentId,
                CreatedAt = x.CreatedAt
            }).ToList()
        };
    }

    public async Task<BankImportActionResult> MatchAndCreateDraftAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        Guid customerId,
        CancellationToken ct)
    {
        var tx = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == transactionId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId, ct);
        if (tx is null)
            return Fail("Hareket bulunamadı.");

        if (HasBankImportDraft(tx))
            return Fail(IsOutgoingTryTransfer(tx)
                ? "Bu hareket için zaten gider pusulası taslağı oluşturulmuş."
                : "Bu hareket için zaten taslak oluşturulmuş.");

        if (tx.Status == BankImportStatuses.Rejected)
            return Fail("Reddedilmiş hareket için taslak oluşturulamaz.");

        if (IsOutgoingTryTransfer(tx))
            return await CreateOutgoingExpenseSlipDraftFromImportAsync(tenantId, branchId, tx, customerId, null, ct);

        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id == customerId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                !x.IsDeleted, ct);
        if (customer is null)
            return Fail("Müşteri bulunamadı.");

        var taxNo = NormalizeTaxNo(customer.NationalId);
        if (!CounterpartyTaxResolverService.IsAcceptableTaxNo(taxNo))
        {
            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                tx.CounterpartyName,
                tx.CounterpartyTaxNo,
                tx.CounterpartyIban,
                tx.Description,
                IsIncomingTransfer: true,
                AllowNihaiTuketici: true), ct);
            taxNo = resolved.TaxNo ?? NormalizeTaxNo(tx.CounterpartyTaxNo);
            if (!CounterpartyTaxResolverService.IsAcceptableTaxNo(taxNo))
            {
                tx.MatchedCustomerId = customer.Id;
                tx.Status = BankImportStatuses.MissingTaxId;
                tx.StatusMessage = resolved.Message ?? "Müşteri seçildi ancak geçerli TCKN/VKN bulunamadı.";
                await _db.SaveChangesAsync(ct);
                return new BankImportActionResult
                {
                    Success = false,
                    Message = tx.StatusMessage,
                    Status = tx.Status
                };
            }
        }

        try
        {
            var bankProfile = await LoadBankSyncProfileAsync(tenantId, branchId, ct);
            var row = new CustomerMatchRow(customer.Id, customer.FullName, customer.NationalId, customer.Address, customer.City, customer.District, customer.Email);
            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                tenantId, branchId, row, taxNo, tx.Amount, tx, null, bankProfile, ct);
            tx.MatchedCustomerId = customer.Id;
            tx.CounterpartyTaxNo = taxNo;
            tx.InvoiceId = invoiceId;
            tx.EInvoiceDocumentId = documentId;

            if (ShouldAutoSendIncoming(bankProfile, tx.Amount))
            {
                await TryPatchLinkedCollectionDraftAddressAsync(tenantId, tx, bankProfile, ct);
                await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                tx.Status = BankImportStatuses.AutoSendQueued;
                tx.StatusMessage =
                    $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL).";
            }
            else
            {
                tx.Status = BankImportStatuses.DraftCreated;
                tx.StatusMessage = "Manuel eşleştirme ile taslak oluşturuldu.";
            }

            await _db.SaveChangesAsync(ct);
            return new BankImportActionResult
            {
                Success = true,
                InvoiceId = invoiceId,
                EInvoiceDocumentId = documentId,
                Status = tx.Status
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<BankImportActionResult> CreateDraftAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        CreateBankImportDraftOptions options,
        CancellationToken ct)
    {
        options ??= new CreateBankImportDraftOptions();

        var tx = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == transactionId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId, ct);
        if (tx is null)
            return Fail("Hareket bulunamadı.");

        if (HasBankImportDraft(tx))
            return Fail(IsOutgoingTryTransfer(tx)
                ? "Bu hareket için zaten gider pusulası taslağı oluşturulmuş."
                : "Bu hareket için zaten taslak oluşturulmuş.");

        if (tx.Status == BankImportStatuses.Rejected)
            return Fail("Reddedilmiş hareket için taslak oluşturulamaz.");

        if (IsOutgoingTryTransfer(tx))
            return await CreateOutgoingExpenseSlipDraftFromImportAsync(
                tenantId,
                branchId,
                tx,
                options.CustomerId,
                options,
                ct);

        string taxNo;
        Guid? customerId = null;
        Guid? supplierId = null;
        string buyerName = tx.CounterpartyName ?? "";
        string? address = null;
        string? city = null;
        string? district = null;
        string? email = null;

        if (options.UseNihaiTuketici)
        {
            taxNo = CounterpartyTaxResolverService.NihaiTuketiciTckn;
            var nihai = await EnsureNihaiCustomerAsync(tenantId, branchId, ct);
            customerId = nihai.Id;
            buyerName = nihai.FullName;
            address = nihai.Address;
            city = nihai.City;
            district = nihai.District;
            email = nihai.Email;
        }
        else if (options.CustomerId is Guid cid && cid != Guid.Empty)
        {
            var customer = await _db.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.Id == cid &&
                    x.TenantId == tenantId &&
                    x.BranchId == branchId &&
                    !x.IsDeleted, ct);
            if (customer is null)
                return Fail("Müşteri bulunamadı.");

            customerId = customer.Id;
            buyerName = customer.FullName;
            address = customer.Address;
            city = customer.City;
            district = customer.District;
            email = customer.Email;
            taxNo = NormalizeTaxNo(CoalesceText(options.ManualTaxNo, customer.NationalId, tx.CounterpartyTaxNo));
            if (!IsValidTaxNo(taxNo))
            {
                var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                    tenantId, branchId, tx.CounterpartyName, tx.CounterpartyTaxNo, tx.CounterpartyIban,
                    tx.Description, IsIncomingTransfer: true, AllowNihaiTuketici: false), ct);
                taxNo = NormalizeTaxNo(resolved.TaxNo ?? tx.CounterpartyTaxNo);
            }
            if (!IsValidTaxNo(taxNo))
                return Fail("Müşteri seçildi ancak geçerli TCKN/VKN bulunamadı.");
        }
        else if (options.SupplierId is Guid sid && sid != Guid.Empty)
        {
            var supplier = await _db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.Id == sid &&
                    x.TenantId == tenantId &&
                    !x.IsDeleted &&
                    x.IsActive, ct);
            if (supplier is null)
                return Fail("Tedarikçi bulunamadı.");

            supplierId = supplier.Id;
            buyerName = supplier.CompanyName;
            address = supplier.Address;
            city = supplier.City;
            district = supplier.District;
            email = supplier.Email;
            taxNo = NormalizeTaxNo(CoalesceText(options.ManualTaxNo, supplier.TaxNumber, tx.CounterpartyTaxNo));
            if (!IsValidTaxNo(taxNo))
            {
                var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                    tenantId, branchId, tx.CounterpartyName, tx.CounterpartyTaxNo, tx.CounterpartyIban,
                    tx.Description, IsIncomingTransfer: true, AllowNihaiTuketici: false), ct);
                taxNo = NormalizeTaxNo(resolved.TaxNo ?? tx.CounterpartyTaxNo);
            }
            if (!IsValidTaxNo(taxNo))
                return Fail("Tedarikçi için geçerli TCKN/VKN bulunamadı.");
        }
        else if (!string.IsNullOrWhiteSpace(options.ManualTaxNo))
        {
            taxNo = NormalizeTaxNo(options.ManualTaxNo);
            if (!IsValidTaxNo(taxNo))
                return Fail("Geçersiz TCKN/VKN.");
            buyerName = CoalesceText(
                BankMovementParser.SanitizeCounterpartyDisplayName(options.ManualBuyerName, taxNo),
                BankMovementParser.SanitizeCounterpartyDisplayName(tx.CounterpartyName, taxNo)) ?? "Karşı Taraf";
        }
        else
        {
            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                tx.CounterpartyName,
                tx.CounterpartyTaxNo,
                tx.CounterpartyIban,
                tx.Description,
                IsIncomingTransfer: true,
                AllowNihaiTuketici: false), ct);

            if (resolved.Success && !string.IsNullOrWhiteSpace(resolved.TaxNo))
            {
                taxNo = resolved.TaxNo;
                buyerName = BankMovementParser.PreferCounterpartyDisplayName(
                    tx.CounterpartyName,
                    buyerName,
                    resolved.DisplayName,
                    taxNo) ?? tx.CounterpartyName ?? "Karşı Taraf";

                if (resolved.CustomerId.HasValue)
                {
                    var customer = await _db.Customers.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == resolved.CustomerId.Value && !x.IsDeleted, ct);
                    if (customer is not null)
                    {
                        customerId = customer.Id;
                        buyerName = BankMovementParser.PreferCounterpartyDisplayName(
                            tx.CounterpartyName,
                            customer.FullName,
                            buyerName,
                            taxNo) ?? customer.FullName;
                        address = customer.Address;
                        city = customer.City;
                        district = customer.District;
                        email = customer.Email;
                    }
                }
                else if (resolved.SupplierId.HasValue)
                {
                    var supplier = await _db.Suppliers.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.Id == resolved.SupplierId.Value && !x.IsDeleted, ct);
                    if (supplier is not null)
                    {
                        supplierId = supplier.Id;
                        buyerName = supplier.CompanyName;
                        address = supplier.Address;
                        city = supplier.City;
                        district = supplier.District;
                        email = supplier.Email;
                    }
                }
                else
                {
                    var customerRows = await LoadCustomerMatchRowsAsync(tenantId, branchId, ct);
                    var matched = ResolveCustomer(customerRows, tx.CounterpartyName, taxNo);
                    if (matched is not null)
                    {
                        customerId = matched.Id;
                        buyerName = matched.FullName;
                        address = matched.Address;
                        city = matched.City;
                        district = matched.District;
                        email = matched.Email;
                    }
                }
            }
            else if (IsValidTaxNo(NormalizeTaxNo(tx.CounterpartyTaxNo)))
            {
                taxNo = NormalizeTaxNo(tx.CounterpartyTaxNo);
                buyerName = tx.CounterpartyName ?? "Karşı Taraf";
                var customerRows = await LoadCustomerMatchRowsAsync(tenantId, branchId, ct);
                var matched = ResolveCustomer(customerRows, tx.CounterpartyName, taxNo);
                if (matched is not null)
                {
                    customerId = matched.Id;
                    buyerName = matched.FullName;
                    address = matched.Address;
                    city = matched.City;
                    district = matched.District;
                    email = matched.Email;
                }
            }
            else
            {
                return Fail("TCKN/VKN bulunamadı. Nihai tüketici veya manuel eşleştirme seçin.");
            }
        }

        if (string.IsNullOrWhiteSpace(buyerName))
            buyerName = tx.CounterpartyName ?? "Karşı Taraf";

        try
        {
            var bankProfile = await LoadBankSyncProfileAsync(tenantId, branchId, ct);
            var vomsisTx = await EnsureVomsisDetailEnrichedAsync(ToImportDtoFromEntity(tx), bankProfile, ct);
            buyerName = BankMovementParser.PreferCounterpartyDisplayName(
                vomsisTx.SenderTitle ?? vomsisTx.SenderName,
                tx.CounterpartyName,
                buyerName,
                taxNo) ?? buyerName;

            var customerRow = new CustomerMatchRow(
                customerId ?? Guid.Empty,
                buyerName,
                taxNo,
                address,
                city,
                district,
                email);
            (address, city, district) = ResolveBuyerLocationFields(customerRow, vomsisTx);

            var description = string.IsNullOrWhiteSpace(tx.Description)
                ? $"Banka tahsilatı - {buyerName}"
                : tx.Description;

            var (invoiceId, documentId) = await _workflow.CreateCollectionDraftAsync(new CollectionDraftInput(
                tenantId,
                branchId,
                customerId,
                buyerName,
                taxNo,
                address,
                city,
                district,
                email,
                tx.Amount,
                description,
                tx.TransactionDateUtc == default ? DateTime.UtcNow : tx.TransactionDateUtc,
                null), ct);

            tx.MatchedCustomerId = customerId;
            tx.CounterpartyTaxNo = taxNo;
            if (!string.IsNullOrWhiteSpace(buyerName))
                tx.CounterpartyName = buyerName;
            tx.InvoiceId = invoiceId;
            tx.EInvoiceDocumentId = documentId;

            if (ShouldAutoSendIncoming(bankProfile, tx.Amount))
            {
                await TryPatchLinkedCollectionDraftAddressAsync(tenantId, tx, bankProfile, ct);
                await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                tx.Status = BankImportStatuses.AutoSendQueued;
                tx.StatusMessage =
                    $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL).";
            }
            else
            {
                tx.Status = BankImportStatuses.DraftCreated;
                tx.StatusMessage = options.UseNihaiTuketici
                    ? "Manuel taslak (nihai tüketici)."
                    : customerId.HasValue || supplierId.HasValue || !string.IsNullOrWhiteSpace(options.ManualTaxNo)
                        ? "Manuel eşleştirme ile taslak oluşturuldu."
                        : $"Taslak oluşturuldu (TCKN/VKN: {taxNo}).";
            }

            await _db.SaveChangesAsync(ct);

            return new BankImportActionResult
                {
                    Success = true,
                    InvoiceId = invoiceId,
                    EInvoiceDocumentId = documentId,
                    Status = tx.Status,
                    Message = tx.StatusMessage
                };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<List<CustomerMatchRow>> LoadCustomerMatchRowsAsync(
        Guid tenantId, Guid branchId, CancellationToken ct)
        => await _db.Customers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.CariTip == 0)
            .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
            .ToListAsync(ct);

    public async Task<BankImportActionResult> RejectAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        string? reason,
        CancellationToken ct)
    {
        var tx = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == transactionId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId, ct);
        if (tx is null)
            return Fail("Hareket bulunamadı.");

        tx.Status = BankImportStatuses.Rejected;
        tx.StatusMessage = string.IsNullOrWhiteSpace(reason) ? "Kullanıcı tarafından reddedildi." : reason.Trim();
        await _db.SaveChangesAsync(ct);
        return new BankImportActionResult { Success = true, Status = tx.Status, Message = tx.StatusMessage };
    }

    private async Task<CustomerMatchRow> EnsureNihaiCustomerAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        var nihaiTax = CounterpartyTaxResolverService.NihaiTuketiciTckn;
        var existing = await _db.Customers
            .AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                !x.IsDeleted &&
                x.CariTip == 0 &&
                x.NationalId != null)
            .ToListAsync(ct);

        var match = existing.FirstOrDefault(c =>
            string.Equals(NormalizeTaxNo(c.NationalId), nihaiTax, StringComparison.Ordinal));
        if (match is not null)
        {
            return new CustomerMatchRow(
                match.Id, match.FullName, match.NationalId, match.Address, match.City, match.District, match.Email);
        }

        var customer = new Customer
        {
            TenantId = tenantId,
            BranchId = branchId,
            CariTip = 0,
            FullName = CounterpartyTaxResolverService.NihaiTuketiciName,
            NationalId = nihaiTax
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);
        return new CustomerMatchRow(
            customer.Id, customer.FullName, customer.NationalId, customer.Address, customer.City, customer.District, customer.Email);
    }

    private async Task<(Guid InvoiceId, Guid DocumentId)> CreateDraftForCustomerAsync(
        Guid tenantId,
        Guid branchId,
        CustomerMatchRow customer,
        string taxNo,
        decimal amount,
        BankImportTransaction entity,
        VomsisTransactionImportDto? vomsisTx,
        BankSyncProfile? bankProfile,
        CancellationToken ct)
    {
        var description = string.IsNullOrWhiteSpace(entity.Description)
            ? $"Banka tahsilatı - {customer.FullName}"
            : entity.Description;

        var enrichedTx = await EnsureVomsisDetailEnrichedAsync(
            vomsisTx ?? ToImportDtoFromEntity(entity),
            bankProfile,
            ct);
        var (address, city, district) = ResolveBuyerLocationFields(customer, enrichedTx);

        return await _workflow.CreateCollectionDraftAsync(new CollectionDraftInput(
            tenantId,
            branchId,
            customer.Id == Guid.Empty ? null : customer.Id,
            ResolveDraftBuyerName(entity, customer),
            taxNo,
            address,
            city,
            district,
            customer.Email,
            amount,
            description,
            entity.TransactionDateUtc == default ? DateTime.UtcNow : entity.TransactionDateUtc,
            null), ct);
    }

    private static string ResolveDraftBuyerName(BankImportTransaction entity, CustomerMatchRow customer)
    {
        var taxNo = entity.CounterpartyTaxNo;
        var bankName = BankMovementParser.SanitizeCounterpartyDisplayName(entity.CounterpartyName, taxNo);
        if (BankMovementParser.HasReliableCounterpartyName(bankName, taxNo))
            return bankName!;

        var customerName = BankMovementParser.SanitizeCounterpartyDisplayName(customer.FullName, taxNo);
        return customerName ?? bankName ?? customer.FullName?.Trim() ?? "Karşı Taraf";
    }

    private static (string? Address, string? City, string? District) ResolveBuyerLocationFields(
        CustomerMatchRow customer,
        VomsisTransactionImportDto? vomsisTx)
    {
        var address = customer.Address;
        var city = customer.City;
        var district = customer.District;

        if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(district))
        {
            var (parsedCity, parsedDistrict) = ParseBuyerCityDistrictFromAddress(address);
            city = CoalesceText(city, parsedCity);
            district = CoalesceText(district, parsedDistrict);
        }

        // Banka şube il/ilçesi alıcı adresi değildir; yalnızca cari/adres bilgisi yoksa son çare.
        if ((string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(district)) && vomsisTx is not null)
        {
            var (branchCity, branchDistrict) = VomsisTaxFieldHelper.ResolveCityDistrict(
                vomsisTx.BankBranchName,
                vomsisTx.BankBranchCity,
                vomsisTx.BankBranchDistrict,
                null);
            city = CoalesceText(city, branchCity);
            district = CoalesceText(district, branchDistrict);
        }

        return (address, city, district);
    }

    private static (string? City, string? District) ParseBuyerCityDistrictFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return (null, null);

        var match = Regex.Match(address.Trim(), @"([^\s/]+)\s*/\s*([^\s/]+)\s*$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return (null, null);

        var district = match.Groups[1].Value.Trim().ToUpperInvariant();
        var city = match.Groups[2].Value.Trim().ToUpperInvariant();
        return (city, district);
    }

    private static void ApplyCounterpartyFields(BankImportTransaction entity, VomsisTransactionImportDto tx)
    {
        var taxNo = NormalizeTaxNo(VomsisTaxFieldHelper.ResolveTaxNo(
            tx.SenderTaxNo,
            BankMovementParser.ExtractTaxNoFromDescription(tx.Description)));
        entity.CounterpartyName = BankMovementParser.ResolveCounterpartyDisplayName(
            tx.SenderName,
            tx.SenderTitle,
            tx.Description,
            taxNo);
        if (!string.IsNullOrWhiteSpace(taxNo))
            entity.CounterpartyTaxNo = taxNo;
        entity.CounterpartyIban = NormalizeIban(tx.SenderIban) ?? entity.CounterpartyIban;

        if (!string.IsNullOrWhiteSpace(tx.BankBranchName))
            entity.BankBranchName = tx.BankBranchName.Trim();
        var city = tx.BankBranchCity;
        var district = tx.BankBranchDistrict;
        ApplyVomsisBranchAddress(ref city, ref district, tx);
        if (!string.IsNullOrWhiteSpace(city))
            entity.BankBranchCity = city;
        if (!string.IsNullOrWhiteSpace(district))
            entity.BankBranchDistrict = district;
    }

    private async Task<VomsisTransactionImportDto> EnrichFromVomsisDetailAsync(
        VomsisTransactionImportDto tx,
        BankSyncProfile? bankProfile,
        CancellationToken ct)
    {
        if (HasBranchAddressInImportDto(tx) && HasVerifiedDekontTax(tx))
            return tx;
        if (bankProfile is null ||
            string.IsNullOrWhiteSpace(bankProfile.VomsisAppKey) ||
            string.IsNullOrWhiteSpace(bankProfile.VomsisAppSecret))
            return tx;

        // Canlıda dekont App Service IP'sinden çekilemez; worker import sırasında doldurur.
        if (ShouldUseWorkerProxy(bankProfile, _config.GetValue("BankSync:PreferWorkerForVomsisPull", true)))
            return tx;

        try
        {
            _vomsis.Configure(bankProfile.VomsisAppKey, bankProfile.VomsisAppSecret);
            var detail = await _vomsis.GetTransactionDetailAsync(tx.ExternalId, ct);
            if (detail is null)
                return tx;

            return VomsisTransactionMapper.MergeDetailIntoImportDto(tx, detail);
        }
        catch (Exception)
        {
            return tx;
        }
    }

    private async Task<VomsisTransactionImportDto> EnsureVomsisDetailEnrichedAsync(
        VomsisTransactionImportDto tx,
        BankSyncProfile? bankProfile,
        CancellationToken ct)
    {
        if (HasBranchAddressInImportDto(tx))
            return tx;
        return await EnrichFromVomsisDetailAsync(tx, bankProfile, ct);
    }

    private static bool HasBranchAddressInImportDto(VomsisTransactionImportDto tx)
        => !string.IsNullOrWhiteSpace(tx.BankBranchCity) && !string.IsNullOrWhiteSpace(tx.BankBranchDistrict);

    private static void ApplyVomsisBranchAddress(ref string? city, ref string? district, VomsisTransactionImportDto? tx)
    {
        if (tx is null)
            return;

        var (parsedCity, parsedDistrict) = VomsisTaxFieldHelper.ResolveCityDistrict(
            tx.BankBranchName,
            tx.BankBranchCity,
            tx.BankBranchDistrict,
            tx.Description);

        if (string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(parsedCity))
            city = parsedCity;
        if (string.IsNullOrWhiteSpace(district) && !string.IsNullOrWhiteSpace(parsedDistrict))
            district = parsedDistrict;
    }

    private static VomsisTransactionImportDto ToImportDtoFromEntity(BankImportTransaction entity)
        => new()
        {
            ExternalId = entity.ExternalId,
            ExternalKey = entity.ExternalKey,
            VomsisAccountId = entity.VomsisAccountId,
            Amount = entity.Amount,
            Currency = entity.Currency,
            Type = entity.TransactionType,
            Description = entity.Description,
            TransactionDateUtc = entity.TransactionDateUtc,
            SenderName = entity.CounterpartyName,
            SenderTitle = entity.CounterpartyName,
            SenderTaxNo = entity.CounterpartyTaxNo,
            SenderIban = entity.CounterpartyIban,
            BankBranchName = entity.BankBranchName,
            BankBranchCity = entity.BankBranchCity,
            BankBranchDistrict = entity.BankBranchDistrict
        };

    private async Task<BankSyncProfile?> LoadBankSyncProfileAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
        => await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);

    public async Task<BankImportTaxRefreshResult> RefreshVomsisTaxAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);

        var entity = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.Id == transactionId &&
                !x.IsDeleted, ct);
        if (entity is null)
            return new BankImportTaxRefreshResult { Success = false, Message = "Banka hareketi bulunamadı." };

        if (!string.Equals(entity.Provider, ProviderVomsis, StringComparison.OrdinalIgnoreCase))
            return new BankImportTaxRefreshResult { Success = false, Message = "Yalnızca Vomsis hareketleri için desteklenir." };

        var bankProfile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (bankProfile is null ||
            string.IsNullOrWhiteSpace(bankProfile.VomsisAppKey) ||
            string.IsNullOrWhiteSpace(bankProfile.VomsisAppSecret))
        {
            return new BankImportTaxRefreshResult
            {
                Success = false,
                Message = "Vomsis ayarları bulunamadı. Önce Vomsis Ayarları sekmesinden kaydedin."
            };
        }

        try
        {
            var preferWorker = _config.GetValue("BankSync:PreferWorkerForVomsisPull", true);
            VomsisTransactionImportDto enriched;

            // Canlıda App Service Vomsis'e/VM:5080'e erişemez. Bu hareketi worker pending kuyruğuna yaz.
            if (preferWorker)
            {
                await _profileService.EnqueuePendingEnrichAsync(tenantId, branchId, entity.ExternalId, ct);
                return new BankImportTaxRefreshResult
                {
                    Success = true,
                    Queued = true,
                    Message =
                        $"Vomsis dekont sorgusu worker kuyruğuna alındı (id={entity.ExternalId}). " +
                        "1-2 dakika bekleyip Yenile yapın. Worker bu hareketin dekontunu çekip TCKN/şube yazar."
                };
            }
            else
            {
                _vomsis.Configure(bankProfile.VomsisAppKey, bankProfile.VomsisAppSecret);
                var detail = await _vomsis.GetTransactionDetailAsync(entity.ExternalId, ct);
                if (detail is null)
                {
                    return new BankImportTaxRefreshResult
                    {
                        Success = false,
                        Message = "Vomsis dekont/detay yanıtı alınamadı."
                    };
                }

                var importDto = new VomsisTransactionImportDto
                {
                    ExternalId = entity.ExternalId,
                    ExternalKey = entity.ExternalKey,
                    VomsisAccountId = entity.VomsisAccountId,
                    Amount = entity.Amount,
                    Currency = entity.Currency,
                    Type = entity.TransactionType,
                    Description = entity.Description,
                    SenderName = entity.CounterpartyName,
                    SenderTitle = entity.CounterpartyName,
                    SenderTaxNo = IsPlaceholderTax(entity.CounterpartyTaxNo) ? null : entity.CounterpartyTaxNo,
                    SenderIban = entity.CounterpartyIban
                };
                enriched = VomsisTransactionMapper.MergeDetailIntoImportDto(importDto, detail);
            }

            ApplyCounterpartyFields(entity, enriched);
            if (entity.InvoiceId.HasValue)
                await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);

            if (!HasValidTaxDigits(entity.CounterpartyTaxNo))
            {
                entity.Status = BankImportStatuses.MissingTaxId;
                entity.StatusMessage = "Vomsis dekontunda geçerli TCKN/VKN bulunamadı.";
                await _db.SaveChangesAsync(ct);
                return new BankImportTaxRefreshResult
                {
                    Success = false,
                    Message = entity.StatusMessage,
                    CounterpartyName = entity.CounterpartyName,
                    CounterpartyTaxNo = entity.CounterpartyTaxNo,
                    Status = entity.Status
                };
            }

            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                entity.CounterpartyName,
                entity.CounterpartyTaxNo,
                entity.CounterpartyIban,
                entity.Description,
                IsIncomingTransfer: true,
                AllowNihaiTuketici: false), ct);

            if (resolved.Success && !string.IsNullOrWhiteSpace(resolved.TaxNo) && !IsPlaceholderTax(resolved.TaxNo))
            {
                entity.CounterpartyTaxNo = NormalizeTaxNo(resolved.TaxNo);
                entity.CounterpartyName = BankMovementParser.PreferCounterpartyDisplayName(
                    enriched.SenderTitle ?? enriched.SenderName,
                    entity.CounterpartyName,
                    resolved.DisplayName,
                    resolved.TaxNo) ?? entity.CounterpartyName;
                entity.MatchedCustomerId = resolved.CustomerId;
                entity.Status = BankImportStatuses.Pending;
                entity.StatusMessage = resolved.Message ?? "Vomsis dekontundan TCKN/VKN alındı.";
                await TryPatchLinkedCollectionDraftTaxAsync(tenantId, entity, ct);
            }
            else if (HasValidTaxDigits(entity.CounterpartyTaxNo) && !IsPlaceholderTax(entity.CounterpartyTaxNo))
            {
                entity.Status = BankImportStatuses.Pending;
                entity.StatusMessage = "Vomsis dekontundan TCKN/VKN alındı.";
                await TryPatchLinkedCollectionDraftTaxAsync(tenantId, entity, ct);
            }
            else
            {
                entity.Status = BankImportStatuses.MissingTaxId;
                entity.StatusMessage = resolved.Message ?? "Vomsis dekontunda geçerli TCKN/VKN bulunamadı.";
            }

            await _db.SaveChangesAsync(ct);
            return new BankImportTaxRefreshResult
            {
                Success = HasValidTaxDigits(entity.CounterpartyTaxNo),
                Message = entity.StatusMessage,
                CounterpartyTaxNo = entity.CounterpartyTaxNo,
                CounterpartyName = entity.CounterpartyName,
                Status = entity.Status
            };
        }
        catch (Exception ex)
        {
            var message = VomsisIpErrorHelper.IsIpBlockedError(ex.Message)
                ? VomsisIpErrorHelper.BuildBlockedMessage(ex.Message) +
                  "\n\nNot: Canlıda TCKN/VKN sorgusu Azure VM worker IP'si üzerinden yapılmalıdır. " +
                  "'Hareketleri Çek' ile yeniden senkron deneyin."
                : ex.Message;
            return new BankImportTaxRefreshResult { Success = false, Message = message };
        }
    }

    public async Task<BankImportTaxRefreshResult> ApplyVomsisEnrichmentAsync(
        Guid tenantId,
        Guid branchId,
        VomsisEnrichmentApplyDto enrichment,
        CancellationToken ct)
    {
        await _schema.EnsureAsync(ct);
        if (enrichment is null || enrichment.ExternalId <= 0)
            return new BankImportTaxRefreshResult { Success = false, Message = "ExternalId zorunludur." };

        var entity = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.Provider == ProviderVomsis &&
                !x.IsDeleted &&
                (x.ExternalId == enrichment.ExternalId ||
                 (!string.IsNullOrWhiteSpace(enrichment.ExternalKey) && x.ExternalKey == enrichment.ExternalKey)), ct);

        if (entity is null)
        {
            // Worker kuyruğunda takılı id kalmasın.
            await RemovePendingEnrichIdAsync(tenantId, branchId, enrichment.ExternalId, ct);
            await _db.SaveChangesAsync(ct);
            return new BankImportTaxRefreshResult
            {
                Success = false,
                Message = $"Banka hareketi bulunamadı (externalId={enrichment.ExternalId})."
            };
        }

        var dto = new VomsisTransactionImportDto
        {
            ExternalId = enrichment.ExternalId,
            ExternalKey = enrichment.ExternalKey ?? entity.ExternalKey,
            Amount = entity.Amount,
            Currency = entity.Currency,
            Type = entity.TransactionType,
            Description = entity.Description,
            SenderName = CoalesceText(enrichment.SenderName, entity.CounterpartyName),
            SenderTitle = CoalesceText(enrichment.SenderTitle, enrichment.SenderName, entity.CounterpartyName),
            SenderTaxNo = CoalesceText(enrichment.SenderTaxNo,
                IsPlaceholderTax(entity.CounterpartyTaxNo) ? null : entity.CounterpartyTaxNo),
            SenderIban = entity.CounterpartyIban,
            BankBranchName = CoalesceText(enrichment.BankBranchName, entity.BankBranchName),
            BankBranchCity = CoalesceText(enrichment.BankBranchCity, entity.BankBranchCity),
            BankBranchDistrict = CoalesceText(enrichment.BankBranchDistrict, entity.BankBranchDistrict)
        };

        ApplyCounterpartyFields(entity, dto);

        var hasTax = HasValidTaxDigits(entity.CounterpartyTaxNo) && !IsPlaceholderTax(entity.CounterpartyTaxNo);
        if (hasTax && entity.Status == BankImportStatuses.MissingTaxId)
        {
            entity.Status = BankImportStatuses.Pending;
            entity.StatusMessage = "Vomsis dekontundan TCKN/VKN alındı.";
        }
        else if (!string.IsNullOrWhiteSpace(entity.BankBranchName) ||
                 !string.IsNullOrWhiteSpace(entity.BankBranchCity))
        {
            entity.StatusMessage = CoalesceText(
                entity.StatusMessage,
                "Vomsis dekontundan şube/adres bilgisi güncellendi.");
        }

        var bankProfile = await LoadBankSyncProfileAsync(tenantId, branchId, ct);
        if (entity.InvoiceId.HasValue)
        {
            if (hasTax)
                await TryPatchLinkedCollectionDraftTaxAsync(tenantId, entity, ct);
            await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
        }

        await RemovePendingEnrichIdAsync(tenantId, branchId, enrichment.ExternalId, ct);
        await _db.SaveChangesAsync(ct);

        return new BankImportTaxRefreshResult
        {
            Success = hasTax ||
                      !string.IsNullOrWhiteSpace(entity.BankBranchName) ||
                      !string.IsNullOrWhiteSpace(entity.BankBranchCity),
            Message = hasTax
                ? "TCKN/VKN ve şube bilgisi Vomsis dekontundan uygulandı."
                : (!string.IsNullOrWhiteSpace(entity.BankBranchName)
                    ? "Şube bilgisi uygulandı; TCKN/VKN dekontta yok veya okunamadı."
                    : "Dekont zenginleştirmesi uygulandı ancak TCKN/şube boş."),
            CounterpartyTaxNo = entity.CounterpartyTaxNo,
            CounterpartyName = entity.CounterpartyName,
            BankBranchName = entity.BankBranchName,
            BankBranchCity = entity.BankBranchCity,
            BankBranchDistrict = entity.BankBranchDistrict,
            Status = entity.Status
        };
    }

    private async Task RemovePendingEnrichIdAsync(
        Guid tenantId,
        Guid branchId,
        long externalId,
        CancellationToken ct)
    {
        var profile = await _db.BankSyncProfiles
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (profile is null || string.IsNullOrWhiteSpace(profile.PendingEnrichExternalIdsJson))
            return;

        var ids = BankSyncProfileService.ParsePendingEnrichIds(profile.PendingEnrichExternalIdsJson)
            .Where(x => x != externalId)
            .ToArray();
        profile.PendingEnrichExternalIdsJson = ids.Length == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(ids);
    }

    private static bool HasValidTaxDigits(string? taxNo)
    {
        var digits = NormalizeTaxNo(taxNo);
        return digits.Length is 10 or 11;
    }

    private static void ApplyVomsisTransactionDate(BankImportTransaction entity, VomsisTransactionImportDto tx)
    {
        if (tx.TransactionDateUtc.HasValue && tx.TransactionDateUtc.Value != default)
            entity.TransactionDateUtc = tx.TransactionDateUtc.Value;
    }

    private static BankImportTransaction CreateEntity(
        Guid tenantId,
        Guid branchId,
        VomsisTransactionImportDto tx,
        string currency,
        string txType,
        decimal amount)
    {
        return new BankImportTransaction
        {
            TenantId = tenantId,
            BranchId = branchId,
            Provider = ProviderVomsis,
            ExternalId = tx.ExternalId,
            ExternalKey = tx.ExternalKey.Trim(),
            VomsisAccountId = tx.VomsisAccountId,
            Amount = amount,
            Currency = currency,
            TransactionType = txType,
            Description = tx.Description?.Trim(),
            TransactionDateUtc = tx.TransactionDateUtc ?? DateTime.UtcNow
        };
    }

    private static string NormalizeVomsisTxType(string? type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant()
            .Replace('ı', 'i')
            .Replace('İ', 'i')
            .Replace('ş', 's')
            .Replace('Ş', 's')
            .Replace('ğ', 'g')
            .Replace('Ğ', 'g')
            .Replace('ü', 'u')
            .Replace('Ü', 'u')
            .Replace('ö', 'o')
            .Replace('Ö', 'o')
            .Replace('ç', 'c')
            .Replace('Ç', 'c');
        return t;
    }

    private static bool IsOutgoingTryTransfer(BankImportTransaction entity)
        => string.Equals(NormalizeVomsisTxType(entity.TransactionType), "borclu", StringComparison.OrdinalIgnoreCase)
           && IsTryCurrency(entity.Currency);

    private static bool IsIncomingTryTransfer(BankImportTransaction entity)
        => string.Equals(NormalizeVomsisTxType(entity.TransactionType), "alacakli", StringComparison.OrdinalIgnoreCase)
           && IsTryCurrency(entity.Currency);

    private static bool HasBankImportDraft(BankImportTransaction entity)
        => entity.InvoiceId.HasValue || entity.EInvoiceDocumentId.HasValue;

    private static bool IsQualifyingIncomingTransfer(string txType, decimal amount, string currency, string? description)
    {
        if (amount <= 0m) return false;
        if (!string.Equals(NormalizeVomsisTxType(txType), "alacakli", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsTryCurrency(currency)) return false;

        var desc = (description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(desc)) return true;

        var upper = desc.ToUpperInvariant();
        if (upper.Contains("GELEN HAVALE") || upper.Contains("GELEN EFT") || upper.Contains("GELEN") ||
            upper.Contains(" HAVALE") || upper.Contains(" EFT"))
            return true;
        if (upper.Contains("GİDEN") || upper.Contains("GIDEN") || upper.Contains("BORÇ") || upper.Contains("BORC"))
            return false;

        return true;
    }

    private static bool ShouldAutoDraft(EInvoiceProfile? profile, decimal amountTl)
    {
        var settings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        if (!settings.AutoDraftEnabled) return false;

        var allowed = settings.AutoDraftAllowedPaymentMethods ?? [];
        if (allowed.Count > 0 &&
            !allowed.Any(m => string.Equals(m, "Tahsilat", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (settings.AutoDraftMinTotal.HasValue && settings.AutoDraftMinTotal.Value > 0m && amountTl < settings.AutoDraftMinTotal.Value)
            return false;
        if (settings.AutoDraftMaxTotal.HasValue && settings.AutoDraftMaxTotal.Value > 0m && amountTl > settings.AutoDraftMaxTotal.Value)
            return false;

        return true;
    }

    private static bool ShouldCreateIncomingBankDraft(
        EInvoiceProfile? profile,
        BankSyncProfile? bankProfile,
        decimal amountTl)
    {
        // Bankadan gelen otomatik fatura yalnızca otomatik talimat eşiğini geçen hareketlerde oluşturulur/gönderilir.
        // Eşik altı veya talimat kapalıysa taslak dahi oluşturulmaz.
        _ = profile;
        _ = bankProfile;
        _ = amountTl;
        return false;
    }

    private static string? ValidateIncomingCollectionWorkmanship(
        EInvoiceProfile? profile,
        decimal amountTl)
    {
        var settings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        if (!EInvoiceProfileSettingsCodec.HasAnyWorkmanshipRules(settings.WorkmanshipRules))
        {
            return "Özel matrah işçilik kuralları tanımlı değil. Fatura Ayarları > Özel Matrah İçin İşçilik Belirleme bölümünden 24K/22K/18K/14K/8K aralıklarını ekleyin.";
        }

        if (EInvoiceProfileSettingsCodec.ListMatchingCollectionWorkmanshipRules(settings.WorkmanshipRules, amountTl).Count == 0)
        {
            return $"Tutar ({amountTl:N2} TL) için eşleşen özel matrah işçilik kuralı yok. Fatura Ayarları > Özel Matrah İçin İşçilik Belirleme aralıklarını kontrol edin.";
        }

        return null;
    }

    private static string IncomingBelowThresholdMessage(BankSyncProfile? bankProfile)
    {
        if (bankProfile is null || !bankProfile.AutoInstructionIncomingEnabled)
            return "Eşleşti; otomatik talimat kapalı — taslak oluşturulmadı.";

        var min = bankProfile.AutoInstructionIncomingMinAmount;
        if (!min.HasValue || min.Value <= 0m)
            return "Eşleşti; otomatik talimat alt limiti tanımlı değil — taslak oluşturulmadı.";

        return $"Tutar otomatik talimat sınırının altında (<{min.Value:N0} TL); taslak oluşturulmadı.";
    }

    private static bool ShouldSkipForAccountFilter(
        int? accountId,
        HashSet<int> allowedAccounts,
        string txType,
        string currency)
    {
        if (allowedAccounts.Count == 0)
            return false;

        // TL gelen havaleler hesap filtresinden muaf — Vomsis hesap id eksik/yanlış olsa da işlenir.
        if (string.Equals(txType, "alacakli", StringComparison.OrdinalIgnoreCase) && IsTryCurrency(currency))
            return false;

        if (!accountId.HasValue)
            return true;

        return !allowedAccounts.Contains(accountId.Value);
    }

    private async Task<VomsisTransactionImportDto> EnsureTaxEnrichedAsync(
        VomsisTransactionImportDto tx,
        BankSyncProfile? bankProfile,
        CancellationToken ct)
    {
        if (bankProfile is null ||
            string.IsNullOrWhiteSpace(bankProfile.VomsisAppKey) ||
            string.IsNullOrWhiteSpace(bankProfile.VomsisAppSecret))
            return tx;

        if (!_config.GetValue("BankSync:EnrichTaxFromDetailOnImport", false) &&
            !_config.GetValue("BankSync:EnrichTaxFromDetailWhenMissing", true))
            return tx;

        if (HasVerifiedDekontTax(tx) && HasReliableImportTitle(tx))
            return tx;

        // Canlıda Vomsis yalnızca VM IP'sinden açılır; App Service IP'si ip_not_found alır.
        // TCKN/VKN zenginleştirmesi worker tarafında yapılır.
        if (ShouldUseWorkerProxy(bankProfile, _config.GetValue("BankSync:PreferWorkerForVomsisPull", true)))
            return tx;

        return await EnrichFromVomsisDetailAsync(tx, bankProfile, ct);
    }

    private static bool ShouldAttemptExistingUpgrade(BankImportTransaction entity, BankSyncProfile? bankProfile)
    {
        if (entity.Status is BankImportStatuses.Rejected)
            return false;
        if (IsPlaceholderTax(entity.CounterpartyTaxNo))
            return true;
        if (string.IsNullOrWhiteSpace(entity.CounterpartyTaxNo) ||
            string.IsNullOrWhiteSpace(entity.BankBranchName) ||
            string.IsNullOrWhiteSpace(entity.BankBranchCity) ||
            string.IsNullOrWhiteSpace(entity.BankBranchDistrict))
            return true;
        if (entity.Status is BankImportStatuses.DraftCreated &&
            entity.InvoiceId.HasValue &&
            ShouldAutoSendIncoming(bankProfile, entity.Amount))
            return true;
        if (!IsOutgoingTryTransfer(entity) &&
            !entity.InvoiceId.HasValue &&
            IsIncomingTryTransfer(entity) &&
            ShouldAutoSendIncoming(bankProfile, entity.Amount) &&
            entity.Status is BankImportStatuses.Pending
                or BankImportStatuses.NoCustomerMatch
                or BankImportStatuses.MissingTaxId
                or BankImportStatuses.Skipped)
            return true;
        if (IsOutgoingTryTransfer(entity) &&
            !entity.EInvoiceDocumentId.HasValue &&
            ShouldAutoSendOutgoing(bankProfile, entity.Amount) &&
            entity.Status is BankImportStatuses.Pending
                or BankImportStatuses.MissingTaxId
                or BankImportStatuses.Skipped)
            return true;
        return entity.Status is BankImportStatuses.MissingTaxId
            or BankImportStatuses.Pending
            or BankImportStatuses.NoCustomerMatch
            or BankImportStatuses.Skipped;
    }

    private static bool NeedsDekontEnrichment(BankImportTransaction entity, VomsisTransactionImportDto tx)
    {
        var entityMissingTax = string.IsNullOrWhiteSpace(entity.CounterpartyTaxNo) ||
                               IsPlaceholderTax(entity.CounterpartyTaxNo);

        // Worker dekonttan TCKN getirdiyse mevcut MissingTaxId satırını güncelle.
        if (entityMissingTax && HasVerifiedDekontTax(tx))
            return true;

        if (IsPlaceholderTax(entity.CounterpartyTaxNo))
            return true;
        if (HasVerifiedDekontTax(tx))
            return false;
        if (!entityMissingTax)
            return false;
        return entity.Status is BankImportStatuses.MissingTaxId
            or BankImportStatuses.Pending
            or BankImportStatuses.NoCustomerMatch
            or BankImportStatuses.Skipped;
    }

    private static bool HasVerifiedDekontTax(VomsisTransactionImportDto tx)
    {
        var tax = NormalizeTaxNo(tx.SenderTaxNo);
        return IsValidTaxNo(tax) && !IsPlaceholderTax(tax);
    }

    private static bool IsPlaceholderTax(string? taxNo)
        => string.Equals(
            NormalizeTaxNo(taxNo),
            CounterpartyTaxResolverService.NihaiTuketiciTckn,
            StringComparison.Ordinal);

    private static bool HasReliableImportTitle(VomsisTransactionImportDto tx)
        => BankMovementParser.HasReliableCounterpartyName(
            CoalesceText(tx.SenderTitle, tx.SenderName),
            tx.SenderTaxNo);

    private async Task TryPatchLinkedCollectionDraftAddressAsync(
        Guid tenantId,
        BankImportTransaction entity,
        BankSyncProfile? bankProfile,
        CancellationToken ct)
    {
        if (!entity.InvoiceId.HasValue)
            return;

        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == entity.InvoiceId.Value, ct);
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.CollectionMetaJson))
            return;

        if (!entity.MatchedCustomerId.HasValue || entity.MatchedCustomerId.Value == Guid.Empty)
            return;

        var customer = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == entity.MatchedCustomerId.Value && !x.IsDeleted, ct);
        if (customer is null)
            return;

        try
        {
            var node = JsonNode.Parse(invoice.CollectionMetaJson)?.AsObject();
            if (node is null)
                return;

            var currentCity = node["BuyerCity"]?.GetValue<string>() ?? node["buyerCity"]?.GetValue<string>();
            var currentDistrict = node["BuyerDistrict"]?.GetValue<string>() ?? node["buyerDistrict"]?.GetValue<string>();
            var currentAddress = node["BuyerAddress"]?.GetValue<string>() ?? node["buyerAddress"]?.GetValue<string>();

            var address = CoalesceText(currentAddress, customer.Address);
            var city = CoalesceText(currentCity, customer.City);
            var district = CoalesceText(currentDistrict, customer.District);
            if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(district))
            {
                var (parsedCity, parsedDistrict) = ParseBuyerCityDistrictFromAddress(address);
                city = CoalesceText(city, parsedCity);
                district = CoalesceText(district, parsedDistrict);
            }

            if (string.Equals(currentCity, city, StringComparison.Ordinal) &&
                string.Equals(currentDistrict, district, StringComparison.Ordinal) &&
                string.Equals(currentAddress, address, StringComparison.Ordinal))
                return;

            if (!string.IsNullOrWhiteSpace(address))
                node["BuyerAddress"] = address;
            if (!string.IsNullOrWhiteSpace(city))
                node["BuyerCity"] = city;
            if (!string.IsNullOrWhiteSpace(district))
                node["BuyerDistrict"] = district;
            invoice.CollectionMetaJson = node.ToJsonString();
        }
        catch
        {
            // Meta güncellenemezse işlem devam eder.
        }
    }

    private async Task TryPatchLinkedCollectionDraftTaxAsync(
        Guid tenantId,
        BankImportTransaction entity,
        CancellationToken ct)
    {
        if (!entity.InvoiceId.HasValue || IsPlaceholderTax(entity.CounterpartyTaxNo))
            return;

        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == entity.InvoiceId.Value, ct);
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.CollectionMetaJson))
            return;

        try
        {
            var node = JsonNode.Parse(invoice.CollectionMetaJson)?.AsObject();
            if (node is null)
                return;

            var currentTax = NormalizeTaxNo(node["BuyerTaxNumber"]?.GetValue<string>() ?? node["buyerTaxNumber"]?.GetValue<string>());
            var nextTax = NormalizeTaxNo(entity.CounterpartyTaxNo);
            if (string.Equals(currentTax, nextTax, StringComparison.Ordinal) && !IsPlaceholderTax(currentTax))
                return;

            node["BuyerTaxNumber"] = nextTax;
            if (!string.IsNullOrWhiteSpace(entity.CounterpartyName))
                node["BuyerName"] = entity.CounterpartyName!.Trim();
            invoice.CollectionMetaJson = node.ToJsonString();
        }
        catch
        {
            // Meta güncellenemezse banka satırı yine de güncellenmiş olur.
        }
    }

    private static bool HasTaxInImportDto(VomsisTransactionImportDto tx)
    {
        var tax = NormalizeTaxNo(VomsisTaxFieldHelper.ResolveTaxNo(
            tx.SenderTaxNo,
            BankMovementParser.ExtractTaxNoFromDescription(tx.Description)));
        return IsValidTaxNo(tax);
    }

    private async Task<bool> TryUpgradeExistingIncomingAsync(
        BankImportTransaction entity,
        VomsisTransactionImportDto tx,
        Guid tenantId,
        Guid branchId,
        EInvoiceProfile? profile,
        BankSyncProfile? bankProfile,
        BankSyncImportResult result,
        CancellationToken ct)
    {
        var placeholderTax = IsPlaceholderTax(entity.CounterpartyTaxNo);

        if (entity.Status is BankImportStatuses.Rejected)
            return false;

        // Giden havaleler (borclu) bu yola düşmemeli; e-Fatura/e-Arşiv oluşturulmaz.
        var earlyType = NormalizeVomsisTxType(entity.TransactionType ?? tx.Type);
        var earlyCurrency = NormalizeCurrency(entity.Currency ?? tx.Currency);
        if (string.Equals(earlyType, "borclu", StringComparison.OrdinalIgnoreCase) &&
            IsTryCurrency(earlyCurrency))
            return false;

        // Önce worker'dan gelen TCKN/şube alanlarını uygula (DraftCreated olsa bile).
        // Eski erken return'ler mevcut taslaklarda il/ilçe ve TCKN backfill'ini engelliyordu.
        var enrichedTx = await EnsureTaxEnrichedAsync(tx, bankProfile, ct);
        ApplyCounterpartyFields(entity, enrichedTx);

        var hasTaxNow = HasValidTaxDigits(entity.CounterpartyTaxNo) && !IsPlaceholderTax(entity.CounterpartyTaxNo);
        if (hasTaxNow && entity.Status == BankImportStatuses.MissingTaxId)
        {
            entity.Status = BankImportStatuses.Pending;
            entity.StatusMessage = "Vomsis dekontundan TCKN/VKN alındı.";
        }

        if (entity.InvoiceId.HasValue)
        {
            if (hasTaxNow)
                await TryPatchLinkedCollectionDraftTaxAsync(tenantId, entity, ct);
            await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
        }

        if (entity.Status is BankImportStatuses.DraftCreated &&
            entity.InvoiceId.HasValue &&
            ShouldAutoSendIncoming(bankProfile, entity.Amount))
        {
            try
            {
                await _workflow.QueueManualSendAsync(tenantId, entity.InvoiceId.Value, null, ct);
                entity.Status = BankImportStatuses.AutoSendQueued;
                entity.StatusMessage =
                    $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL).";
                await _db.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                entity.StatusMessage = "Otomatik gönderim başarısız: " + ex.Message;
                await _db.SaveChangesAsync(ct);
                return true;
            }
        }

        if (entity.Status is BankImportStatuses.DraftCreated or BankImportStatuses.AutoSendQueued)
        {
            await _db.SaveChangesAsync(ct);
            return hasTaxNow ||
                   !string.IsNullOrWhiteSpace(entity.BankBranchName) ||
                   !string.IsNullOrWhiteSpace(entity.BankBranchCity);
        }

        if (placeholderTax && entity.InvoiceId.HasValue && hasTaxNow)
        {
            entity.StatusMessage = "Vomsis dekontundan gerçek TCKN/VKN ile güncellendi.";
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (entity.InvoiceId.HasValue)
        {
            await _db.SaveChangesAsync(ct);
            return hasTaxNow ||
                   !string.IsNullOrWhiteSpace(entity.BankBranchName) ||
                   !string.IsNullOrWhiteSpace(entity.BankBranchCity);
        }

        if (!NeedsDekontEnrichment(entity, tx) &&
            string.IsNullOrWhiteSpace(tx.BankBranchName) &&
            string.IsNullOrWhiteSpace(tx.BankBranchCity) &&
            hasTaxNow &&
            !string.IsNullOrWhiteSpace(entity.BankBranchName))
            return false;

        var txType = NormalizeVomsisTxType(enrichedTx.Type ?? entity.TransactionType);
        var currency = NormalizeCurrency(enrichedTx.Currency ?? entity.Currency);
        var amount = entity.Amount;

        // Tip alanı bozulmuş olsa bile mevcut MissingTaxId satırını TCKN ile güncellemeye devam et.
        var qualifying = IsQualifyingIncomingTransfer(
            txType, amount, currency, enrichedTx.Description ?? entity.Description);
        if (!qualifying && entity.Status is not BankImportStatuses.MissingTaxId)
        {
            if (entity.Status is BankImportStatuses.Skipped or BankImportStatuses.Pending)
            {
                await _db.SaveChangesAsync(ct);
                if (entity.InvoiceId.HasValue)
                    await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                return HasValidTaxDigits(entity.CounterpartyTaxNo) ||
                       !string.IsNullOrWhiteSpace(entity.BankBranchCity);
            }

            return HasValidTaxDigits(entity.CounterpartyTaxNo);
        }

        if (entity.Status is not (
            BankImportStatuses.Skipped or
            BankImportStatuses.MissingTaxId or
            BankImportStatuses.Pending or
            BankImportStatuses.NoCustomerMatch))
        {
            return !string.IsNullOrWhiteSpace(entity.CounterpartyTaxNo);
        }

        var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
            tenantId,
            branchId,
            entity.CounterpartyName,
            entity.CounterpartyTaxNo,
            entity.CounterpartyIban,
            entity.Description,
            IsIncomingTransfer: true,
            AllowNihaiTuketici: false), ct);

        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.TaxNo) || IsPlaceholderTax(resolved.TaxNo))
        {
            if (string.IsNullOrWhiteSpace(entity.CounterpartyTaxNo) || IsPlaceholderTax(entity.CounterpartyTaxNo))
            {
                entity.Status = BankImportStatuses.MissingTaxId;
                entity.StatusMessage = resolved.Message ?? "TCKN/VKN bulunamadı.";
            }
            return !string.IsNullOrWhiteSpace(entity.CounterpartyTaxNo) && !IsPlaceholderTax(entity.CounterpartyTaxNo);
        }

        entity.CounterpartyTaxNo = NormalizeTaxNo(resolved.TaxNo);
        entity.CounterpartyName = BankMovementParser.PreferCounterpartyDisplayName(
            enrichedTx.SenderTitle ?? enrichedTx.SenderName,
            entity.CounterpartyName,
            resolved.DisplayName,
            resolved.TaxNo) ?? entity.CounterpartyName;

        CustomerMatchRow? customer = null;
        if (resolved.CustomerId.HasValue)
        {
            customer = await _db.Customers.AsNoTracking()
                .Where(x => x.Id == resolved.CustomerId.Value && !x.IsDeleted)
                .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            var customerRows = await _db.Customers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.CariTip == 0)
                .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
                .ToListAsync(ct);
            customer = ResolveCustomer(customerRows, entity.CounterpartyName, entity.CounterpartyTaxNo);
        }

        if (customer is null && !resolved.IsNihaiTuketici)
        {
            if (ShouldAutoSendIncoming(bankProfile, amount) &&
                IsValidTaxNo(NormalizeTaxNo(entity.CounterpartyTaxNo)))
            {
                var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                if (workmanshipError is not null)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = workmanshipError;
                }
                else
                {
                    try
                    {
                        var counterparty = new CustomerMatchRow(
                            Guid.Empty,
                            entity.CounterpartyName ?? resolved.DisplayName ?? "Karşı Taraf",
                            entity.CounterpartyTaxNo!,
                            null,
                            null,
                            null,
                            null);
                        var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                            tenantId, branchId, counterparty, entity.CounterpartyTaxNo!, amount, entity, enrichedTx, bankProfile, ct);
                        await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                        await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                        entity.Status = BankImportStatuses.AutoSendQueued;
                        entity.StatusMessage =
                            $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL, cari eşleşmesiz).";
                        entity.InvoiceId = invoiceId;
                        entity.EInvoiceDocumentId = documentId;
                        result.DraftCreated++;
                    }
                    catch (Exception ex)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = "Otomatik gönderim başarısız: " + ex.Message;
                    }
                }

                await _db.SaveChangesAsync(ct);
                return true;
            }

            entity.Status = BankImportStatuses.NoCustomerMatch;
            entity.StatusMessage = resolved.Message ?? "Karşı taraf cari kayıtlarda bulunamadı.";
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (customer is not null)
        {
            entity.MatchedCustomerId = customer.Id;
            var taxNo = entity.CounterpartyTaxNo!;

            if (ShouldAutoSendIncoming(bankProfile, amount))
            {
                var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                if (workmanshipError is not null)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = workmanshipError;
                }
                else
                {
                    try
                    {
                        var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                            tenantId, branchId, customer, taxNo, amount, entity, enrichedTx, bankProfile, ct);
                        await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                        await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                        entity.Status = BankImportStatuses.AutoSendQueued;
                        entity.StatusMessage = $"Otomatik e-Fatura/e-Arşiv gönderim kuyruğuna alındı (≥{bankProfile!.AutoInstructionIncomingMinAmount:N0} TL).";
                        entity.InvoiceId = invoiceId;
                        entity.EInvoiceDocumentId = documentId;
                        result.DraftCreated++;
                    }
                    catch (Exception ex)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = "Otomatik gönderim başarısız: " + ex.Message;
                    }
                }
            }
            else if (ShouldCreateIncomingBankDraft(profile, bankProfile, amount))
            {
                var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                if (workmanshipError is not null)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = workmanshipError;
                }
                else
                {
                    try
                    {
                        var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                            tenantId, branchId, customer, taxNo, amount, entity, enrichedTx, bankProfile, ct);
                        entity.Status = BankImportStatuses.DraftCreated;
                        entity.StatusMessage = $"Otomatik taslak oluşturuldu ({resolved.Source}).";
                        entity.InvoiceId = invoiceId;
                        entity.EInvoiceDocumentId = documentId;
                        result.DraftCreated++;
                    }
                    catch (Exception ex)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = "Taslak oluşturulamadı: " + ex.Message;
                    }
                }
            }
            else if (entity.InvoiceId is null)
            {
                entity.Status = BankImportStatuses.Pending;
                entity.StatusMessage = IncomingBelowThresholdMessage(bankProfile);
            }

        }
        else if (resolved.IsNihaiTuketici)
        {
            if (ShouldAutoSendIncoming(bankProfile, amount))
            {
                var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                if (workmanshipError is not null)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = workmanshipError;
                }
                else
                {
                    try
                    {
                        var nihaiCustomer = await EnsureNihaiCustomerAsync(tenantId, branchId, ct);
                        entity.MatchedCustomerId = nihaiCustomer.Id;
                        var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                            tenantId, branchId, nihaiCustomer, resolved.TaxNo!, amount, entity, enrichedTx, bankProfile, ct);
                        await TryPatchLinkedCollectionDraftAddressAsync(tenantId, entity, bankProfile, ct);
                        await _workflow.QueueManualSendAsync(tenantId, invoiceId, null, ct);
                        entity.Status = BankImportStatuses.AutoSendQueued;
                        entity.StatusMessage = "Otomatik e-Arşiv gönderim kuyruğuna alındı (nihai tüketici).";
                        entity.InvoiceId = invoiceId;
                        entity.EInvoiceDocumentId = documentId;
                        result.DraftCreated++;
                    }
                    catch (Exception ex)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = "Nihai tüketici taslağı oluşturulamadı: " + ex.Message;
                    }
                }
            }
            else if (ShouldCreateIncomingBankDraft(profile, bankProfile, amount))
            {
                var workmanshipError = ValidateIncomingCollectionWorkmanship(profile, amount);
                if (workmanshipError is not null)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = workmanshipError;
                }
                else
                {
                    try
                    {
                        var nihaiCustomer = await EnsureNihaiCustomerAsync(tenantId, branchId, ct);
                        entity.MatchedCustomerId = nihaiCustomer.Id;
                        var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                            tenantId, branchId, nihaiCustomer, resolved.TaxNo!, amount, entity, enrichedTx, bankProfile, ct);
                        entity.Status = BankImportStatuses.DraftCreated;
                        entity.StatusMessage = "Otomatik taslak (nihai tüketici).";
                        entity.InvoiceId = invoiceId;
                        entity.EInvoiceDocumentId = documentId;
                        result.DraftCreated++;
                    }
                    catch (Exception ex)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = "Nihai tüketici taslağı oluşturulamadı: " + ex.Message;
                    }
                }
            }
            else if (entity.InvoiceId is null)
            {
                entity.Status = BankImportStatuses.Pending;
                entity.StatusMessage = IncomingBelowThresholdMessage(bankProfile);
            }
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static bool ShouldAutoSendIncoming(BankSyncProfile? bankProfile, decimal amountTl)
    {
        if (bankProfile is null || !bankProfile.AutoInstructionIncomingEnabled)
            return false;
        if (!bankProfile.AutoInstructionIncomingMinAmount.HasValue || bankProfile.AutoInstructionIncomingMinAmount.Value <= 0m)
            return false;
        return amountTl >= bankProfile.AutoInstructionIncomingMinAmount.Value;
    }

    private static bool ShouldAutoSendOutgoing(BankSyncProfile? bankProfile, decimal amountTl)
    {
        if (bankProfile is null || !bankProfile.AutoInstructionOutgoingEnabled)
            return false;
        if (!bankProfile.AutoInstructionOutgoingMinAmount.HasValue || bankProfile.AutoInstructionOutgoingMinAmount.Value <= 0m)
            return false;
        return amountTl >= bankProfile.AutoInstructionOutgoingMinAmount.Value;
    }

    private async Task ImportOutgoingTransactionAsync(
        Guid tenantId,
        Guid branchId,
        BankSyncProfile? bankProfile,
        VomsisTransactionImportDto tx,
        string currency,
        string txType,
        decimal amount,
        BankSyncImportResult result,
        CancellationToken ct)
    {
        var enrichedTx = await EnsureTaxEnrichedAsync(tx, bankProfile, ct);
        var entity = CreateEntity(tenantId, branchId, enrichedTx, currency, txType, amount);
        ApplyCounterpartyFields(entity, enrichedTx);

        if (!ShouldAutoSendOutgoing(bankProfile, amount))
        {
            if (bankProfile?.AutoInstructionOutgoingEnabled == true)
            {
                entity.Status = BankImportStatuses.Pending;
                entity.StatusMessage = $"Giden havale — otomatik gider pusulası eşiği altında (<{bankProfile.AutoInstructionOutgoingMinAmount:N0} TL).";
                result.PendingReview++;
            }
            else
            {
                entity.Status = BankImportStatuses.Skipped;
                entity.StatusMessage = "Giden havale — otomatik talimat tanımlı değil.";
                result.SkippedFilter++;
            }

            _db.BankImportTransactions.Add(entity);
            result.Imported++;
            return;
        }

        await TryCreateOutgoingExpenseSlipDraftAsync(
            tenantId, branchId, entity, bankProfile, result, ct);

        _db.BankImportTransactions.Add(entity);
        result.Imported++;
    }

    private async Task<bool> TryUpgradeExistingOutgoingAsync(
        BankImportTransaction entity,
        VomsisTransactionImportDto tx,
        Guid tenantId,
        Guid branchId,
        BankSyncProfile? bankProfile,
        BankSyncImportResult result,
        CancellationToken ct)
    {
        if (entity.Status is BankImportStatuses.Rejected)
            return false;

        var enrichedTx = await EnsureTaxEnrichedAsync(tx, bankProfile, ct);
        ApplyCounterpartyFields(entity, enrichedTx);

        // Giden havale asla e-Fatura/e-Arşiv taslağına dönüştürülmez.
        if (entity.InvoiceId.HasValue)
        {
            entity.StatusMessage =
                "Giden havale e-Fatura/e-Arşiv ile ilişkilendirilmişti; yeni işlemler Gider Pusulası taslağına yazılır.";
            await _db.SaveChangesAsync(ct);
            return true;
        }

        if (entity.Status is BankImportStatuses.DraftCreated or BankImportStatuses.AutoSendQueued)
        {
            if (entity.EInvoiceDocumentId.HasValue)
            {
                await _db.SaveChangesAsync(ct);
                return true;
            }
        }

        if (!ShouldAutoSendOutgoing(bankProfile, entity.Amount))
        {
            if (entity.Status is BankImportStatuses.Skipped or BankImportStatuses.Pending)
            {
                if (bankProfile?.AutoInstructionOutgoingEnabled == true)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage =
                        $"Giden havale — otomatik gider pusulası eşiği altında (<{bankProfile.AutoInstructionOutgoingMinAmount:N0} TL).";
                }
                await _db.SaveChangesAsync(ct);
                return true;
            }

            return HasValidTaxDigits(entity.CounterpartyTaxNo);
        }

        if (entity.Status is not (
            BankImportStatuses.Skipped or
            BankImportStatuses.MissingTaxId or
            BankImportStatuses.Pending or
            BankImportStatuses.NoCustomerMatch))
        {
            await _db.SaveChangesAsync(ct);
            return HasValidTaxDigits(entity.CounterpartyTaxNo);
        }

        await TryCreateOutgoingExpenseSlipDraftAsync(
            tenantId, branchId, entity, bankProfile, result, ct);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task TryCreateOutgoingExpenseSlipDraftAsync(
        Guid tenantId,
        Guid branchId,
        BankImportTransaction entity,
        BankSyncProfile? bankProfile,
        BankSyncImportResult result,
        CancellationToken ct)
    {
        var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
            tenantId,
            branchId,
            entity.CounterpartyName,
            entity.CounterpartyTaxNo,
            entity.CounterpartyIban,
            entity.Description,
            IsIncomingTransfer: false,
            AllowNihaiTuketici: false), ct);

        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.TaxNo))
        {
            entity.Status = BankImportStatuses.MissingTaxId;
            entity.StatusMessage = resolved.Message ?? "Giden havale için TCKN/VKN bulunamadı.";
            result.MissingTaxId++;
            result.PendingReview++;
            return;
        }

        entity.CounterpartyTaxNo = resolved.TaxNo;
        entity.CounterpartyName = BankMovementParser.PreferCounterpartyDisplayName(
            null,
            entity.CounterpartyName,
            resolved.DisplayName,
            resolved.TaxNo) ?? entity.CounterpartyName;
        if (resolved.CustomerId.HasValue)
            entity.MatchedCustomerId = resolved.CustomerId;
        else if (resolved.SupplierId.HasValue)
            entity.MatchedCustomerId = resolved.SupplierId;

        var buyerName = entity.CounterpartyName ?? resolved.DisplayName ?? "Karşı Taraf";
        try
        {
            var expenseSlipId = await CreateExpenseSlipDraftFromBankAsync(
                tenantId, branchId, entity, resolved.TaxNo, buyerName, ct);
            entity.Status = BankImportStatuses.DraftCreated;
            entity.StatusMessage =
                $"Gider Pusulası taslağı oluşturuldu (≥{bankProfile!.AutoInstructionOutgoingMinAmount:N0} TL). Gider Pusulası sekmesinden gönderin.";
            entity.EInvoiceDocumentId = expenseSlipId;
            entity.InvoiceId = null;
            result.DraftCreated++;
        }
        catch (Exception ex)
        {
            entity.Status = BankImportStatuses.Pending;
            entity.StatusMessage = "Gider Pusulası taslağı oluşturulamadı: " + ex.Message;
            result.PendingReview++;
        }
    }

    private async Task<Guid> CreateExpenseSlipDraftFromBankAsync(
        Guid tenantId,
        Guid branchId,
        BankImportTransaction entity,
        string taxNo,
        string buyerName,
        CancellationToken ct)
    {
        await _expenseSlipSchema.EnsureAsync(ct);

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.IsActive, ct);
        var senderTaxNo = NormalizeTaxNo(profile?.TaxNumber);
        if (senderTaxNo.Length is not (10 or 11))
            throw new InvalidOperationException("Gider pusulası için aktif şube e-fatura profilinde geçerli Vergi No zorunludur.");

        var settings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        var karat = EInvoiceProfileSettingsCodec.ResolveRandomExpenseSlipKarat(settings.WorkmanshipRules, entity.Amount);
        var productType = EInvoiceProfileSettingsCodec.BuildExpenseSlipProductType(karat);
        var workmanship = EInvoiceProfileSettingsCodec.BuildExpenseSlipWorkmanship(karat);

        IReadOnlyDictionary<string, decimal>? adjustedSell = null;
        try
        {
            adjustedSell = await _rates.GetAdjustedSellRatesByCodeAsync(tenantId, branchId, ct);
        }
        catch
        {
            // Kur servisi geçici ulaşılamazsa ham kura düş.
        }

        var unitPrice = _rates.GetKaratGramSellPrice(karat, "HAS", adjustedSell);
        if (unitPrice <= 0m)
            unitPrice = _rates.GetKaratGramSellPrice(karat);
        if (unitPrice <= 0m)
            throw new InvalidOperationException($"{karat} altın kuru bulunamadı; gider pusulası taslağı oluşturulamadı.");

        var grandTotal = decimal.Round(Math.Max(0m, entity.Amount), 2, MidpointRounding.AwayFromZero);
        var quantityGram = Math.Round(grandTotal / unitPrice, 6, MidpointRounding.AwayFromZero);
        if (quantityGram <= 0m)
            throw new InvalidOperationException("Gram miktarı hesaplanamadı (tutar / ayar kuru).");

        var docNo = await BuildExpenseSlipDocumentNoAsync(tenantId, branchId, ct);
        var payload = JsonSerializer.Serialize(new
        {
            branchId,
            buyerName = buyerName.Trim(),
            buyerTaxNumber = taxNo,
            grandTotal,
            workmanship,
            productType,
            karat,
            quantityGram,
            unitPrice,
            lineTotal = grandTotal,
            currency = "TRY",
            description = entity.Description?.Trim(),
            source = "BankImport"
        });

        var row = new ExpenseSlipDocument
        {
            TenantId = tenantId,
            BranchId = branchId,
            DocumentNo = docNo,
            Status = "Draft",
            Currency = "TRY",
            GrandTotal = grandTotal,
            BuyerName = buyerName.Trim(),
            BuyerTaxNumber = taxNo,
            Description = entity.Description?.Trim(),
            PayloadJson = payload
        };
        _db.ExpenseSlipDocuments.Add(row);
        _db.ExpenseSlipAuditLogs.Add(new ExpenseSlipAuditLog
        {
            TenantId = tenantId,
            BranchId = branchId,
            DocumentId = row.Id,
            Action = "AutoDraftFromBankImport",
            StatusBefore = null,
            StatusAfter = row.Status,
            IsSuccess = true,
            RequestJson = payload
        });
        await _db.SaveChangesAsync(ct);
        return row.Id;
    }

    private async Task<BankImportActionResult> CreateOutgoingExpenseSlipDraftFromImportAsync(
        Guid tenantId,
        Guid branchId,
        BankImportTransaction tx,
        Guid? matchedPartyId,
        CreateBankImportDraftOptions? options,
        CancellationToken ct)
    {
        options ??= new CreateBankImportDraftOptions();
        string taxNo;
        string buyerName;

        if (options.UseNihaiTuketici)
        {
            taxNo = CounterpartyTaxResolverService.NihaiTuketiciTckn;
            buyerName = CounterpartyTaxResolverService.NihaiTuketiciName;
        }
        else if (options.CustomerId is Guid cid && cid != Guid.Empty)
        {
            var customer = await _db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == cid && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
            if (customer is null)
                return Fail("Müşteri bulunamadı.");
            buyerName = customer.FullName;
            taxNo = NormalizeTaxNo(CoalesceText(options.ManualTaxNo, customer.NationalId, tx.CounterpartyTaxNo));
        }
        else if (options.SupplierId is Guid sid && sid != Guid.Empty)
        {
            var supplier = await _db.Suppliers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == sid && x.TenantId == tenantId && !x.IsDeleted && x.IsActive, ct);
            if (supplier is null)
                return Fail("Tedarikçi bulunamadı.");
            buyerName = supplier.CompanyName;
            taxNo = NormalizeTaxNo(CoalesceText(options.ManualTaxNo, supplier.TaxNumber, tx.CounterpartyTaxNo));
        }
        else if (matchedPartyId is Guid mid && mid != Guid.Empty)
        {
            var customer = await _db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == mid && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
            if (customer is null)
                return Fail("Müşteri bulunamadı.");
            buyerName = customer.FullName;
            taxNo = NormalizeTaxNo(CoalesceText(customer.NationalId, tx.CounterpartyTaxNo));
        }
        else if (!string.IsNullOrWhiteSpace(options.ManualTaxNo))
        {
            taxNo = NormalizeTaxNo(options.ManualTaxNo);
            buyerName = CoalesceText(
                BankMovementParser.SanitizeCounterpartyDisplayName(options.ManualBuyerName, taxNo),
                BankMovementParser.SanitizeCounterpartyDisplayName(tx.CounterpartyName, taxNo)) ?? "Karşı Taraf";
        }
        else
        {
            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                tx.CounterpartyName,
                tx.CounterpartyTaxNo,
                tx.CounterpartyIban,
                tx.Description,
                IsIncomingTransfer: false,
                AllowNihaiTuketici: false), ct);
            taxNo = NormalizeTaxNo(resolved.TaxNo ?? tx.CounterpartyTaxNo);
            buyerName = resolved.DisplayName
                ?? BankMovementParser.SanitizeCounterpartyDisplayName(tx.CounterpartyName, taxNo)
                ?? "Karşı Taraf";
        }

        if (!IsValidTaxNo(taxNo))
            return Fail("Giden havale için geçerli TCKN/VKN bulunamadı.");

        buyerName = BankMovementParser.SanitizeCounterpartyDisplayName(buyerName, taxNo)
            ?? BankMovementParser.SanitizeCounterpartyDisplayName(tx.CounterpartyName, taxNo)
            ?? buyerName;

        try
        {
            tx.CounterpartyTaxNo = taxNo;
            if (!string.IsNullOrWhiteSpace(buyerName))
                tx.CounterpartyName = buyerName;
            if (matchedPartyId is Guid partyId && partyId != Guid.Empty)
                tx.MatchedCustomerId = partyId;
            else if (options.CustomerId is Guid optCid && optCid != Guid.Empty)
                tx.MatchedCustomerId = optCid;

            var expenseSlipId = await CreateExpenseSlipDraftFromBankAsync(
                tenantId, branchId, tx, taxNo, buyerName, ct);
            tx.InvoiceId = null;
            tx.EInvoiceDocumentId = expenseSlipId;
            tx.Status = BankImportStatuses.DraftCreated;
            tx.StatusMessage = "Gider Pusulası taslağı oluşturuldu.";
            await _db.SaveChangesAsync(ct);

            return new BankImportActionResult
            {
                Success = true,
                EInvoiceDocumentId = expenseSlipId,
                Status = tx.Status,
                Message = tx.StatusMessage
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<string> BuildExpenseSlipDocumentNoAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"GPS-{datePart}-";
        var countToday = await _db.ExpenseSlipDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && x.DocumentNo.StartsWith(prefix))
            .CountAsync(ct);
        return $"{prefix}{(countToday + 1):0000}";
    }

    private static string TruncateText(string value, int maxLen)
        => value.Length <= maxLen ? value : value[..maxLen];

    private static CustomerMatchRow? ResolveCustomer(
        IReadOnlyList<CustomerMatchRow> customers,
        string? counterpartyName,
        string? counterpartyTaxNo)
    {
        if (!string.IsNullOrWhiteSpace(counterpartyTaxNo))
        {
            var byTax = customers.FirstOrDefault(c =>
                string.Equals(NormalizeTaxNo(c.NationalId), counterpartyTaxNo, StringComparison.Ordinal));
            if (byTax is not null) return byTax;
        }

        if (string.IsNullOrWhiteSpace(counterpartyName))
            return null;

        var normalizedTarget = BankMovementParser.NormalizeName(counterpartyName);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return null;

        var exact = customers.FirstOrDefault(c =>
            string.Equals(BankMovementParser.NormalizeName(c.FullName), normalizedTarget, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var containsMatches = customers
            .Where(c =>
            {
                var n = BankMovementParser.NormalizeName(c.FullName);
                return n.Contains(normalizedTarget, StringComparison.Ordinal) ||
                       normalizedTarget.Contains(n, StringComparison.Ordinal);
            })
            .ToList();
        if (containsMatches.Count == 1)
            return containsMatches[0];

        return containsMatches
            .OrderByDescending(c => BankMovementParser.NameSimilarity(c.FullName, counterpartyName))
            .FirstOrDefault(c => BankMovementParser.NameSimilarity(c.FullName, counterpartyName) >= 0.72);
    }

    private static bool IsTryCurrency(string currency)
        => string.Equals(currency, "TRY", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(currency, "TL", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCurrency(string? value)
    {
        var c = (value ?? "TRY").Trim().ToUpperInvariant();
        return c switch
        {
            "TL" => "TRY",
            "TRY" => "TRY",
            "EUR" or "EURO" => "EUR",
            "USD" => "USD",
            _ => c
        };
    }

    private static string NormalizeTaxNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static bool IsValidTaxNo(string taxNo)
        => CounterpartyTaxResolverService.IsAcceptableTaxNo(taxNo);

    private static string? NormalizeIban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(compact) ? null : compact;
    }

    private static string? CoalesceText(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    private static BankImportActionResult Fail(string message)
        => new() { Success = false, Message = message };

    private sealed record CustomerMatchRow(
        Guid Id,
        string FullName,
        string? NationalId,
        string? Address,
        string? City,
        string? District,
        string? Email);
}

public static class VomsisTaxFieldHelper
{
    private static readonly string[] TaxFieldTokens =
    [
        "sender_taxno", "sender_tax_no", "sender_tckn", "payer_tax_no", "payer_tckn",
        "receiver_taxno", "receiver_tax_no", "receiver_tckn",
        "related_vkn", "related_tckn", "related_tax_no", "related_vkn_tckn",
        "ilgili_vkn", "ilgili_tckn", "ilgili_tax_no", "ilgili_vkn_tckn", "ilgili_kimlik",
        "counterparty_tax_no", "counterparty_tckn",
        "tax_no", "taxno", "vkn", "tckn", "vkn_tckn", "tckn_vkn",
        "identity_number", "national_id", "national_id_no", "vergi_no", "vergi_kimlik_no",
        "tc_kimlik", "tc_kimlik_no", "kimlik_no", "identity_no", "id_number"
    ];

    private static readonly string[] TitleFieldTokens =
    [
        "related_title", "ilgili_unvan", "sender_title", "sender_name", "counterparty_name", "payer_name"
    ];

    private static readonly string[] BranchFieldTokens =
    [
        "branch_name", "sube_adi", "sube_ad", "sube_name", "bank_branch_name", "account_branch_name"
    ];

    private static readonly Regex VomsisBranchCityDistrictRegex = new(
        @"^\s*(?<district>[^/]+?)\s*/\s*(?<city>[^/]+?)\s*(?:Subesi|Şubesi|SUBESI|Sube|Şube|Branch)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VomsisBranchCityDistrictInlineRegex = new(
        @"(?<district>[\p{L}\p{M}]+(?:\s+[\p{L}\p{M}]+)?)\s*/\s*(?<city>[\p{L}\p{M}]+(?:\s+[\p{L}\p{M}]+)?)\s*(?:Subesi|Şubesi|SUBESI|Sube|Şube|Branch)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static (string? City, string? District) ResolveCityDistrict(
        string? branchName,
        string? branchCity,
        string? branchDistrict,
        string? description)
    {
        if (!string.IsNullOrWhiteSpace(branchCity) && !string.IsNullOrWhiteSpace(branchDistrict))
            return (FormatLocationName(branchCity, upper: true), FormatLocationName(branchDistrict, upper: false));

        var fromBranch = ParseCityDistrictFromBranchName(branchName);
        if (!string.IsNullOrWhiteSpace(fromBranch.City) && !string.IsNullOrWhiteSpace(fromBranch.District))
            return fromBranch;

        return ParseCityDistrictFromText(description);
    }

    public static (string? City, string? District) ParseCityDistrictFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, null);

        var match = VomsisBranchCityDistrictInlineRegex.Match(text.Trim());
        if (!match.Success)
            return (null, null);

        var district = FormatLocationName(match.Groups["district"].Value, upper: false);
        var city = FormatLocationName(match.Groups["city"].Value, upper: true);
        if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(district))
            return (null, null);

        return (city, district);
    }

    private static string FormatLocationName(string value, bool upper)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return trimmed;
        return upper
            ? trimmed.ToUpper(new CultureInfo("tr-TR"))
            : char.ToUpper(trimmed[0], new CultureInfo("tr-TR")) + trimmed[1..].ToLower(new CultureInfo("tr-TR"));
    }

    public static string? ResolveTaxNo(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizeTaxDigits(candidate);
            if (normalized.Length is 10 or 11)
                return normalized;
        }

        return null;
    }

    public static string? ExtractTaxNoFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractTaxNoFromElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractTitleFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractTitleFromElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractBranchNameFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ExtractBranchNameFromElement(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public static (string? City, string? District) ParseCityDistrictFromBranchName(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return (null, null);

        // Vomsis "i̇" (combining dot) gönderebiliyor.
        var text = branchName.Trim().Normalize(NormalizationForm.FormC).Replace("\u0307", "", StringComparison.Ordinal);
        text = Regex.Replace(text, @"\s*(?:Subesi|Şubesi|SUBESI|Sube|Şube|Branch)\s*$", "", RegexOptions.IgnoreCase).Trim();

        var match = VomsisBranchCityDistrictRegex.Match(text);
        if (!match.Success)
            match = VomsisBranchCityDistrictInlineRegex.Match(text);
        if (!match.Success)
            return (null, null);

        var district = FormatLocationName(match.Groups["district"].Value.Trim(), upper: false);
        var city = FormatLocationName(match.Groups["city"].Value.Trim(), upper: true);
        if (string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(district))
            return (null, null);

        return (city, district);
    }

    private static string? ExtractTaxNoFromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsTaxFieldName(prop.Name))
                    {
                        var direct = NormalizeTaxDigits(ReadJsonString(prop.Value));
                        if (direct.Length is 10 or 11)
                            return direct;
                    }

                    var nested = ExtractTaxNoFromElement(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ExtractTaxNoFromElement(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
        }

        return null;
    }

    private static string? ExtractTitleFromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsTitleFieldName(prop.Name))
                    {
                        var value = ReadJsonString(prop.Value)?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }

                    var nested = ExtractTitleFromElement(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ExtractTitleFromElement(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
        }

        return null;
    }

    private static string? ExtractBranchNameFromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (IsBranchFieldName(prop.Name))
                    {
                        var value = ReadJsonString(prop.Value)?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }

                    var nested = ExtractBranchNameFromElement(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ExtractBranchNameFromElement(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
        }

        return null;
    }

    private static bool IsTitleFieldName(string name)
    {
        var normalized = NormalizeFieldToken(name);
        return TitleFieldTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static bool IsBranchFieldName(string name)
    {
        var normalized = NormalizeFieldToken(name);
        return BranchFieldTokens.Any(token =>
            string.Equals(normalized, token, StringComparison.Ordinal) ||
            normalized.EndsWith("_" + token, StringComparison.Ordinal));
    }

    private static bool IsTaxFieldName(string name)
    {
        var normalized = NormalizeFieldToken(name);
        return TaxFieldTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string NormalizeFieldToken(string name)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        normalized = normalized
            .Replace('ü', 'u')
            .Replace('ö', 'o')
            .Replace('ı', 'i')
            .Replace('ş', 's')
            .Replace('ç', 'c')
            .Replace('ğ', 'g');
        return normalized;
    }

    private static string? ReadJsonString(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();

    private static string NormalizeTaxDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        return new string(value.Where(char.IsDigit).ToArray());
    }
}

public static class BankMovementParser
{
    private static readonly string[] DescriptionPrefixes =
    [
        "Gelen Havale Ödemesi",
        "Gelen Havale",
        "Gelen EFT",
        "Gelen Fast",
        "Gelen FAST",
        "GELEN HAVALE ÖDEMESİ",
        "GELEN HAVALE",
        "HAVALE"
    ];

    private static readonly Regex TransferLabelPrefixRegex = new(
        @"^(?:Gönd(?:eren)?|GON(?:DEREN)?|Gönderici|GONDERICI|Gönder|GONDER|Alan|ALAN|Alıcı|ALICI|From|FROM|To|TO)\s*\.?\s*:?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CompanyTransferNameRegex = new(
        @"^(?:Gönd(?:eren)?|GON(?:DEREN)?|Gönderici|GONDERICI|Gönder|GONDER|Alan|ALAN|Alıcı|ALICI|From|FROM|To|TO)\s*\.?\s*:?\s*(.+?)\s+\d{10,11}\s+\d{4}-",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NameBeforeAccountBranchRegex = new(
        @"^(.+?)\s+\d{10,11}\s+\d{4}-",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LeadingAccountPersonNameRegex = new(
        @"^\d{10,11}\s+(.+?)\s+(?:Ziraat|Vakif|Vakıf|Halk|Is\s*Bankasi|İş\s*Bankası|Garanti|Akbank|Yapı\s*Kredi|Yapi\s*Kredi|QNB|Deniz|TEB|ING|Enpara|Kuveyt|Fibabanka|Mobil|FAST|HAVALE|EFT)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BankBranchSuffixRegex = new(
        @"\s+\d{10,11}\s+\d{4}-.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BankNameAfterAccountRegex = new(
        @"\s+\d{10,11}\s+(?:Türkiye\s+)?(?:Halk|Ziraat|Vakıf|Vakif|Is\s*Bankasi|İş\s*Bankası|Garanti|Akbank|Yapı\s*Kredi|Yapi\s*Kredi|QNB|Deniz|TEB|ING|Enpara|Kuveyt|Fibabanka).*?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? ResolveCounterpartyDisplayName(
        string? senderName,
        string? senderTitle,
        string? description,
        string? knownTaxNo = null)
    {
        var fromTitle = SanitizeCounterpartyDisplayName(senderTitle, knownTaxNo);
        var fromSender = SanitizeCounterpartyDisplayName(senderName, knownTaxNo);
        var fromDescription = SanitizeCounterpartyDisplayName(ExtractCounterpartyName(description), knownTaxNo);

        // sender_title / sender_name açıklamadan daha güvenilir; uzunlukla değil kaynakla önceliklendir.
        foreach (var candidate in new[] { fromTitle, fromSender, fromDescription })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && !LooksLikeTransferLabel(candidate))
                return candidate;
        }

        return new[] { fromTitle, fromSender, fromDescription }
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    public static bool LooksLikeTransferLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return TransferLabelPrefixRegex.IsMatch(text) ||
               text.StartsWith("Gönd", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("GON", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Mobil Havale", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Gelen Havale", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vomsis ilgili ünvan / dekont adının güvenilir olup olmadığını kontrol eder.</summary>
    public static bool HasReliableCounterpartyName(string? value, string? knownTaxNo = null)
    {
        var sanitized = SanitizeCounterpartyDisplayName(value, knownTaxNo);
        return !string.IsNullOrWhiteSpace(sanitized) && !LooksLikeTransferLabel(sanitized);
    }

    /// <summary>Dekont ünvanını (ilgili_unvan) açıklama ve EDM ünvanına tercih eder.</summary>
    public static string? PreferCounterpartyDisplayName(
        string? vomsisTitleOrName,
        string? currentName,
        string? resolvedName,
        string? knownTaxNo = null)
    {
        foreach (var candidate in new[]
                 {
                     SanitizeCounterpartyDisplayName(vomsisTitleOrName, knownTaxNo),
                     SanitizeCounterpartyDisplayName(currentName, knownTaxNo),
                     SanitizeCounterpartyDisplayName(resolvedName, knownTaxNo)
                 })
        {
            if (HasReliableCounterpartyName(candidate, knownTaxNo))
                return candidate;
        }

        return new[]
        {
            SanitizeCounterpartyDisplayName(vomsisTitleOrName, knownTaxNo),
            SanitizeCounterpartyDisplayName(currentName, knownTaxNo),
            SanitizeCounterpartyDisplayName(resolvedName, knownTaxNo)
        }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    public static string? ExtractCounterpartyName(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var text = description.Trim();

        foreach (var prefix in DescriptionPrefixes.OrderByDescending(p => p.Length))
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim(' ', '-', ':', '.');
                break;
            }
        }

        var companyTransfer = CompanyTransferNameRegex.Match(text);
        if (companyTransfer.Success)
            return SanitizeCounterpartyDisplayName(companyTransfer.Groups[1].Value);

        var beforeBranch = NameBeforeAccountBranchRegex.Match(text);
        if (beforeBranch.Success)
            return SanitizeCounterpartyDisplayName(beforeBranch.Groups[1].Value);

        var leadingAccountPerson = LeadingAccountPersonNameRegex.Match(text);
        if (leadingAccountPerson.Success)
            return SanitizeCounterpartyDisplayName(leadingAccountPerson.Groups[1].Value);

        text = Regex.Replace(text, @"\b(VKN|TCKN|TC)\s*:?\s*\d+\b", "", RegexOptions.IgnoreCase).Trim();
        return SanitizeCounterpartyDisplayName(text);
    }

    /// <summary>
    /// Vomsis açıklamasından veya birleşik metinden alıcı/gönderen ünvanını temizler.
    /// Örn: "48130086662 TEKİN ÖZKAN Ziraat Mobil Havale" → "TEKİN ÖZKAN"
    /// </summary>
    public static string? SanitizeCounterpartyDisplayName(string? raw, string? knownTaxNo = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        for (var i = 0; i < 3; i++)
        {
            var stripped = TransferLabelPrefixRegex.Replace(text, "").Trim();
            if (stripped.Length == text.Length)
                break;
            text = stripped;
        }

        text = Regex.Replace(text, @"^(?:TCKN|VKN|TC|VERGI\s*NO)\s*:?\s*\d+\s*", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"^\d{11}\s+", "").Trim();
        text = Regex.Replace(text, @"^\d{10}\s+", "").Trim();

        var taxDigits = string.IsNullOrWhiteSpace(knownTaxNo)
            ? null
            : new string(knownTaxNo.Where(char.IsDigit).ToArray());
        if (!string.IsNullOrWhiteSpace(taxDigits))
            text = Regex.Replace(text, $@"^{Regex.Escape(taxDigits)}\s+", "", RegexOptions.IgnoreCase).Trim();

        var bankSuffixes = new[]
        {
            "Ziraat Mobil Havale", "Ziraat Mobil HAVALE", "Ziraat Mobil Fast", "Ziraat Mobil FAST",
            "Gelen Havale Ödemesi", "Gelen Havale", "Gelen EFT", "Gelen FAST", "Gelen Fast",
            "Mobil Havale", "Mobil HAVALE", "Mobil Fast", "Mobil FAST",
            "FAST işlemi", "FAST ISLEMI", "FAST Islem", "FAST İşlem", "FAST İşlemi",
            "FAST İşlem", "FAST Islem",
            "HAVALE", "EFT"
        };
        foreach (var suffix in bankSuffixes.OrderByDescending(s => s.Length))
        {
            var idx = text.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                text = text[..idx].Trim();
                break;
            }
        }

        text = BankBranchSuffixRegex.Replace(text, "").Trim();
        text = BankNameAfterAccountRegex.Replace(text, "").Trim();
        text = Regex.Replace(text, @"\s+\d{10,11}(?=\s+\d{4}-)", "", RegexOptions.CultureInvariant).Trim();

        text = Regex.Replace(
            text,
            @"\s+(Ziraat|Vakif|Vakıf|Halk|Is Bankasi|İş Bankası|Garanti|Akbank|Yapı Kredi|Yapi Kredi|QNB|Deniz|TEB|ING|Enpara|Kuveyt|Fibabanka|A\.?\s?Ş\.?|A\.?\s?S\.?).*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        text = CollapseRepeatedName(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string CollapseRepeatedName(string text)
    {
        var duplicate = Regex.Match(text, @"^(.+?)\s+\1(?:\s|$)", RegexOptions.IgnoreCase);
        if (duplicate.Success)
            return duplicate.Groups[1].Value.Trim();

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts.Length % 2 == 0)
        {
            var half = parts.Length / 2;
            var first = string.Join(' ', parts.Take(half));
            var second = string.Join(' ', parts.Skip(half));
            if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
                return first;
        }

        return text;
    }

    /// <summary>Açıklamadaki TCKN/VKN'yi döndürür; hesap numaralarını (şube kodu veya banka adı öncesi) ayıklar.</summary>
    public static string? ExtractTaxNoFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var labeledTc = Regex.Match(description, @"(?:TCKN|TC\s*KIMLIK|TC)\s*:?\s*(\d{11})", RegexOptions.IgnoreCase);
        if (labeledTc.Success && TurkishTaxIdValidator.IsValidTckn(labeledTc.Groups[1].Value))
            return labeledTc.Groups[1].Value;

        var labeledVkn = Regex.Match(description, @"(?:VKN|VERGI\s*NO)\s*:?\s*(\d{10})", RegexOptions.IgnoreCase);
        if (labeledVkn.Success && TurkishTaxIdValidator.IsValidVkn(labeledVkn.Groups[1].Value))
            return labeledVkn.Groups[1].Value;

        foreach (Match match in Regex.Matches(description, @"(?<!\d)(\d{11})(?!\d)"))
        {
            if (LooksLikeBankAccountNumber(description, match))
                continue;
            if (TurkishTaxIdValidator.IsValidTckn(match.Groups[1].Value))
                return match.Groups[1].Value;
        }

        foreach (Match match in Regex.Matches(description, @"(?<!\d)(\d{10})(?!\d)"))
        {
            if (LooksLikeBankAccountNumber(description, match))
                continue;
            if (TurkishTaxIdValidator.IsValidVkn(match.Groups[1].Value))
                return match.Groups[1].Value;
        }

        return null;
    }

    private static bool LooksLikeBankAccountNumber(string description, Match digitMatch)
    {
        var after = description[(digitMatch.Index + digitMatch.Length)..].TrimStart();
        if (Regex.IsMatch(after, @"^\d{4}-"))
            return true;

        if (Regex.IsMatch(after,
                @"^(?:Türkiye\s+)?(?:Halk|Ziraat|Vakıf|Vakif|Is\s*Bankasi|İş\s*Bankası|Garanti|Akbank|Yapı\s*Kredi|Yapi\s*Kredi|QNB|Deniz|TEB|ING|Enpara|Kuveyt|Fibabanka)\b",
                RegexOptions.IgnoreCase))
            return true;

        var before = description[..digitMatch.Index].TrimEnd();
        if (string.IsNullOrWhiteSpace(before) || TransferLabelPrefixRegex.IsMatch(before))
        {
            if (Regex.IsMatch(after, @"^[A-Za-zÇĞİÖŞÜçğıöşü]"))
                return true;
        }

        if (Regex.IsMatch(before,
                @"(?:Gönd(?:eren)?|GON(?:DEREN)?|Gönderici|GONDERICI|Gönder|GONDER|Alan|ALAN|Alıcı|ALICI)\s*\.?\s*:?\s*.+\S",
                RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().ToUpperInvariant();
        text = ReplaceTurkishChars(text);
        text = Regex.Replace(text, @"[^A-Z0-9\s]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        var legalSuffixes = new[]
        {
            "SAN TIC LTD STI", "SAN TIC AS", "TIC LTD STI", "LTD STI", "LTD STI",
            "LIMITED SIRKETI", "ANONIM SIRKETI", "AS", "STI", "LTD"
        };
        foreach (var suffix in legalSuffixes.OrderByDescending(s => s.Length))
        {
            if (text.EndsWith(" " + suffix, StringComparison.Ordinal))
                text = text[..^(suffix.Length + 1)].Trim();
        }

        return text;
    }

    public static double NameSimilarity(string? a, string? b)
    {
        var na = NormalizeName(a);
        var nb = NormalizeName(b);
        if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb))
            return 0;
        if (string.Equals(na, nb, StringComparison.Ordinal)) return 1;
        if (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal))
            return 0.85;

        var longer = na.Length >= nb.Length ? na : nb;
        var shorter = na.Length >= nb.Length ? nb : na;
        var common = shorter.Count(ch => longer.Contains(ch));
        return (double)common / longer.Length;
    }

    private static string ReplaceTurkishChars(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                'İ' or 'I' or 'ı' or 'i' => 'I',
                'Ş' or 'ş' => 'S',
                'Ğ' or 'ğ' => 'G',
                'Ü' or 'ü' => 'U',
                'Ö' or 'ö' => 'O',
                'Ç' or 'ç' => 'C',
                _ => char.ToUpperInvariant(ch)
            });
        }
        return sb.ToString();
    }
}
