using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KUYUMCU.Price_Service.Services;

public sealed class VomsisApiClient
{
    private const int MaxDateWindowDays = 7;

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
        if (_http.BaseAddress is null)
        {
            var baseUrl = (_config["Vomsis:BaseUrl"] ?? "https://developers.vomsis.com/").Trim();
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            _http.BaseAddress = new Uri(baseUrl);
        }
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

        var filtered = merged.Values
            .Where(tx => IsWithinUtcRange(tx, beginUtc, endUtc))
            .ToList();

        _logger.LogInformation(
            "Vomsis: {Begin:yyyy-MM-dd}..{End:yyyy-MM-dd} aralığında {Count} hareket alındı.",
            rangeBegin, rangeEnd, filtered.Count);

        return filtered;
    }

    private static bool IsWithinUtcRange(VomsisTransaction tx, DateTime beginUtc, DateTime endUtc)
    {
        var dt = VomsisTransactionMapper.ToImportDto(tx).TransactionDateUtc;
        if (!dt.HasValue)
            return true;

        return dt.Value >= beginUtc && dt.Value <= endUtc;
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

    /// <summary>
    /// Vomsis dekont/bildirim formundaki İLGİLİ VKN/TCKN alanları liste yanıtında boş olabilir;
    /// transaction detail endpoint'inden tamamlanır.
    /// </summary>
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
                body.Length > 300 ? body[..300] : body);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var statusEl) &&
                !string.Equals(statusEl.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Vomsis transaction detail başarısız (id={Id}): {Body}", transactionId, body);
                return null;
            }

            var txElement = ResolveTransactionElement(root);
            if (txElement is null)
                return null;

            var tx = JsonSerializer.Deserialize<VomsisTransaction>(txElement.Value.GetRawText(), JsonOptions);
            if (tx is null)
                return null;

            var taxFromJson = VomsisTaxFieldHelper.ExtractTaxNoFromJson(txElement.Value.GetRawText());
            if (!string.IsNullOrWhiteSpace(taxFromJson))
                tx.SenderTaxno = VomsisTaxFieldHelper.ResolveTaxNo(tx.SenderTaxno, tx.PayerTaxNo, taxFromJson) ?? taxFromJson;

            var titleFromJson = VomsisTaxFieldHelper.ExtractTitleFromJson(txElement.Value.GetRawText());
            if (!string.IsNullOrWhiteSpace(titleFromJson))
            {
                tx.RelatedTitle ??= titleFromJson;
                tx.SenderTitle ??= titleFromJson;
            }

            var branchFromJson = VomsisTaxFieldHelper.ExtractBranchNameFromJson(txElement.Value.GetRawText());
            if (!string.IsNullOrWhiteSpace(branchFromJson))
            {
                tx.BranchName ??= branchFromJson;
                tx.SubeAdi ??= branchFromJson;
            }

            return tx;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vomsis transaction detail parse hatası (id={Id})", transactionId);
            return null;
        }
    }

    private static JsonElement? ResolveTransactionElement(JsonElement root)
    {
        if (root.TryGetProperty("transaction", out var transaction))
            return transaction;
        if (root.TryGetProperty("data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object)
                return data;
            if (data.TryGetProperty("transaction", out var nested))
                return nested;
        }

        if (root.TryGetProperty("id", out _) || root.TryGetProperty("key", out _))
            return root;

        return null;
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_token) && DateTime.UtcNow < _tokenExpiresUtc)
            return;

        var appKey = _runtimeAppKey ?? _config["Vomsis:AppKey"]
            ?? throw new InvalidOperationException("Vomsis AppKey tanımlı değil.");
        var appSecret = _runtimeAppSecret ?? _config["Vomsis:AppSecret"]
            ?? throw new InvalidOperationException("Vomsis AppSecret tanımlı değil.");

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

    [JsonPropertyName("branch_name")]
    public string? BranchName { get; set; }

    [JsonPropertyName("sube_adi")]
    public string? SubeAdi { get; set; }

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

    [JsonPropertyName("identity_number")]
    public string? IdentityNumber { get; set; }
}

