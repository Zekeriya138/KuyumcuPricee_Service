using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KUYUMCU.Price_Service.Services;

public sealed class VomsisWorkerProxyClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<VomsisWorkerProxyClient> _logger;

    public VomsisWorkerProxyClient(HttpClient http, IConfiguration config, ILogger<VomsisWorkerProxyClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config["BankSync:WorkerTriggerUrl"]);

    public async Task<BankSyncPullResult> TriggerSyncAsync(
        Guid tenantId,
        Guid branchId,
        string? erpApiBaseUrl,
        CancellationToken ct)
    {
        var workerUrl = (_config["BankSync:WorkerTriggerUrl"] ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(workerUrl))
            throw new InvalidOperationException("Vomsis worker adresi (BankSync:WorkerTriggerUrl) tanımlı değil.");

        var query = new List<string>
        {
            "tenantId=" + Uri.EscapeDataString(tenantId.ToString()),
            "branchId=" + Uri.EscapeDataString(branchId.ToString())
        };
        if (!string.IsNullOrWhiteSpace(erpApiBaseUrl))
            query.Add("erpApiBaseUrl=" + Uri.EscapeDataString(erpApiBaseUrl.Trim().TrimEnd('/')));

        using var req = new HttpRequestMessage(HttpMethod.Post, workerUrl + "/sync?" + string.Join("&", query));
        var syncKey = _config["BankSync:WorkerSyncKey"];
        if (!string.IsNullOrWhiteSpace(syncKey))
            req.Headers.Add("x-sync-key", syncKey);

        _logger.LogInformation("Vomsis worker senkron tetikleniyor: {Url}", workerUrl);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var error = TryReadError(body) ?? body;
            throw new InvalidOperationException($"Vomsis worker senkron hatası: {error}");
        }

        var workerResult = JsonSerializer.Deserialize<WorkerSyncResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Vomsis worker yanıtı okunamadı.");

        return new BankSyncPullResult
        {
            FetchedFromVomsis = workerResult.FetchedFromVomsis,
            Received = workerResult.Received,
            Imported = workerResult.Imported,
            SkippedDuplicate = workerResult.SkippedDuplicate,
            SkippedFilter = workerResult.SkippedFilter,
            DraftCreated = workerResult.DraftCreated,
            PendingReview = workerResult.PendingReview,
            MissingTaxId = workerResult.MissingTaxId,
            NoCustomerMatch = workerResult.NoCustomerMatch,
            SummaryMessage = workerResult.SummaryMessage ??
                $"Vomsis worker: {workerResult.FetchedFromVomsis} hareket, ERP'ye {workerResult.Imported} kayıt."
        };
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
        }
        catch
        {
            // ignore parse errors
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class WorkerSyncResponse
    {
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
    }
}
