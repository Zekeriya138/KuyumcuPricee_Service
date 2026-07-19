using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace KuyumcuVomsisWorker;

public sealed class ErpWorkerConfigClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<ErpWorkerConfigClient> _logger;

    public ErpWorkerConfigClient(HttpClient http, IConfiguration config, ILogger<ErpWorkerConfigClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public async Task<RemoteWorkerConfig?> FetchAsync(CancellationToken ct)
    {
        var baseUrl = _config["Bootstrap:ErpApiBaseUrl"] ?? _config["ErpApi:BaseUrl"];
        var appKey = _config["Bootstrap:ErpApiAppKey"] ?? _config["ErpApi:AppKey"];
        var tenantId = _config["Bootstrap:TenantId"] ?? _config["Sync:TenantId"];
        var branchId = _config["Bootstrap:BranchId"] ?? _config["Sync:BranchId"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(appKey) ||
            string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(branchId))
        {
            _logger.LogWarning("Bootstrap ERP ayarları eksik (BaseUrl/AppKey/TenantId/BranchId).");
            return null;
        }

        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        var headerName = _config["Bootstrap:AppKeyHeader"] ?? _config["ErpApi:AppKeyHeader"] ?? "x-app-key";
        _http.DefaultRequestHeaders.Remove(headerName);
        _http.DefaultRequestHeaders.Add(headerName, appKey);
        _http.DefaultRequestHeaders.Remove("X-Tenant-Id");
        _http.DefaultRequestHeaders.Remove("X-Branch-Id");
        _http.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        _http.DefaultRequestHeaders.Add("X-Branch-Id", branchId);

        using var resp = await _http.GetAsync("api/bank-sync/profile/worker?branchId=" + Uri.EscapeDataString(branchId), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("ERP'de banka sync profili yok veya devre dışı.");
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Worker config HTTP {(int)resp.StatusCode}: {body}");

        return await resp.Content.ReadFromJsonAsync<RemoteWorkerConfig>(cancellationToken: ct);
    }
}

public sealed class RemoteWorkerConfig
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public bool IsEnabled { get; set; }
    public string? VomsisAppKey { get; set; }
    public string? VomsisAppSecret { get; set; }
    public string ErpApiBaseUrl { get; set; } = "";
    public string? ErpApiAppKey { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
    public int[] AllowedAccountIds { get; set; } = [];
    public int LookbackDays { get; set; } = 7;
}
