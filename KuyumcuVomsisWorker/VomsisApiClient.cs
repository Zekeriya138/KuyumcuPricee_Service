using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KuyumcuVomsisWorker;

public sealed class VomsisApiClient
{
    private const int MaxDateWindowDays = 7;

    private static readonly Regex TaxDigitsRegex = new(
        @"\b(\d{10}|\d{11})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<VomsisApiClient> _logger;
    private string? _token;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;
    private string? _runtimeAppKey;
    private string? _runtimeAppSecret;

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

    public async Task<IReadOnlyList<VomsisTransaction>> GetTransactionsAsync(DateTime beginUtc, DateTime endUtc, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var rangeBegin = beginUtc.ToLocalTime().Date;
        var rangeEnd = endUtc.ToLocalTime().Date;
        if (rangeEnd < rangeBegin)
            (rangeBegin, rangeEnd) = (rangeEnd, rangeBegin);

        var merged = new Dictionary<long, VomsisTransaction>();
        var windowEnd = rangeEnd;

        while (windowEnd >= rangeBegin)
        {
            var windowBegin = windowEnd.AddDays(-(MaxDateWindowDays - 1));
            var batch = await FetchTransactionsWindowAsync(windowBegin, windowEnd, ct);
            foreach (var tx in batch)
                merged[tx.Id] = tx;

            if (windowBegin <= rangeBegin)
                break;

            windowEnd = windowBegin.AddDays(-1);
        }

        return merged.Values
            .Where(tx => IsWithinUtcRange(tx, beginUtc, endUtc))
            .ToList();
    }

    public async Task<VomsisTransaction?> GetTransactionDetailAsync(long transactionId, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var url = $"api/v2/transactions/{transactionId.ToString(CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Vomsis transaction detail HTTP {Code} for id {Id}: {Body}",
                (int)resp.StatusCode,
                transactionId,
                Truncate(body, 400));
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var statusEl) &&
                !string.Equals(statusEl.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Vomsis transaction detail başarısız (id={Id}): {Body}", transactionId, Truncate(body, 400));
                return null;
            }

            var txElement = ResolveTransactionElement(root) ?? root;
            var tx = JsonSerializer.Deserialize<VomsisTransaction>(txElement.GetRawText(), JsonOptions) ?? new VomsisTransaction { Id = transactionId };

            var taxFromJson = ExtractBestTaxNo(txElement);
            if (!string.IsNullOrWhiteSpace(taxFromJson))
                tx.SenderTaxno = taxFromJson;

            var titleFromJson = ExtractTitleFromElement(txElement);
            if (!string.IsNullOrWhiteSpace(titleFromJson))
            {
                tx.RelatedTitle ??= titleFromJson;
                tx.SenderTitle ??= titleFromJson;
            }

            if (txElement.ValueKind == JsonValueKind.Object)
            {
                if (txElement.TryGetProperty("sender_branch", out var senderBranchEl) &&
                    senderBranchEl.ValueKind == JsonValueKind.String)
                    tx.SenderBranch ??= senderBranchEl.GetString();
                if (txElement.TryGetProperty("receiver_branch", out var receiverBranchEl) &&
                    receiverBranchEl.ValueKind == JsonValueKind.String)
                    tx.ReceiverBranch ??= receiverBranchEl.GetString();
                if (txElement.TryGetProperty("sender_identity_number", out var senderIdentityEl) &&
                    senderIdentityEl.ValueKind == JsonValueKind.String)
                    tx.SenderIdentityNumber ??= senderIdentityEl.GetString();
                if (txElement.TryGetProperty("opponent_taxno", out var opponentTaxEl) &&
                    opponentTaxEl.ValueKind == JsonValueKind.String)
                    tx.OpponentTaxNo ??= opponentTaxEl.GetString();
                if (txElement.TryGetProperty("opponent_tax_no", out var opponentTaxAltEl) &&
                    opponentTaxAltEl.ValueKind == JsonValueKind.String)
                    tx.OpponentTaxNo ??= opponentTaxAltEl.GetString();
            }

