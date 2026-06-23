using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kuyumcu.PriceService.Services;

/// <summary>
/// Harem feed (AltinApi upstream) — GET /api/v1/prices, header X-API-Key.
/// </summary>
public sealed class HaremApiClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<HaremApiClient> _log;

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public HaremApiClient(HttpClient http, IConfiguration cfg, ILogger<HaremApiClient> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Tüm fiyat listesi; ağ/401 hatalarında boş liste.</summary>
    public async Task<HaremPricesResult> FetchPricesAsync(CancellationToken ct = default)
    {
        var baseUrl = (_cfg["Upstream:HaremApi:BaseUrl"] ?? "https://altinapi.com/api/v1").TrimEnd('/');
        var apiKey = ResolveApiKey();

        var url = $"{baseUrl}/prices";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-API-Key", apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var qUrl = $"{baseUrl}/prices?api_key={Uri.EscapeDataString(apiKey)}";
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, qUrl);
                    using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
                    body = await resp2.Content.ReadAsStringAsync(ct);
                    if (!resp2.IsSuccessStatusCode)
                    {
                        _log.LogWarning("HaremAPI (header+query) {Status}: {Body}", (int)resp2.StatusCode,
                            body.Length > 200 ? body[..200] : body);
                        return HaremPricesResult.Empty;
                    }
                }
                else
                {
                    _log.LogWarning("HaremAPI {Status}: {Body}", (int)resp.StatusCode, body.Length > 200 ? body[..200] : body);
                    return HaremPricesResult.Empty;
                }
            }

            var (rows, updatedAt, stale) = ParseHaremBody(body, _log);
            if (rows.Count == 0 && body.Length > 20)
                _log.LogWarning("Harem 200 ama satır yok. Gövde (ilk 500 karakter): {Head}",
                    body.Length > 500 ? body[..500] : body);
            else
                _log.LogInformation("Harem fiyat: {Count} satır (stale={Stale}).", rows.Count, stale);

            return new HaremPricesResult(rows, updatedAt, stale);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "HaremAPI isteği başarısız.");
            return HaremPricesResult.Empty;
        }
    }

    /// <summary>Swagger teşhisi: Harem’den gelen gerçek HTTP kodu, gövde özeti, örnek satırlar (anahtar dönmez).</summary>
    public async Task<HaremProbeDetail> ProbeRemoteAsync(CancellationToken ct = default)
    {
        var baseUrl = (_cfg["Upstream:HaremApi:BaseUrl"] ?? "https://altinapi.com/api/v1").TrimEnd('/');
        var key = ResolveApiKey();
        var d = new HaremProbeDetail
        {
            HaremUrl = $"{baseUrl}/prices",
            ApiKeyConfigured = key.Length > 0,
            ApiKeyLength = key.Length
        };

        if (key.Length == 0)
        {
            d.RemoteError = "ApiKey boş (Upstream:HaremApi:ApiKey / ortam / yedek).";
            return d;
        }

        string body;
        var authMode = "X-API-Key header";
        try
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/prices"))
            {
                req.Headers.TryAddWithoutValidation("X-API-Key", key);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                d.RemoteHttpStatus = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    authMode = "?api_key=... (401/403 sonrası, doküman yedeği)";
                    var qUrl = $"{baseUrl}/prices?api_key={Uri.EscapeDataString(key)}";
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, qUrl);
                    using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
                    d.RemoteHttpStatus = (int)resp2.StatusCode;
                    body = await resp2.Content.ReadAsStringAsync(ct);
                }
            }

            d.AuthMode = authMode;
            d.BodyPreview = body.Length > 800 ? body[..800] : body;

            if (d.RemoteHttpStatus is < 200 or >= 300)
            {
                d.RemoteError = $"Harem HTTP {d.RemoteHttpStatus}";
                return d;
            }

            var (rows, updatedAt, stale) = ParseHaremBody(body, _log);
            d.ParsedRowCount = rows.Count;
            d.UpdatedAt = updatedAt;
            d.Stale = stale;
            foreach (var x in rows.Take(8))
                d.Sample.Add(new HaremProbeSample { Symbol = x.Symbol, Bid = x.Bid, Ask = x.Ask });
        }
        catch (Exception ex)
        {
            d.RemoteHttpStatus ??= 0;
            d.RemoteError = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException is { } i)
                d.RemoteError += $" | {i.GetType().Name}: {i.Message}";
        }

        return d;
    }

    /// <summary>Harem anahtarları <c>hapi_</c> ile başlar ve genelde 30+ karakterdir. Kısa/bozuk değerler (User Secrets ezmesi) yok sayılır.</summary>
    private static bool LooksLikeHaremApiKey(string? value)
    {
        var t = (value ?? "").Trim();
        return t.Length >= 18 && t.StartsWith("hapi_", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveApiKey()
    {
        var env1 = Environment.GetEnvironmentVariable("HAREM_API_KEY");
        var env2 = Environment.GetEnvironmentVariable("Upstream__HaremApi__ApiKey");
        if (LooksLikeHaremApiKey(env1)) return env1!.Trim();
        if (LooksLikeHaremApiKey(env2)) return env2!.Trim();

        var cfgKey = (_cfg["Upstream:HaremApi:ApiKey"] ?? "").Trim();
        if (!string.IsNullOrEmpty(cfgKey) && !LooksLikeHaremApiKey(cfgKey))
            _log.LogWarning(
                "Upstream:HaremApi:ApiKey geçersiz veya çok kısa ({Len} karakter). Muhtemelen user-secrets veya ortam eski değeri eziyor. dotnet user-secrets list ile kontrol edin.",
                cfgKey.Length);

        if (LooksLikeHaremApiKey(cfgKey)) return cfgKey;

        const string devFallback = "hapi_9da8d32f28244fd5a0e4daa20615633c";
        if (LooksLikeHaremApiKey(devFallback))
            return devFallback;

        return cfgKey.Length > 0 ? cfgKey : (env1 ?? env2 ?? "").Trim();
    }

    /// <summary>Önce POCO ayrıştırma; boş veya hata olursa <see cref="JsonDocument"/> ile satır satır (tek kötü satır tüm listeyi düşürmez).</summary>
    private static (List<HaremPriceItem> Rows, string? UpdatedAt, bool Stale) ParseHaremBody(string body, ILogger<HaremApiClient> log)
    {
        List<HaremPriceItem>? rows = null;
        string? updatedAt = null;
        bool stale = false;

        try
        {
            var doc = JsonSerializer.Deserialize<HaremPricesResponse>(body, JsonOpt);
            rows = doc?.Data;
            updatedAt = doc?.UpdatedAt;
            stale = doc?.Stale ?? false;
        }
        catch (JsonException jex)
        {
            log.LogWarning(jex, "Harem POCO ayrıştırma başarısız, JsonDocument yedeğine geçiliyor.");
        }

        if (rows is { Count: > 0 })
            return (rows, updatedAt, stale);

        try
        {
            using var j = JsonDocument.Parse(body);
            var root = j.RootElement;
            if (root.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.String)
                updatedAt = u.GetString();
            if (root.TryGetProperty("stale", out var st) && st.ValueKind is JsonValueKind.True or JsonValueKind.False)
                stale = st.GetBoolean();

            var list = new List<HaremPriceItem>(64);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (list, updatedAt, stale);

            foreach (var el in data.EnumerateArray())
            {
                if (TryParsePriceRow(el, out var item))
                    list.Add(item);
            }

            if (list.Count > 0)
                log.LogInformation("Harem JsonDocument yedeği: {Count} satır.", list.Count);

            return (list, updatedAt, stale);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Harem JsonDocument ayrıştırma da başarısız.");
            return (new List<HaremPriceItem>(), updatedAt, stale);
        }
    }

    private static bool TryParsePriceRow(JsonElement el, out HaremPriceItem item)
    {
        item = new HaremPriceItem();
        if (el.ValueKind != JsonValueKind.Object)
            return false;

        var sym = ReadStringProp(el, "symbol");
        if (string.IsNullOrWhiteSpace(sym))
            return false;

        item.Symbol = sym.Trim();
        item.Category = ReadStringProp(el, "category");
        item.Bid = ReadDecimalProp(el, "bid");
        item.Ask = ReadDecimalProp(el, "ask");
        item.Timestamp = ReadTimestampProp(el, "timestamp");
        return true;
    }

    private static string ReadStringProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return "";
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? "",
            JsonValueKind.Number => p.GetRawText(),
            _ => ""
        };
    }

    private static decimal ReadDecimalProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return 0m;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d))
            return d;
        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var x))
                return x;
        }
        return 0m;
    }

    private static long ReadTimestampProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n))
            return n;
        if (p.ValueKind == JsonValueKind.String)
        {
            var s = p.GetString();
            if (!string.IsNullOrWhiteSpace(s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                return ms;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }
        return 0;
    }
}

