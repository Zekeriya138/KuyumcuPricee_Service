using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuyumcuVomsisWorker;

public sealed class VomsisApiClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<VomsisApiClient> _logger;
    private string? _token;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public VomsisApiClient(HttpClient http, IConfiguration config, ILogger<VomsisApiClient> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
        _http.BaseAddress = new Uri(_config["Vomsis:BaseUrl"] ?? "https://developers.vomsis.com/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public void Configure(string appKey, string appSecret)
    {
        _runtimeAppKey = appKey;
        _runtimeAppSecret = appSecret;
        _token = null;
        _tokenExpiresUtc = DateTime.MinValue;
    }

    private string? _runtimeAppKey;
    private string? _runtimeAppSecret;

    public async Task<IReadOnlyList<VomsisTransaction>> GetTransactionsAsync(DateTime beginUtc, DateTime endUtc, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        var begin = beginUtc.ToLocalTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var end = endUtc.ToLocalTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var url = $"api/v2/transactions?beginDate={Uri.EscapeDataString(begin)}&endDate={Uri.EscapeDataString(end)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vomsis transactions HTTP {(int)resp.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize<VomsisTransactionsResponse>(body, JsonOptions);
        if (parsed is null || !string.Equals(parsed.Status, "success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Vomsis transactions yanıtı başarısız: " + body);

        return parsed.Transactions ?? [];
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_token) && DateTime.UtcNow < _tokenExpiresUtc)
            return;

        var appKey = _runtimeAppKey ?? _config["Vomsis:AppKey"] ?? throw new InvalidOperationException("Vomsis AppKey tanımlı değil.");
        var appSecret = _runtimeAppSecret ?? _config["Vomsis:AppSecret"] ?? throw new InvalidOperationException("Vomsis AppSecret tanımlı değil.");

        var payload = new { app_key = appKey, app_secret = appSecret };
        using var resp = await _http.PostAsJsonAsync("api/v2/authenticate", payload, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vomsis authenticate HTTP {(int)resp.StatusCode}: {body}");

        var auth = JsonSerializer.Deserialize<VomsisAuthResponse>(body, JsonOptions);
        if (auth is null || !string.Equals(auth.Status, "success", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(auth.Token))
            throw new InvalidOperationException("Vomsis authenticate başarısız: " + body);

        _token = auth.Token;
        _tokenExpiresUtc = DateTime.UtcNow.AddHours(23);
        _logger.LogInformation("Vomsis token alındı.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

public sealed class VomsisAuthResponse
{
    public string? Status { get; set; }
    public string? Token { get; set; }
}

public sealed class VomsisTransactionsResponse
{
    public string? Status { get; set; }
    public List<VomsisTransaction>? Transactions { get; set; }
}

public sealed class VomsisTransaction
{
    public long Id { get; set; }
    public string? Key { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? Type { get; set; }

    [JsonPropertyName("fec_name")]
    public string? FecName { get; set; }

    [JsonPropertyName("bank_account_id")]
    public int? BankAccountId { get; set; }

    [JsonPropertyName("system_date")]
    public string? SystemDate { get; set; }

    [JsonPropertyName("sender_name")]
    public string? SenderName { get; set; }

    [JsonPropertyName("sender_title")]
    public string? SenderTitle { get; set; }

    [JsonPropertyName("sender_iban")]
    public string? SenderIban { get; set; }

    [JsonPropertyName("sender_taxno")]
    public string? SenderTaxno { get; set; }

    [JsonPropertyName("payer_tax_no")]
    public string? PayerTaxNo { get; set; }
}