            // Karşı taraf şubesi: kendi hesap (account) şubesini ASLA alma.
            var branchFromJson = ExtractCounterpartyBranchName(txElement);
            if (!string.IsNullOrWhiteSpace(branchFromJson))
            {
                tx.BranchName ??= branchFromJson;
                tx.SubeAdi ??= branchFromJson;
            }

            var resolvedTax = VomsisTransactionMapper.ResolveCounterpartyTaxNo(tx);
            if (string.IsNullOrWhiteSpace(resolvedTax))
            {
                var keys = txElement.ValueKind == JsonValueKind.Object
                    ? string.Join(",", txElement.EnumerateObject().Select(p => p.Name).Take(40))
                    : txElement.ValueKind.ToString();
                _logger.LogWarning(
                    "Vomsis detail'de TCKN/VKN bulunamadı (id={Id}). Keys={Keys}. BodyPreview={Body}",
                    transactionId,
                    keys,
                    Truncate(body, 800));
            }
            else
            {
                tx.SenderTaxno = resolvedTax;
                _logger.LogInformation(
                    "Vomsis detail TCKN/VKN bulundu (id={Id}, tax={Tax}, branch={Branch})",
                    transactionId,
                    resolvedTax,
                    Coalesce(VomsisTransactionMapper.ResolveBranchName(tx)) ?? "-");
            }