public sealed record HaremPricesResult(
    IReadOnlyList<HaremPriceItem> Items,
    string? UpdatedAt,
    bool Stale)
{
    public static HaremPricesResult Empty { get; } = new(Array.Empty<HaremPriceItem>(), null, false);
}

/// <summary><see cref="HaremApiClient.ProbeRemoteAsync"/> JSON çıktısı (API anahtarı içermez).</summary>
public sealed class HaremProbeDetail
{
    public string HaremUrl { get; set; } = "";
    public bool ApiKeyConfigured { get; set; }
    public int ApiKeyLength { get; set; }
    public string AuthMode { get; set; } = "";
    public int? RemoteHttpStatus { get; set; }
    public string? RemoteError { get; set; }
    public string? BodyPreview { get; set; }
    public int ParsedRowCount { get; set; }
    public string? UpdatedAt { get; set; }
    public bool Stale { get; set; }
    public List<HaremProbeSample> Sample { get; set; } = new();
}

public sealed class HaremProbeSample
{
    public string Symbol { get; set; } = "";
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
}

public sealed class HaremPricesResponse
{
    [JsonPropertyName("data")]
    public List<HaremPriceItem>? Data { get; set; }

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("stale")]
    public bool Stale { get; set; }
}

public sealed class HaremPriceItem
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("bid")]
    [JsonConverter(typeof(HaremJsonDecimalConverter))]
    public decimal Bid { get; set; }

    [JsonPropertyName("ask")]
    [JsonConverter(typeof(HaremJsonDecimalConverter))]
    public decimal Ask { get; set; }

    /// <summary>Harem bazen Unix ms (sayı), bazen ISO-8601 string döndürür.</summary>
    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(HaremTimestampJsonConverter))]
    public long Timestamp { get; set; }
}

/// <summary><c>timestamp</c> alanı için sayı veya ISO tarih string desteği.</summary>
internal sealed class HaremTimestampJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var n) ? n : 0;
            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return 0;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                    return ms;
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                return 0;
            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary><c>bid</c>/<c>ask</c> için null veya string sayı desteği (Harem satırlarında farklılık olabiliyor).</summary>
internal sealed class HaremJsonDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return 0m;
            case JsonTokenType.Number:
                return reader.GetDecimal();
            case JsonTokenType.String:
                var s = reader.GetString();
                return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
            default:
                return 0m;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
