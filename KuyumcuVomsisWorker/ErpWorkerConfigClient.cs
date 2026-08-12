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

    public Task<RemoteWorkerConfig?> FetchAsync(CancellationToken ct)
        => FetchAsync(request: null, ct);

    public async Task<RemoteWorkerConfig?> FetchAsync(VomsisSyncRunRequest? request, CancellationToken ct)
    {
        var bootstrap = ResolveBootstrap(request);
        if (bootstrap is null)
        {
            _logger.LogWarning("Bootstrap ERP ayarları eksik (BaseUrl/AppKey).");
            return null;
        }

        if (request?.BranchId is not { } branchId || branchId == Guid.Empty)
        {
            var fallbackBranchId = ParseOptionalGuid(bootstrap.BranchId);
            if (fallbackBranchId is null || fallbackBranchId == Guid.Empty)
            {
                _logger.LogWarning("Tek şube profili için BranchId gerekli.");
                return null;
            }

            branchId = fallbackBranchId.Value;
        }

        var tenantId = request?.TenantId
            ?? ParseOptionalGuid(bootstrap.TenantId)
            ?? Guid.Empty;
        if (tenantId == Guid.Empty)
        {
            _logger.LogWarning("Tek şube profili için TenantId gerekli.");
            return null;
        }

        var url = bootstrap.BaseUrl.TrimEnd('/') + "/api/bank-sync/profile/worker?branchId=" + Uri.EscapeDataString(branchId.ToString());
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        
        req.Headers.Add(bootstrap.AppKeyHeader, bootstrap.AppKey);
        req.Headers.Add("X-Tenant-Id", tenantId.ToString());
        req.Headers.Add("X-Branch-Id", branchId.ToString());

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("ERP'de banka sync profili yok veya devre dışı (şube {BranchId}).", branchId);
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Worker config HTTP {(int)resp.StatusCode}: {body}");

        return await resp.Content.ReadFromJsonAsync<RemoteWorkerConfig>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<RemoteWorkerBranchQueueItem>> FetchBranchQueueAsync(CancellationToken ct)
    {
        var bootstrap = ResolveBootstrap(null);
        if (bootstrap is null)
        {
            _logger.LogWarning("Bootstrap ERP ayarları eksik (BaseUrl/AppKey).");
            return Array.Empty<RemoteWorkerBranchQueueItem>();
        }

        var query = BuildBranchQueueQuery(bootstrap);
        using var req = new HttpRequestMessage(HttpMethod.Get, bootstrap.BaseUrl.TrimEnd('/') + "/api/bank-sync/profile/worker/branches" + query);
        
        req.Headers.Add(bootstrap.AppKeyHeader, bootstrap.AppKey);
        if (ParseOptionalGuid(bootstrap.TenantId) is { } tenantId && tenantId != Guid.Empty)
            req.Headers.Add("X-Tenant-Id", tenantId.ToString());

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Worker branch queue HTTP {(int)resp.StatusCode}: {body}");

        var items = await resp.Content.ReadFromJsonAsync<List<RemoteWorkerBranchQueueItem>>(cancellationToken: ct)
            ?? new List<RemoteWorkerBranchQueueItem>();

        return items;
    }

    public static bool IsManualSyncPending(RemoteWorkerBranchQueueItem branch, DateTime utcNow)
        => branch.HasPendingEnrich ||
           (branch.ManualSyncRequestedUtc is DateTime req &&
            (utcNow - req).TotalMinutes < 15);

    public static bool ShouldSyncBranch(RemoteWorkerBranchQueueItem branch, DateTime utcNow)
    {
        if (IsManualSyncPending(branch, utcNow))
            return true;

        if (!branch.LastWorkerSyncUtc.HasValue)
            return true;

        var intervalMinutes = Math.Clamp(branch.PollIntervalMinutes, 1, 60);
        return (utcNow - branch.LastWorkerSyncUtc.Value).TotalMinutes >= intervalMinutes;
    }

    private string BuildBranchQueueQuery(BootstrapSettings bootstrap)
    {
        var parts = new List<string>();

        if (ParseOptionalGuid(bootstrap.TenantId) is { } tenantId && tenantId != Guid.Empty)
            parts.Add("tenantId=" + Uri.EscapeDataString(tenantId.ToString()));

        var branchFilter = ResolveBranchIdFilter(bootstrap);
        if (branchFilter is { Count: > 0 })
            parts.Add("branchIds=" + Uri.EscapeDataString(string.Join(",", branchFilter)));

        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private IReadOnlyCollection<Guid>? ResolveBranchIdFilter(BootstrapSettings bootstrap)
    {
        if (!string.IsNullOrWhiteSpace(bootstrap.BranchIds))
        {
            var ids = bootstrap.BranchIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToList();
            if (ids.Count > 0)
                return ids;
        }

        if (ParseOptionalGuid(bootstrap.BranchId) is { } single && single != Guid.Empty)
            return [single];

        return null;
    }


    private BootstrapSettings? ResolveBootstrap(VomsisSyncRunRequest? request)
    {
        var baseUrl = FirstNonEmpty(
            request?.ErpApiBaseUrl,
            _config["Bootstrap:ErpApiBaseUrl"],
            _config["ErpApi:BaseUrl"]);
        var appKey = FirstNonEmpty(
            _config["Bootstrap:ErpApiAppKey"],
            _config["ErpApi:AppKey"]);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(appKey))
            return null;

        return new BootstrapSettings
        {
            BaseUrl = baseUrl.Trim(),
            AppKey = appKey.Trim(),
            AppKeyHeader = FirstNonEmpty(
                _config["Bootstrap:AppKeyHeader"],
                _config["ErpApi:AppKeyHeader"],
                "x-app-key") ?? "x-app-key",
            TenantId = FirstNonEmpty(
                request?.TenantId?.ToString(),
                _config["Bootstrap:TenantId"],
                _config["Sync:TenantId"]),
            BranchId = FirstNonEmpty(
                request?.BranchId?.ToString(),
                _config["Bootstrap:BranchId"],
                _config["Sync:BranchId"]),
            BranchIds = _config["Bootstrap:BranchIds"]
        };
    }

    private static Guid? ParseOptionalGuid(string? value)
        => Guid.TryParse(value, out var g) && g != Guid.Empty ? g : null;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }

    private sealed class BootstrapSettings
    {
        public string BaseUrl { get; set; } = "";
        public string AppKey { get; set; } = "";
        public string AppKeyHeader { get; set; } = "x-app-key";
        public string? TenantId { get; set; }
        public string? BranchId { get; set; }
        public string? BranchIds { get; set; }
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
    public int PollIntervalMinutes { get; set; } = 1;
    public int[] AllowedAccountIds { get; set; } = [];
    public int LookbackDays { get; set; } = 7;
    public DateTime? ManualSyncRequestedUtc { get; set; }
    public long[] PendingEnrichExternalIds { get; set; } = [];
}

public sealed class RemoteWorkerBranchQueueItem
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public DateTime? ManualSyncRequestedUtc { get; set; }
    public int PollIntervalMinutes { get; set; } = 1;
    public DateTime? LastWorkerSyncUtc { get; set; }
    public bool HasPendingEnrich { get; set; }
}
