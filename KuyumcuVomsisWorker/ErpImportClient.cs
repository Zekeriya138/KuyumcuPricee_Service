using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace KuyumcuVomsisWorker;

public sealed class ErpImportClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ErpImportClient> _logger;
    private RemoteWorkerConfig? _remote;

    public ErpImportClient(HttpClient http, IConfiguration config, ILogger<ErpImportClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public void Configure(RemoteWorkerConfig remote)
    {
        _remote = remote;
        _http.BaseAddress = new Uri(remote.ErpApiBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(90);

        var headerName = _config["ErpApi:AppKeyHeader"] ?? "x-app-key";
        _http.DefaultRequestHeaders.Remove(headerName);
        _http.DefaultRequestHeaders.Add(headerName, remote.ErpApiAppKey ?? "");
        _http.DefaultRequestHeaders.Remove("X-Tenant-Id");
        _http.DefaultRequestHeaders.Remove("X-Branch-Id");
        _http.DefaultRequestHeaders.Add("X-Tenant-Id", remote.TenantId.ToString());
        _http.DefaultRequestHeaders.Add("X-Branch-Id", remote.BranchId.ToString());
    }

    public async Task<ErpImportResult?> ImportAsync(IReadOnlyList<ErpImportTransaction> transactions, CancellationToken ct)
    {
        if (_remote is null)
            throw new InvalidOperationException("ERP import istemcisi yapılandırılmadı.");

        var payload = new ErpImportRequest
        {
            BranchId = _remote.BranchId,
            Transactions = transactions.ToList()
        };

        using var resp = await _http.PostAsJsonAsync("api/bank-sync/vomsis/import", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ERP import HTTP {(int)resp.StatusCode}: {body}");

        var result = await resp.Content.ReadFromJsonAsync<ErpImportResult>(cancellationToken: ct);
        _logger.LogInformation(
            "ERP import: received={Received}, imported={Imported}, drafts={Drafts}, pending={Pending}",
            result?.Received, result?.Imported, result?.DraftCreated, result?.PendingReview);
        return result;
    }
}

public sealed class ErpImportRequest
{
    public Guid BranchId { get; set; }
    public List<ErpImportTransaction> Transactions { get; set; } = new();
}

public sealed class ErpImportTransaction
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
    public string? SenderTaxNo { get; set; }
    public string? SenderIban { get; set; }
}

public sealed class ErpImportResult
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

public static class VomsisTransactionMapper
{
    public static ErpImportTransaction ToErp(VomsisTransaction tx)
    {
        return new ErpImportTransaction
        {
            ExternalId = tx.Id,
            ExternalKey = string.IsNullOrWhiteSpace(tx.Key) ? tx.Id.ToString(CultureInfo.InvariantCulture) : tx.Key.Trim(),
            VomsisAccountId = tx.BankAccountId,
            Amount = tx.Amount,
            Currency = NormalizeCurrency(tx.FecName),
            Type = tx.Type,
            Description = tx.Description,
            TransactionDateUtc = ParseSystemDate(tx.SystemDate),
            SenderName = Coalesce(tx.SenderName, tx.SenderTitle),
            SenderTaxNo = Coalesce(tx.SenderTaxno, tx.PayerTaxNo),
            SenderIban = tx.SenderIban
        };
    }

    private static string NormalizeCurrency(string? fecName)
    {
        var c = (fecName ?? "TRY").Trim().ToUpperInvariant();
        return c switch
        {
            "TL" => "TRY",
            _ => c
        };
    }

    private static DateTime? ParseSystemDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss",
            "dd-MM-yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm:ss"
        };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt.ToUniversalTime();
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return dt.ToUniversalTime();
        return null;
    }

    private static string? Coalesce(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }
}