            return tx;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vomsis transaction detail parse hatası (id={Id})", transactionId);
            return null;
        }
    }

    private async Task<IReadOnlyList<VomsisTransaction>> FetchTransactionsWindowAsync(
        DateTime windowBeginLocal,
        DateTime windowEndLocal,
        CancellationToken ct)
    {
        var begin = windowBeginLocal.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        var end = windowEndLocal.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
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

    private static JsonElement? ResolveTransactionElement(JsonElement root)
    {
        if (root.TryGetProperty("transaction", out var transaction))
            return UnwrapObjectOrFirstArrayItem(transaction);
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("transaction", out var nested))
                    return UnwrapObjectOrFirstArrayItem(nested);
                return data;
            }

            if (data.ValueKind == JsonValueKind.Array)
                return UnwrapObjectOrFirstArrayItem(data);
        }

        if (root.TryGetProperty("id", out _) || root.TryGetProperty("key", out _))
            return root;

        return null;
    }

    private static JsonElement? UnwrapObjectOrFirstArrayItem(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                    return item;
            }
        }

        return null;
    }

    private static string? ExtractBestTaxNo(JsonElement element)
    {
        var preferred = ExtractTaxByPreferredKeys(element);
        if (!string.IsNullOrWhiteSpace(preferred))
            return preferred;

        return ExtractAnyTaxDigits(element);
    }

    private static string? ExtractTaxByPreferredKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var prop in element.EnumerateObject())
        {
            var name = prop.Name;
            var looksTax =
                name.Contains("tax", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("vkn", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("tckn", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("kimlik", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("vergi", StringComparison.OrdinalIgnoreCase);

            if (looksTax)
            {
                var digits = DigitsFromJsonValue(prop.Value);
                if (digits.Length is 10 or 11)
                    return digits;
            }

            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var nested = ExtractTaxByPreferredKeys(prop.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? ExtractAnyTaxDigits(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var nested = ExtractAnyTaxDigits(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ExtractAnyTaxDigits(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            {
                var digits = DigitsFromJsonValue(element);
                if (digits.Length is 10 or 11)
                    return digits;
                break;
            }
        }

        return null;
    }

    private static string DigitsFromJsonValue(JsonElement value)
    {
        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var match = TaxDigitsRegex.Match(raw);
        if (match.Success)
            return match.Groups[1].Value;

        return new string(raw.Where(char.IsDigit).ToArray());
    }

    private static string? ExtractTitleFromElement(JsonElement element)
    {
        foreach (var key in new[] { "related_title", "ilgili_unvan", "sender_title", "sender_name", "title", "payer_name", "opponent_title", "receiver_name", "reciever_name" })
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(key, out var el) &&
                el.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(el.GetString()))
                return el.GetString()!.Trim();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var nested = ExtractTitleFromElement(prop.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Karşı taraf şube adı. Kendi banka hesabının account.branch_name değerini bilerek yok sayar.
    /// </summary>
    private static string? ExtractCounterpartyBranchName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[]
                 {
                     "sender_branch", "receiver_branch", "opponent_branch",
                     "branch_name", "sube_adi", "sube_ad", "bank_branch_name"
                 })
        {
            if (element.TryGetProperty(key, out var el) &&
                el.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(el.GetString()))
            {
                var text = el.GetString()!.Trim();
                if (text.Contains('/') || key.Contains("branch", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("sube", StringComparison.OrdinalIgnoreCase))
                    return text;
            }
        }

        // İç içe alanlar — "account" kendi şubemiz, atla.
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, "account", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, "bank_account", StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                var nested = ExtractCounterpartyBranchName(prop.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? ResolveTaxNo(params string?[] values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length is 10 or 11)
                return digits;
        }

        return null;
    }

    private static string? Coalesce(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..max];
    }

    private static bool IsWithinUtcRange(VomsisTransaction tx, DateTime beginUtc, DateTime endUtc)
    {
        var dt = ParseSystemDate(tx.SystemDate);
        if (!dt.HasValue)
            return true;

        return dt.Value >= beginUtc && dt.Value <= endUtc;
    }

    private static DateTime? ParseSystemDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss",
            "dd-MM-yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm:ss",
            "dd.MM.yyyy HH:mm"
        };

        TimeZoneInfo turkey;
        try { turkey = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        catch
        {
            try { turkey = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
            catch { turkey = TimeZoneInfo.CreateCustomTimeZone("Turkey", TimeSpan.FromHours(3), "Turkey", "Turkey"); }
        }

        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), turkey);
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), turkey);
        return null;
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

    [JsonPropertyName("related_vkn")]
    public string? RelatedVkn { get; set; }

    [JsonPropertyName("ilgili_vkn")]
    public string? IlgiliVkn { get; set; }

    [JsonPropertyName("related_title")]
    public string? RelatedTitle { get; set; }

    [JsonPropertyName("ilgili_unvan")]
    public string? IlgiliUnvan { get; set; }

    [JsonPropertyName("receiver_taxno")]
    public string? ReceiverTaxno { get; set; }

    [JsonPropertyName("receiver_tax_no")]
    public string? ReceiverTaxNoAlt { get; set; }

    [JsonPropertyName("ilgili_tckn")]
    public string? IlgiliTckn { get; set; }

    [JsonPropertyName("related_tckn")]
    public string? RelatedTckn { get; set; }

    [JsonPropertyName("sender_tckn")]
    public string? SenderTckn { get; set; }

    [JsonPropertyName("payer_tckn")]
    public string? PayerTckn { get; set; }

    [JsonPropertyName("tc_kimlik_no")]
    public string? TcKimlikNo { get; set; }

    [JsonPropertyName("branch_name")]
    public string? BranchName { get; set; }

    [JsonPropertyName("sube_adi")]
    public string? SubeAdi { get; set; }

    [JsonPropertyName("opponent_title")]
    public string? OpponentTitle { get; set; }

    [JsonPropertyName("opponent_taxno")]
    public string? OpponentTaxNo { get; set; }

    [JsonPropertyName("sender_branch")]
    public string? SenderBranch { get; set; }

    [JsonPropertyName("receiver_branch")]
    public string? ReceiverBranch { get; set; }

    [JsonPropertyName("sender_identity_number")]
    public string? SenderIdentityNumber { get; set; }

    [JsonPropertyName("identity_number")]
    public string? IdentityNumber { get; set; }
}