public static class VomsisTransactionMapper
{
    public static VomsisTransactionImportDto ToImportDto(VomsisTransaction tx)
    {
        var branchName = ResolveBranchName(tx);
        var (branchCity, branchDistrict) = VomsisTaxFieldHelper.ParseCityDistrictFromBranchName(branchName);
        return new VomsisTransactionImportDto
        {
            ExternalId = tx.Id,
            ExternalKey = string.IsNullOrWhiteSpace(tx.Key) ? tx.Id.ToString(CultureInfo.InvariantCulture) : tx.Key.Trim(),
            VomsisAccountId = tx.BankAccountId,
            Amount = tx.Amount,
            Currency = NormalizeCurrency(tx.FecName),
            Type = tx.Type,
            Description = tx.Description,
            TransactionDateUtc = ParseSystemDate(tx.SystemDate),
            SenderName = Coalesce(tx.RelatedTitle, tx.IlgiliUnvan, tx.SenderName)?.Trim(),
            SenderTitle = Coalesce(tx.RelatedTitle, tx.IlgiliUnvan, tx.SenderTitle)?.Trim(),
            SenderTaxNo = ResolveTaxNo(tx),
            SenderIban = tx.SenderIban,
            BankBranchName = branchName,
            BankBranchCity = branchCity,
            BankBranchDistrict = branchDistrict
        };
    }

    public static VomsisTransactionImportDto MergeDetailIntoImportDto(VomsisTransactionImportDto current, VomsisTransaction detail)
    {
        var mergedTax = ResolveTaxNo(detail) ?? current.SenderTaxNo;
        var mergedTitle = Coalesce(detail.RelatedTitle, detail.IlgiliUnvan, detail.SenderTitle, detail.SenderName, current.SenderTitle, current.SenderName);
        var mergedBranchName = Coalesce(ResolveBranchName(detail), current.BankBranchName);
        var (branchCity, branchDistrict) = VomsisTaxFieldHelper.ParseCityDistrictFromBranchName(mergedBranchName);
        return new VomsisTransactionImportDto
        {
            ExternalId = current.ExternalId,
            ExternalKey = current.ExternalKey,
            VomsisAccountId = current.VomsisAccountId,
            Amount = current.Amount,
            Currency = current.Currency,
            Type = current.Type,
            Description = Coalesce(detail.Description, current.Description),
            TransactionDateUtc = current.TransactionDateUtc,
            SenderTaxNo = mergedTax,
            SenderTitle = mergedTitle?.Trim(),
            SenderName = Coalesce(mergedTitle, current.SenderName)?.Trim(),
            SenderIban = Coalesce(detail.SenderIban, current.SenderIban),
            BankBranchName = mergedBranchName,
            BankBranchCity = Coalesce(branchCity, current.BankBranchCity),
            BankBranchDistrict = Coalesce(branchDistrict, current.BankBranchDistrict)
        };
    }

    private static string? ResolveBranchName(VomsisTransaction tx)
        => Coalesce(tx.BranchName, tx.SubeAdi);

    private static string? ResolveTaxNo(VomsisTransaction tx)
        => VomsisTaxFieldHelper.ResolveTaxNo(
            tx.SenderTaxno,
            tx.SenderTckn,
            tx.PayerTaxNo,
            tx.PayerTckn,
            tx.RelatedVkn,
            tx.RelatedTckn,
            tx.IlgiliVkn,
            tx.IlgiliTckn,
            tx.TcKimlikNo,
            tx.IdentityNumber,
            tx.ReceiverTaxno,
            tx.ReceiverTaxNoAlt);

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
        => VomsisDateHelper.ParseSystemDateToUtc(value);

    private static string? Coalesce(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }
}
