using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    public void Configure(RemoteWorkerConfig remote)
    {
        // HttpClient BaseAddress / DefaultRequestHeaders ilk istekten sonra değiştirilemez.
        // Ayarları yalnızca bellekte tutup her istekte absolute URL + header kullanıyoruz.
        _remote = remote;
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

        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            "api/bank-sync/vomsis/import",
            payload,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ERP import HTTP {(int)resp.StatusCode}: {body}");

        var result = JsonSerializer.Deserialize<ErpImportResult>(body, JsonOptions);
        _logger.LogInformation(
            "ERP import: received={Received}, imported={Imported}, drafts={Drafts}, pending={Pending}",
            result?.Received, result?.Imported, result?.DraftCreated, result?.PendingReview);
        return result;
    }

    public async Task CompleteManualSyncAsync(VomsisSyncRunResult result, CancellationToken ct)
    {
        if (_remote is null)
            throw new InvalidOperationException("ERP import istemcisi yapılandırılmadı.");

        var payload = new ErpSyncCompleteRequest
        {
            BranchId = _remote.BranchId,
            FetchedFromVomsis = result.FetchedFromVomsis,
            Imported = result.Imported,
            SummaryMessage = result.SummaryMessage
        };

        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            "api/bank-sync/vomsis/sync-complete",
            payload,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Manuel sync tamamlama HTTP {Code}: {Body}", (int)resp.StatusCode, body);
        }
    }

    public async Task<bool> ApplyEnrichmentAsync(ErpImportTransaction tx, CancellationToken ct)
    {
        if (_remote is null)
            throw new InvalidOperationException("ERP import istemcisi yapılandırılmadı.");

        var payload = new
        {
            externalId = tx.ExternalId,
            externalKey = tx.ExternalKey,
            senderName = tx.SenderName,
            senderTitle = tx.SenderTitle,
            senderTaxNo = tx.SenderTaxNo,
            bankBranchName = tx.BankBranchName,
            bankBranchCity = tx.BankBranchCity,
            bankBranchDistrict = tx.BankBranchDistrict
        };

        using var resp = await SendJsonAsync(
            HttpMethod.Post,
            "api/bank-sync/vomsis/apply-enrichment",
            payload,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ERP apply-enrichment HTTP {Code} (id={Id}): {Body}",
                (int)resp.StatusCode, tx.ExternalId, body);
            return false;
        }

        _logger.LogInformation(
            "ERP apply-enrichment OK id={Id} tax={Tax} branch={Branch}",
            tx.ExternalId, tx.SenderTaxNo ?? "-", tx.BankBranchName ?? "-");
        return true;
    }

    private async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpMethod method,
        string relativePath,
        T payload,
        CancellationToken ct)
    {
        if (_remote is null)
            throw new InvalidOperationException("ERP import istemcisi yapılandırılmadı.");

        var url = _remote.ErpApiBaseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');
        using var req = new HttpRequestMessage(method, url);
        var headerName = _config["ErpApi:AppKeyHeader"] ?? "x-app-key";
        req.Headers.TryAddWithoutValidation(headerName, _remote.ErpApiAppKey ?? "");
        req.Headers.TryAddWithoutValidation("X-Tenant-Id", _remote.TenantId.ToString());
        req.Headers.TryAddWithoutValidation("X-Branch-Id", _remote.BranchId.ToString());
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        return await _http.SendAsync(req, ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public sealed class ErpSyncCompleteRequest
{
    public Guid BranchId { get; set; }
    public int FetchedFromVomsis { get; set; }
    public int Imported { get; set; }
    public string? SummaryMessage { get; set; }
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
    public string? SenderTitle { get; set; }
    public string? SenderTaxNo { get; set; }
    public string? SenderIban { get; set; }
    public string? BankBranchName { get; set; }
    public string? BankBranchCity { get; set; }
    public string? BankBranchDistrict { get; set; }
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
    private static readonly System.Text.RegularExpressions.Regex BranchCityDistrictRegex = new(
        @"^\s*(?<district>[^/]+?)\s*/\s*(?<city>[^/]+?)\s*(?:Subesi|Şubesi|SUBESI|Sube|Şube|Branch)?\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BranchCityDistrictInlineRegex = new(
        @"(?<district>[\p{L}\p{M}]+(?:\s+[\p{L}\p{M}]+)?)\s*/\s*(?<city>[\p{L}\p{M}]+(?:\s+[\p{L}\p{M}]+)?)\s*(?:Subesi|Şubesi|SUBESI|Sube|Şube|Branch)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BranchSuffixRegex = new(
        @"\s*(?:Subesi|Şubesi|SUBESI|Sube|Şube|Branch)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string? ResolveBranchName(VomsisTransaction tx)
    {
        var isOutgoing = IsOutgoingTransfer(tx);
        return isOutgoing
            ? Coalesce(tx.ReceiverBranch, tx.BranchName, tx.SubeAdi, tx.SenderBranch)
            : Coalesce(tx.SenderBranch, tx.BranchName, tx.SubeAdi, tx.ReceiverBranch);
    }

    public static string? ResolveCounterpartyTaxNo(VomsisTransaction tx)
    {
        var isOutgoing = IsOutgoingTransfer(tx);
        // opponent_taxno Vomsis dekontundaki karşı taraf TCKN/VKN alanıdır.
        return isOutgoing
            ? ResolveTaxNo(
                tx.OpponentTaxNo, tx.ReceiverTaxno, tx.ReceiverTaxNoAlt, tx.RelatedVkn, tx.IlgiliVkn,
                tx.IlgiliTckn, tx.RelatedTckn, tx.IdentityNumber, tx.SenderIdentityNumber,
                tx.SenderTaxno, tx.PayerTaxNo, tx.SenderTckn, tx.PayerTckn, tx.TcKimlikNo)
            : ResolveTaxNo(
                tx.OpponentTaxNo, tx.SenderTaxno, tx.PayerTaxNo, tx.RelatedVkn, tx.IlgiliVkn,
                tx.IlgiliTckn, tx.RelatedTckn, tx.SenderTckn, tx.PayerTckn, tx.TcKimlikNo,
                tx.IdentityNumber, tx.SenderIdentityNumber, tx.ReceiverTaxno, tx.ReceiverTaxNoAlt);
    }

    public static ErpImportTransaction ToErp(VomsisTransaction tx)
    {
        var branchName = ResolveBranchName(tx);
        var (city, district) = ParseCityDistrictFromBranchName(branchName);
        var isOutgoing = IsOutgoingTransfer(tx);
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
            SenderName = ResolveCounterpartyName(tx, isOutgoing),
            SenderTitle = ResolveCounterpartyName(tx, isOutgoing),
            SenderTaxNo = ResolveCounterpartyTaxNo(tx),
            SenderIban = tx.SenderIban,
            BankBranchName = branchName,
            BankBranchCity = city,
            BankBranchDistrict = district
        };
    }

    public static bool IsOutgoingTransfer(VomsisTransaction tx)
    {
        var type = (tx.Type ?? "").Trim().ToLowerInvariant()
            .Replace('ı', 'i').Replace('İ', 'i');
        return type.Contains("borclu", StringComparison.Ordinal);
    }

    private static string? ResolveCounterpartyName(VomsisTransaction tx, bool isOutgoing)
    {
        if (isOutgoing)
        {
            return Coalesce(
                tx.OpponentTitle, tx.RelatedTitle, tx.IlgiliUnvan, tx.SenderTitle, tx.SenderName)?.Trim();
        }

        return Coalesce(tx.RelatedTitle, tx.IlgiliUnvan, tx.SenderName, tx.SenderTitle)?.Trim();
    }

    public static (string? City, string? District) ParseCityDistrictFromBranchName(string? branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return (null, null);

        // Vomsis bazen "i̇" (i + combining dot) gönderir; regex/il eşlemesi bozulmasın.
        var text = NormalizeBranchText(branchName);
        text = BranchSuffixRegex.Replace(text, "").Trim();

        var match = BranchCityDistrictRegex.Match(text);
        if (!match.Success)
            match = BranchCityDistrictInlineRegex.Match(text);
        if (!match.Success)
            return (null, null);

        var district = FormatLocationName(match.Groups["district"].Value.Trim(), upper: false);
        var city = FormatLocationName(match.Groups["city"].Value.Trim(), upper: true);
        return (
            string.IsNullOrWhiteSpace(city) ? null : city,
            string.IsNullOrWhiteSpace(district) ? null : district);
    }

    private static string NormalizeBranchText(string value)
    {
        var normalized = value.Trim().Normalize(NormalizationForm.FormC);
        // Combining Dot Above (U+0307) — "Dereci̇k", "hakkari̇", "şubesi̇"
        normalized = normalized.Replace("\u0307", "", StringComparison.Ordinal);
        return normalized;
    }

    private static string FormatLocationName(string value, bool upper)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        var trimmed = NormalizeBranchText(value);
        return upper
            ? trimmed.ToUpper(new CultureInfo("tr-TR"))
            : char.ToUpper(trimmed[0], new CultureInfo("tr-TR")) + trimmed[1..].ToLower(new CultureInfo("tr-TR"));
    }

    public static string? ResolveTaxNo(params string?[] values)
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

    private static string? Coalesce(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        return null;
    }
}
