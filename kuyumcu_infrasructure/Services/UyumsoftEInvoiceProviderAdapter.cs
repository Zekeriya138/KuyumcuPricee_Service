using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using kuyumcu_application;
using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace kuyumcu_infrastructure.Services;

public sealed class UyumsoftEInvoiceProviderAdapter : IEInvoiceProviderAdapter
{
    private static string? _cachedProductionEndpoint;
    private const string TempUri = "http://tempuri.org/";
    private const string UblInvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace Tns = TempUri;
    private const string SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private const string Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private const string PasswordTextType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordText";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly bool _sendAsDraftOnly;
    private static readonly ConcurrentDictionary<string, (DateTime ExpiresAtUtc, int MaxSerial)> PortalSerialCache = new();

    public UyumsoftEInvoiceProviderAdapter(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
        _sendAsDraftOnly = cfg.GetValue("EInvoice:Uyumsoft:SendAsDraftOnly", true);
    }

    public string ProviderCode => "uyumsoft";

    public async Task<EInvoiceConnectionTestResult> TestConnectionAsync(EInvoiceConnectionTestRequest request, CancellationToken ct)
    {
        try
        {
            var (user, pass) = ResolveCredentials(request.IntegratorUsername, request.IntegratorPassword);
            string? lastXml = null;
            string? lastMessage = null;
            string? lastEndpoint = null;

            foreach (var endpoint in ResolveEndpointCandidates())
            {
                foreach (var useDigest in new[] { false, true })
                {
                    try
                    {
                        var testXml = await SendSoapAsync(
                            "TestConnection",
                            @"<TestConnection xmlns=""http://tempuri.org/""/>",
                            user,
                            pass,
                            ct,
                            usePasswordDigest: useDigest,
                            endpointOverride: endpoint);
                        lastXml = testXml;

                        if (!TryGetResponseSuccess(testXml, out var testMessage) || !IsFlagResponseTrue(testXml))
                        {
                            lastMessage = testMessage;
                            lastEndpoint = endpoint;
                            continue;
                        }

                        _cachedProductionEndpoint = endpoint;
                        var digestNote = useDigest ? " (PasswordDigest)" : "";
                        var clusterNote = endpoint.Contains("uyum.com.tr", StringComparison.OrdinalIgnoreCase)
                            && !endpoint.Contains("uyumsoft.com.tr", StringComparison.OrdinalIgnoreCase)
                            ? " Kurumsal sunucu (efatura.uyum.com.tr)."
                            : "";

                        var info = $"Uyumsoft bağlantısı başarılı.{clusterNote}{digestNote}";
                        try
                        {
                            var whoXml = await SendSoapAsync(
                                "WhoAmI",
                                @"<WhoAmI xmlns=""http://tempuri.org/""/>",
                                user,
                                pass,
                                ct,
                                usePasswordDigest: useDigest,
                                endpointOverride: endpoint);
                            if (TryGetResponseSuccess(whoXml, out _))
                            {
                                var customerTitle = GetXmlValue(whoXml, "Title")
                                                    ?? GetAttributeValue(whoXml, "Title")
                                                    ?? GetXmlValue(whoXml, "Name");
                                if (!string.IsNullOrWhiteSpace(customerTitle))
                                    info = $"Uyumsoft bağlantısı başarılı. ({customerTitle.Trim()}){clusterNote}";
                            }
                        }
                        catch
                        {
                            // WhoAmI isteğe bağlıdır.
                        }

                        return new EInvoiceConnectionTestResult(true, info.Trim(), testXml);
                    }
                    catch (Exception ex) when (useDigest == false)
                    {
                        lastMessage = ex.Message;
                        lastEndpoint = endpoint;
                    }
                }
            }

            var hint = BuildAuthorizationHint(user, lastEndpoint ?? ResolveEndpoint(), lastMessage);
            if (!string.IsNullOrWhiteSpace(lastEndpoint)
                && lastEndpoint.Contains("uyumsoft.com.tr", StringComparison.OrdinalIgnoreCase)
                && !lastEndpoint.Contains("uyum.com.tr", StringComparison.OrdinalIgnoreCase))
            {
                hint += " Kurumsal portal (edonusum.uyum.com.tr) kullanıyorsanız endpoint "
                        + "https://efatura.uyum.com.tr/Services/Integration olmalıdır.";
            }

            return new EInvoiceConnectionTestResult(false, hint, lastXml);
        }
        catch (Exception ex)
        {
            return new EInvoiceConnectionTestResult(false, NormalizeMessage(ex.Message), null);
        }
    }

    public async Task<EInvoiceSendResult> SendOutgoingAsync(EInvoiceSendRequest request, CancellationToken ct)
    {
        try
        {
            var (user, pass) = ResolveCredentials(request.IntegratorUsername, request.IntegratorPassword);
            var receiverVkn = ReadJson(request.PayloadJson, "receiverVkn", "buyerTaxNo", "buyerTaxNumber") ?? request.BuyerTaxNumber;
            var receiverAlias = ReadJson(request.PayloadJson, "receiverAlias", "buyerAlias", "toAlias", "buyerEmail");
            var isEArchive = string.Equals(request.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
            var ublInner = ExtractInvoiceInnerXml(request.PayloadJson);
            if (string.IsNullOrWhiteSpace(ublInner))
                return new EInvoiceSendResult(false, null, null, null, null, null, "Uyumsoft gönderim hatası: UBL içeriği (ublXml/ublBase64) boş.");

            if (string.IsNullOrWhiteSpace(receiverVkn))
                return new EInvoiceSendResult(false, null, null, null, null, null, "Uyumsoft gönderim hatası: Alıcı vergi numarası boş.");
            if (!isEArchive && string.IsNullOrWhiteSpace(receiverAlias))
            {
                return new EInvoiceSendResult(
                    false, null, null, null, null, null,
                    "Uyumsoft gönderim hatası: e-Fatura için alıcı posta kutusu etiketi (PK) zorunlu.");
            }

            ublInner = EnsureInvoiceUblNamespace(ublInner);
            var invoiceIdText = ReadJson(request.PayloadJson, "invoiceId");
            if (!Guid.TryParse(invoiceIdText, out var invoiceEntityId))
            {
                return new EInvoiceSendResult(
                    false, null, null, null, null, null,
                    "Uyumsoft gönderim hatası: Payload içinde invoiceId (fatura GUID) bulunamadı. Faturayı yeniden kuyruğa alın.");
            }

            var issueDate = GibInvoiceNumber.TryReadPayloadIssueDate(request.PayloadJson)
                            ?? GibInvoiceNumber.TryReadIssueDate(ublInner)
                            ?? DateTime.Now.Date;
            var payloadDocumentType = ReadJson(request.PayloadJson, "documentType") ?? request.DocumentType;
            isEArchive = GibInvoiceNumber.IsEArchiveDocumentType(payloadDocumentType);

            var prefix = ResolveSeriesPrefix(
                request.PayloadJson,
                payloadDocumentType,
                ublInner);

            var bootstrapMax = ReadBootstrapSerial(prefix, issueDate.Year);
            var (initialPortalMax, _) = await QueryPortalSeriesStateAsync(
                prefix,
                issueDate.Year,
                isEArchive,
                user,
                pass,
                maxPages: 2,
                includeAllStatusQueries: false,
                bypassCache: false,
                quickQuery: true,
                ct);

            var attemptSerial = initialPortalMax > 0
                ? initialPortalMax + 1
                : bootstrapMax > 0
                    ? bootstrapMax + 1
                    : 0;

            if (attemptSerial <= 0)
            {
                return new EInvoiceSendResult(
                    false, null, null, null, null, null,
                    $"Uyumsoft gönderim hatası: {prefix} serisi için sıradaki belge numarası alınamadı. " +
                    $"E-Fatura ayarlarından bağlantı testi yapın; portal son numara otomatik okunur.");
            }

            var buyerEmail = ReadJson(request.PayloadJson, "buyerEmail");
            var localDocumentId = request.DocumentId.ToString("D");
            var scenarios = isEArchive
                ? new[] { "eArchive", "Automated" }
                : new[] { "eInvoice" };

            string? lastXml = null;
            string? lastMessage = null;
            var portalResyncCount = 0;
            const int maxPortalResyncs = 3;
            const int maxSerialAttempts = 6;
            var serialRetryRequested = false;

            for (var offset = 0; offset < maxSerialAttempts; offset++)
            {
                serialRetryRequested = false;
                var candidateNumber = GibInvoiceNumber.BuildFromSerial(prefix, issueDate, attemptSerial + offset);
                ublInner = GibInvoiceNumber.SanitizeEmbeddedInvoice(
                    GibInvoiceNumber.EnsureInUbl(
                        ExtractInvoiceInnerXml(request.PayloadJson) ?? ublInner,
                        candidateNumber,
                        issueDate,
                        invoiceEntityId,
                        prefix));
                ublInner = GibInvoiceNumber.RegenerateUuid(ublInner);
                // UBL default xmlns'i burada kaldırılmaz; PrepareUblInvoiceElementForUyumsoft yapar.
                ublInner = UyumsoftUblPartyNormalizer.NormalizeForPortalDisplay(ublInner);

                var documentId = GibInvoiceNumber.TryReadDocumentId(ublInner);
                if (!GibInvoiceNumber.IsValid(documentId, issueDate))
                {
                    return new EInvoiceSendResult(
                        false, null, null, null, null, null,
                        $"Uyumsoft gönderim hatası: Belge numarası GİB formatında değil ({documentId ?? "boş"}). " +
                        $"Beklenen seri: {prefix}, format: {prefix}{issueDate:yyyy}000000001.");
                }

                if (!string.IsNullOrWhiteSpace(documentId)
                    && !documentId.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return new EInvoiceSendResult(
                        false, null, null, null, null, null,
                        $"Uyumsoft gönderim hatası: Belge numarası seri ön eki uyuşmuyor. " +
                        $"UBL: {documentId}, beklenen seri: {prefix} ({(isEArchive ? "e-Arşiv" : "e-Fatura")}).");
                }

                foreach (var scenario in scenarios)
                {
                    var invoiceInfoXml = BuildInvoiceInfoForUyumsoft(
                        ublInner,
                        localDocumentId,
                        receiverVkn,
                        receiverAlias,
                        request.BuyerName,
                        isEArchive,
                        scenario,
                        buyerEmail);

                    if (_sendAsDraftOnly)
                    {
                        var draftResult = await TrySaveAsDraftPathsAsync(invoiceInfoXml, user, pass, ct);
                        lastXml = draftResult.ResponseXml;
                        if (draftResult.Success)
                        {
                            var identity = ParseInvoiceIdentity(draftResult.ResponseXml);
                            return new EInvoiceSendResult(
                                true,
                                identity?.Id ?? localDocumentId,
                                identity?.Id,
                                identity?.Number ?? documentId,
                                "IntegratorDraft",
                                draftResult.ResponseXml,
                                null);
                        }

                        lastMessage = draftResult.ErrorMessage;
                        if (IsDocumentNumberValidationError(lastMessage))
                        {
                            serialRetryRequested = true;
                            break;
                        }

                        if (IsMissingInvoiceInfoError(lastMessage) || IsXmlDocumentError(lastMessage))
                            continue;

                        return new EInvoiceSendResult(
                            false,
                            null,
                            null,
                            null,
                            null,
                            lastXml,
                            NormalizeMessage(BuildUyumsoftSendFailureMessage(lastMessage, documentId, prefix)));
                    }
                    else
                    {
                        var body = BuildDraftSoapBody("SendInvoice", invoiceInfoXml);
                        var xml = await SendSoapAsync("SendInvoice", body, user, pass, ct);
                        lastXml = xml;

                        if (TryGetResponseSuccess(xml, out var message))
                        {
                            var identity = ParseInvoiceIdentity(xml);
                            return new EInvoiceSendResult(
                                true,
                                identity?.Id ?? localDocumentId,
                                identity?.Id,
                                identity?.Number ?? documentId,
                                "Sent",
                                xml,
                                null);
                        }

                        lastMessage = message;
                        if (IsDocumentNumberValidationError(lastMessage))
                        {
                            serialRetryRequested = true;
                            break;
                        }

                        if (IsMissingInvoiceInfoError(lastMessage) || IsXmlDocumentError(lastMessage))
                            continue;

                        return new EInvoiceSendResult(
                            false,
                            null,
                            null,
                            null,
                            null,
                            lastXml,
                            NormalizeMessage(BuildUyumsoftSendFailureMessage(lastMessage, documentId, prefix)));
                    }
                }

                if (serialRetryRequested)
                {
                    var resync = await TryResyncSerialFromPortal(
                        attemptSerial,
                        offset,
                        portalResyncCount,
                        maxPortalResyncs,
                        prefix,
                        issueDate,
                        isEArchive,
                        user,
                        pass,
                        ct);
                    if (resync.Resynced)
                    {
                        attemptSerial = resync.AttemptSerial;
                        offset = resync.Offset;
                        portalResyncCount = resync.PortalResyncCount;
                    }

                    if (offset < maxSerialAttempts - 1)
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(lastMessage))
                {
                    return new EInvoiceSendResult(
                        false,
                        null,
                        null,
                        null,
                        null,
                        lastXml,
                        NormalizeMessage(BuildUyumsoftSendFailureMessage(lastMessage, documentId, prefix)));
                }
            }

            return new EInvoiceSendResult(
                false,
                null,
                null,
                null,
                null,
                lastXml,
                NormalizeMessage(
                    IsDocumentNumberValidationError(lastMessage)
                        ? $"Belge numarası Uyumsoft portal sayacıyla eşleşmedi. " +
                          $"Denenen son numara: {GibInvoiceNumber.BuildFromSerial(prefix, issueDate, attemptSerial + maxSerialAttempts - 1)}. " +
                          $"Portalda {prefix} serisinin son numarasını kontrol edin, ardından bağlantı testi yapıp yeni taslak gönderin. " +
                          $"Son hata: {lastMessage}"
                        : BuildUyumsoftSendFailureMessage(lastMessage, GibInvoiceNumber.BuildFromSerial(prefix, issueDate, attemptSerial), prefix)));
        }
        catch (Exception ex)
        {
            return new EInvoiceSendResult(false, null, null, null, null, null, $"Uyumsoft gönderim hatası: {NormalizeMessage(ex.Message)}");
        }
    }

    public async Task<EInvoiceStatusResult> GetStatusAsync(EInvoiceStatusRequest request, CancellationToken ct)
    {
        try
        {
            var (user, pass) = ResolveCredentials(request.IntegratorUsername, request.IntegratorPassword);
            var invoiceId = request.Uuid ?? request.IntegratorDocumentId;
            if (string.IsNullOrWhiteSpace(invoiceId))
                return new EInvoiceStatusResult(false, null, null, null, "Uyumsoft durum sorgu hatası: fatura UUID/Id boş.");

            var body = $@"<GetOutboxInvoiceStatusWithLogs xmlns=""{TempUri}"">
      <invoiceIds>
        <string>{XmlEscape(invoiceId)}</string>
      </invoiceIds>
    </GetOutboxInvoiceStatusWithLogs>";

            var xml = await SendSoapAsync("GetOutboxInvoiceStatusWithLogs", body, user, pass, ct);
            if (!TryGetResponseSuccess(xml, out var message))
                return new EInvoiceStatusResult(false, null, null, xml, NormalizeMessage(message));

            var status = GetAttributeValue(xml, "Status") ?? GetXmlValue(xml, "Status");
            var statusMessage = GetAttributeValue(xml, "Message") ?? message;
            return new EInvoiceStatusResult(true, status ?? statusMessage ?? "Unknown", DateTime.UtcNow, xml, null);
        }
        catch (Exception ex)
        {
            return new EInvoiceStatusResult(false, null, null, null, $"Uyumsoft durum sorgu hatası: {NormalizeMessage(ex.Message)}");
        }
    }

    public async Task<EInvoiceCancelResult> CancelAsync(EInvoiceCancelRequest request, CancellationToken ct)
    {
        try
        {
            var (user, pass) = ResolveCredentials(request.IntegratorUsername, request.IntegratorPassword);
            var invoiceId = string.IsNullOrWhiteSpace(request.Uuid) ? request.IntegratorDocumentId : request.Uuid;
            if (string.IsNullOrWhiteSpace(invoiceId))
                return new EInvoiceCancelResult(false, null, null, "Uyumsoft iptal hatası: fatura UUID/Id boş.");

            var cancelDate = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var body = $@"<CancelEArchiveInvoice xmlns=""{TempUri}"">
      <request InvoiceId=""{XmlEscape(invoiceId)}"" CancelDate=""{cancelDate}""/>
    </CancelEArchiveInvoice>";

            var xml = await SendSoapAsync("CancelEArchiveInvoice", body, user, pass, ct);
            if (!TryGetResponseSuccess(xml, out var message))
                return new EInvoiceCancelResult(false, null, xml, NormalizeMessage(message));

            return new EInvoiceCancelResult(true, "Cancelled", xml, null);
        }
        catch (Exception ex)
        {
            return new EInvoiceCancelResult(false, null, null, $"Uyumsoft iptal hatası: {NormalizeMessage(ex.Message)}");
        }
    }

    public Task<EInvoiceWebhookVerificationResult> VerifyWebhookAsync(EInvoiceWebhookVerificationRequest request, CancellationToken ct)
        => Task.FromResult(new EInvoiceWebhookVerificationResult(false, null, null, null, null, "Uyumsoft webhook doğrulaması tanımlı değil."));

    public async Task<IntegratorTaxpayerQueryResult> QueryTaxpayerAsync(string? username, string? password, string taxNumber, CancellationToken ct)
    {
        var normalizedTaxNo = new string((taxNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedTaxNo.Length != 10 && normalizedTaxNo.Length != 11)
            return new IntegratorTaxpayerQueryResult(false, null, null, null, null, "TCKN/VKN 10 veya 11 hane olmalıdır.");

        try
        {
            var (user, pass) = ResolveCredentials(username, password);
            var isUserBody = $@"<IsEInvoiceUser xmlns=""{TempUri}"">
      <vknTckn>{XmlEscape(normalizedTaxNo)}</vknTckn>
    </IsEInvoiceUser>";
            var isUserXml = await SendSoapAsync("IsEInvoiceUser", isUserBody, user, pass, ct);
            var isEInvoice = TryGetResponseSuccess(isUserXml, out var isUserMessage) && IsFlagResponseTrue(isUserXml);
            string? title = null;
            string? receiverAlias = null;

            var aliasBody = $@"<GetUserAliasses xmlns=""{TempUri}"">
      <vknTckn>{XmlEscape(normalizedTaxNo)}</vknTckn>
    </GetUserAliasses>";
            var aliasXml = await SendSoapAsync("GetUserAliasses", aliasBody, user, pass, ct);
            if (TryGetResponseSuccess(aliasXml, out _))
            {
                title = GetAttributeValue(aliasXml, "Title") ?? GetXmlValue(aliasXml, "Title");
                receiverAlias = ExtractReceiverAlias(aliasXml);
                if (!string.IsNullOrWhiteSpace(receiverAlias))
                    isEInvoice = true;
            }
            else if (!TryGetResponseSuccess(isUserXml, out _))
            {
                return new IntegratorTaxpayerQueryResult(false, null, null, null, aliasXml ?? isUserXml, NormalizeMessage(isUserMessage));
            }

            var message = isEInvoice
                ? (string.IsNullOrWhiteSpace(receiverAlias)
                    ? "Uyumsoft: e-Fatura mükellefi; alıcı etiketi boş döndü."
                    : "Uyumsoft üzerinden mükellef bilgileri alındı.")
                : "Uyumsoft: e-Fatura mükellefi değil (e-Arşiv).";

            return new IntegratorTaxpayerQueryResult(
                true,
                isEInvoice,
                title?.Trim(),
                receiverAlias?.Trim(),
                aliasXml,
                message);
        }
        catch (Exception ex)
        {
            return new IntegratorTaxpayerQueryResult(false, null, null, null, null, NormalizeMessage(ex.Message));
        }
    }

    public async Task<EInvoiceIncomingResult> GetIncomingInvoicesAsync(EInvoiceIncomingRequest request, CancellationToken ct)
    {
        try
        {
            var (user, pass) = ResolveCredentials(request.IntegratorUsername, request.IntegratorPassword);
            var maxItems = request.Limit <= 0 ? 500 : Math.Min(request.Limit, 5000);
            var pageSize = Math.Min(50, maxItems);
            var start = request.StartDateUtc.ToUniversalTime();
            var end = request.EndDateUtc.ToUniversalTime();
            if (end < start) (start, end) = (end, start);

            var items = new List<EInvoiceIncomingItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? lastRaw = null;
            var pageIndex = 0;

            while (items.Count < maxItems)
            {
                var body = $@"<GetInboxInvoices xmlns=""{TempUri}"">
      <query PageIndex=""{pageIndex}"" PageSize=""{pageSize}"" SetTaken=""false"" OnlyNewestInvoices=""false"">
        <ExecutionStartDate>{start:yyyy-MM-ddTHH:mm:ss}</ExecutionStartDate>
        <ExecutionEndDate>{end:yyyy-MM-ddTHH:mm:ss}</ExecutionEndDate>
      </query>
    </GetInboxInvoices>";

                var xml = await SendSoapAsync("GetInboxInvoices", body, user, pass, ct);
                lastRaw = xml;
                if (!TryGetResponseSuccess(xml, out var message))
                    return new EInvoiceIncomingResult(false, items, lastRaw, NormalizeMessage(message));

                var pageItems = ParseIncomingInvoiceInfos(xml);
                if (pageItems.Count == 0)
                    break;

                foreach (var item in pageItems)
                {
                    if (!seen.Add(item.Uuid))
                        continue;
                    items.Add(item);
                    if (items.Count >= maxItems)
                        break;
                }

                if (pageItems.Count < pageSize)
                    break;
                pageIndex++;
            }

            return new EInvoiceIncomingResult(true, items, lastRaw, null);
        }
        catch (Exception ex)
        {
            return new EInvoiceIncomingResult(false, new List<EInvoiceIncomingItem>(), null, NormalizeMessage(ex.Message));
        }
    }

    private (string User, string Pass) ResolveCredentials(string? username, string? password)
    {
        var user = string.IsNullOrWhiteSpace(username) ? _cfg["EInvoice:Uyumsoft:Username"] : username;
        var pass = string.IsNullOrWhiteSpace(password) ? _cfg["EInvoice:Uyumsoft:Password"] : password;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            throw new InvalidOperationException("Uyumsoft web servis kullanıcı adı/şifre tanımlı değil.");
        return (user.Trim(), pass.Trim());
    }

    private string ResolveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_cachedProductionEndpoint))
            return _cachedProductionEndpoint;

        return ResolveEndpointCandidates().First();
    }

    private IReadOnlyList<string> ResolveEndpointCandidates()
    {
        var useTest = string.Equals(_cfg["EInvoice:Uyumsoft:UseTestEnvironment"], "true", StringComparison.OrdinalIgnoreCase);
        var primary = useTest
            ? _cfg["EInvoice:Uyumsoft:TestEndpoint"]
            : _cfg["EInvoice:Uyumsoft:Endpoint"];
        var alternates = useTest
            ? _cfg.GetSection("EInvoice:Uyumsoft:AlternateTestEndpoints").Get<string[]>() ?? Array.Empty<string>()
            : _cfg.GetSection("EInvoice:Uyumsoft:AlternateEndpoints").Get<string[]>() ?? Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(primary))
        {
            var legacyBase = (_cfg["EInvoice:Uyumsoft:BaseUrl"] ?? "").Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(legacyBase))
            {
                primary = legacyBase.EndsWith("/Services/Integration", StringComparison.OrdinalIgnoreCase)
                    ? legacyBase
                    : $"{legacyBase}/Services/Integration";
            }
        }

        var defaults = useTest
            ? new[] { "https://efatura-test.uyumsoft.com.tr/Services/Integration" }
            : new[]
            {
                "https://efatura.uyum.com.tr/Services/Integration",
                "https://efatura.uyumsoft.com.tr/Services/Integration"
            };

        var result = new List<string>();
        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return;
            var normalized = uri.ToString();
            if (result.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                return;
            result.Add(normalized);
        }

        Add(primary);
        foreach (var alt in alternates)
            Add(alt);
        foreach (var def in defaults)
            Add(def);

        if (result.Count == 0)
            throw new InvalidOperationException(
                "Uyumsoft endpoint tanımlı değil. Kurumsal hesaplar için " +
                "https://efatura.uyum.com.tr/Services/Integration kullanın.");

        return result;
    }

    private async Task<string> SendSoapAsync(
        string operation,
        string bodyInnerXml,
        string username,
        string password,
        CancellationToken ct,
        bool usePasswordDigest = false,
        string? endpointOverride = null)
    {
        var endpoint = endpointOverride ?? ResolveEndpoint();
        var envelope = BuildEnvelope(bodyInnerXml, username, password, usePasswordDigest);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Content = new StringContent(envelope, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPAction", $"http://tempuri.org/IIntegration/{operation}");

        var res = await _http.SendAsync(req, ct);
        var xml = await res.Content.ReadAsStringAsync(ct);
        DumpSoapDiagnostics(operation, envelope, xml, (int)res.StatusCode);
        if (!res.IsSuccessStatusCode && !HasSoapFault(xml))
        {
            var preview = string.IsNullOrWhiteSpace(xml) ? string.Empty : (xml.Length > 600 ? xml[..600] + "..." : xml);
            throw new InvalidOperationException(
                $"Uyumsoft SOAP HTTP {(int)res.StatusCode}: {res.ReasonPhrase}. Endpoint: {endpoint}. {preview}".Trim());
        }
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidOperationException(
                $"Uyumsoft SOAP boş yanıt döndürdü (HTTP {(int)res.StatusCode}). Endpoint: {endpoint}. İşlem: {operation}.");
        return xml;
    }

    // Yalnızca gönderim/taslak işlemleri için gerçek SOAP istek+yanıtını diske yazar (teşhis).
    private void DumpSoapDiagnostics(string operation, string requestEnvelope, string? responseXml, int httpStatus)
    {
        if (!_cfg.GetValue("EInvoice:Uyumsoft:DumpSoap", true))
            return;
        if (operation is not ("SaveAsDraft" or "CompressedSaveAsDraft" or "SendInvoice"))
            return;

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "uyumsoft-soap-dump");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var file = Path.Combine(dir, $"{stamp}-{operation}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Operation: {operation}");
            sb.AppendLine($"HTTP: {httpStatus}");
            sb.AppendLine("==== REQUEST ENVELOPE ====");
            sb.AppendLine(requestEnvelope);
            sb.AppendLine();
            sb.AppendLine("==== RESPONSE ====");
            sb.AppendLine(responseXml ?? "(boş)");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // Teşhis günlüğü başarısız olsa da gönderimi etkilemez.
        }
    }

    private static string BuildEnvelope(string bodyInnerXml, string username, string password, bool usePasswordDigest = false)
    {
        var created = DateTime.UtcNow;
        var expires = created.AddMinutes(5);
        var tokenId = $"UsernameToken-{Guid.NewGuid():N}";
        var tsId = $"TS-{Guid.NewGuid():N}";

        string passwordElement;
        string? nonceElement = null;
        string? createdElement = null;
        if (usePasswordDigest)
        {
            var nonceBytes = RandomNumberGenerator.GetBytes(16);
            var nonce = Convert.ToBase64String(nonceBytes);
            var createdText = created.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var digestBytes = SHA1.HashData(Encoding.UTF8.GetBytes(nonce + createdText + password));
            var digest = Convert.ToBase64String(digestBytes);
            const string digestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
            passwordElement = $@"<wsse:Password Type=""{digestType}"">{XmlEscape(digest)}</wsse:Password>";
            nonceElement = $@"<wsse:Nonce EncodingType=""http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"">{nonce}</wsse:Nonce>";
            createdElement = $@"<wsu:Created>{createdText}</wsu:Created>";
        }
        else
        {
            passwordElement = $@"<wsse:Password Type=""{PasswordTextType}"">{XmlEscape(password)}</wsse:Password>";
        }

        return $@"<s:Envelope xmlns:s=""{SoapEnv}"">
  <s:Header>
    <wsse:Security s:mustUnderstand=""1"" xmlns:wsse=""{Wsse}"" xmlns:wsu=""{Wsu}"">
      <wsu:Timestamp wsu:Id=""{tsId}"">
        <wsu:Created>{created:yyyy-MM-ddTHH:mm:ss.fffZ}</wsu:Created>
        <wsu:Expires>{expires:yyyy-MM-ddTHH:mm:ss.fffZ}</wsu:Expires>
      </wsu:Timestamp>
      <wsse:UsernameToken wsu:Id=""{tokenId}"">
        <wsse:Username>{XmlEscape(username)}</wsse:Username>
        {passwordElement}
        {nonceElement}{createdElement}
      </wsse:UsernameToken>
    </wsse:Security>
  </s:Header>
  <s:Body>
    {bodyInnerXml}
  </s:Body>
</s:Envelope>";
    }

    private string BuildAuthorizationHint(string username, string endpoint, string? providerMessage)
    {
        var baseMessage = NormalizeMessage(providerMessage);
        return $"{baseMessage} Endpoint: {endpoint}. " +
               "Kontrol: (1) Web servis kullanıcı adı/şifresini portalden sıfırlayıp tekrar girin, " +
               "(2) IP whitelist'te 'Web Servis' tipi tanımlı olsun, " +
               $"(3) Kullanılan hesap: '{username}'.";
    }

    private static bool TryGetResponseSuccess(string xml, out string? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            message = "Boş yanıt.";
            return false;
        }

        if (HasSoapFault(xml))
        {
            message = GetXmlValue(xml, "faultstring") ?? GetXmlValue(xml, "Reason") ?? "SOAP fault.";
            return false;
        }

        var doc = XDocument.Parse(xml);
        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("Result", StringComparison.Ordinal));
        if (result is null)
            return true;

        var isSucceded = result.Attribute("IsSucceded")?.Value ?? result.Attribute("IsSucceeded")?.Value;
        message = result.Attribute("Message")?.Value;
        if (string.IsNullOrWhiteSpace(isSucceded))
            return true;
        return string.Equals(isSucceded, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFlagResponseTrue(string xml)
    {
        var doc = XDocument.Parse(xml);
        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("Result", StringComparison.Ordinal));
        var value = result?.Attribute("Value")?.Value;
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSoapFault(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        return xml.Contains("Fault", StringComparison.OrdinalIgnoreCase)
               && (xml.Contains("faultstring", StringComparison.OrdinalIgnoreCase)
                   || xml.Contains("Fault>", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSeriesPrefix(string? payloadJson, string? documentType, string ublInner)
    {
        var seriesPrefix = ReadJson(payloadJson, "seriesPrefix");
        if (!string.IsNullOrWhiteSpace(seriesPrefix))
            return GibInvoiceNumber.ResolvePrefixForDocumentType(documentType, seriesPrefix, seriesPrefix);

        var invoicePrefix = ReadJson(payloadJson, "invoiceSeriesPrefix");
        var archivePrefix = ReadJson(payloadJson, "archiveSeriesPrefix");
        if (!string.IsNullOrWhiteSpace(invoicePrefix) || !string.IsNullOrWhiteSpace(archivePrefix))
        {
            return GibInvoiceNumber.ResolvePrefixForDocumentType(
                documentType,
                invoicePrefix,
                archivePrefix);
        }

        try
        {
            var root = XDocument.Parse(ublInner).Root;
            var profileId = root?.Elements(Cbc + "ProfileID").FirstOrDefault()?.Value
                            ?? root?.Elements().FirstOrDefault(e => e.Name.LocalName == "ProfileID")?.Value;
            var ublIsArchive = string.Equals(profileId, "EARSIVFATURA", StringComparison.OrdinalIgnoreCase);
            var documentId = GibInvoiceNumber.TryReadDocumentId(ublInner);
            if (!string.IsNullOrWhiteSpace(documentId)
                && documentId.Length >= 16
                && GibInvoiceNumber.IsValid(documentId, GibInvoiceNumber.TryReadIssueDate(ublInner) ?? DateTime.Now.Date))
            {
                var candidate = documentId[..3];
                if (ublIsArchive == GibInvoiceNumber.IsEArchiveDocumentType(documentType))
                    return candidate;
            }
        }
        catch
        {
            // ignore
        }

        return GibInvoiceNumber.ResolvePrefixForDocumentType(documentType, "AUR", "ARS");
    }

    public async Task<IntegratorSeriesCounterResult> QuerySeriesCounterAsync(
        string? username,
        string? password,
        string prefix,
        int year,
        bool isEArchive,
        CancellationToken ct)
    {
        try
        {
            var (user, pass) = ResolveCredentials(username, password);
            var normalizedPrefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
                isEArchive ? "EArsiv" : "EFatura",
                prefix,
                prefix);
            var (lastSerial, lastRaw) = await QueryPortalSeriesStateAsync(
                normalizedPrefix,
                year,
                isEArchive,
                user,
                pass,
                maxPages: 10,
                includeAllStatusQueries: true,
                bypassCache: true,
                quickQuery: false,
                ct);

            if (lastSerial <= 0)
            {
                return new IntegratorSeriesCounterResult(
                    false,
                    null,
                    null,
                    lastRaw,
                    $"{normalizedPrefix} serisi için {year} yılında Uyumsoft portalında kayıt bulunamadı.");
            }

            var issueDate = new DateTime(year, 1, 1);
            var nextNumber = GibInvoiceNumber.BuildFromSerial(normalizedPrefix, issueDate, lastSerial + 1);
            return new IntegratorSeriesCounterResult(true, lastSerial, nextNumber, lastRaw, null);
        }
        catch (Exception ex)
        {
            return new IntegratorSeriesCounterResult(false, null, null, null, NormalizeMessage(ex.Message));
        }
    }

    private async Task<string?> TryResolveNextInvoiceNumberAsync(
        string prefix,
        DateTime issueDate,
        bool isEArchive,
        string? payloadJson,
        string user,
        string pass,
        CancellationToken ct)
    {
        var bootstrapMax = ReadBootstrapSerial(prefix, issueDate.Year);
        var (portalMax, _) = await QueryPortalSeriesStateAsync(
            prefix,
            issueDate.Year,
            isEArchive,
            user,
            pass,
            maxPages: 2,
            includeAllStatusQueries: false,
            bypassCache: false,
            quickQuery: true,
            ct);
        if (portalMax > 0)
            return GibInvoiceNumber.BuildFromSerial(prefix, issueDate, portalMax + 1);

        if (bootstrapMax > 0)
            return GibInvoiceNumber.BuildFromSerial(prefix, issueDate, bootstrapMax + 1);

        return null;
    }

    private void InvalidatePortalSerialCache(string user, string prefix, int year, bool isEArchive)
    {
        var prefixKey = $"{user}|{prefix}|{year}|{(isEArchive ? "A" : "I")}";
        foreach (var key in PortalSerialCache.Keys.ToList())
        {
            if (key.StartsWith(prefixKey, StringComparison.OrdinalIgnoreCase))
                PortalSerialCache.TryRemove(key, out _);
        }
    }

    private async Task<(bool Resynced, int AttemptSerial, int Offset, int PortalResyncCount)> TryResyncSerialFromPortal(
        int attemptSerial,
        int offset,
        int portalResyncCount,
        int maxPortalResyncs,
        string prefix,
        DateTime issueDate,
        bool isEArchive,
        string user,
        string pass,
        CancellationToken ct)
    {
        if (portalResyncCount >= maxPortalResyncs)
            return (false, attemptSerial, offset, portalResyncCount);

        InvalidatePortalSerialCache(user, prefix, issueDate.Year, isEArchive);
        var (portalMax, _) = await QueryPortalSeriesStateAsync(
            prefix,
            issueDate.Year,
            isEArchive,
            user,
            pass,
            maxPages: 10,
            includeAllStatusQueries: true,
            bypassCache: true,
            quickQuery: false,
            ct);
        if (portalMax <= 0)
            return (false, attemptSerial, offset, portalResyncCount);

        return (true, portalMax + 1, -1, portalResyncCount + 1);
    }

    private int ReadBootstrapSerial(string prefix, int year)
    {
        var underscoreKey = $"{prefix}_{year}";
        var serial = _cfg.GetValue<int?>($"EInvoice:Uyumsoft:SeriesBootstrap:{underscoreKey}");
        if (serial > 0)
            return serial.Value;

        // Eski konfigürasyon anahtarı (SYD:2026) geriye dönük uyumluluk
        return _cfg.GetValue<int?>($"EInvoice:Uyumsoft:SeriesBootstrap:{prefix}:{year}") ?? 0;
    }

    private async Task<(int MaxSerial, string? LastRaw)> QueryPortalSeriesStateAsync(
        string prefix,
        int year,
        bool isEArchive,
        string user,
        string pass,
        int maxPages,
        bool includeAllStatusQueries,
        bool bypassCache,
        bool quickQuery,
        CancellationToken ct)
    {
        var cacheKey = $"{user}|{prefix}|{year}|{(isEArchive ? "A" : "I")}|{maxPages}|{(includeAllStatusQueries ? "F" : "Q")}|{(quickQuery ? "Q" : "S")}";
        if (!bypassCache
            && PortalSerialCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return (cached.MaxSerial, null);
        }

        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? lastRaw = null;
        const string xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var yearStart = new DateTime(year, 1, 1);
        var yearEnd = new DateTime(year, 12, 31, 23, 59, 59);
        var scenariosToQuery = isEArchive
            ? new[] { "eArchive" }
            : new[] { "eInvoice" };
        const int pageSize = 100;
        var pageLimit = quickQuery
            ? Math.Clamp(maxPages, 1, 2)
            : Math.Clamp(maxPages, 1, 20);

        async Task CollectOutboxListAsync(string? scenarioValue, string? statusValue, bool useYearFilter)
        {
            for (var pageIndex = 0; pageIndex < pageLimit; pageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var statusElement = string.IsNullOrWhiteSpace(statusValue)
                    ? $@"<Status xmlns:xsi=""{xsi}"" xsi:nil=""true""/>"
                    : $@"<Status>{statusValue}</Status>";
                var scenarioElement = string.IsNullOrWhiteSpace(scenarioValue)
                    ? $@"<Scenario xmlns:xsi=""{xsi}"" xsi:nil=""true""/>"
                    : $@"<Scenario>{scenarioValue}</Scenario>";
                var createStartElement = useYearFilter
                    ? $@"<CreateStartDate>{yearStart:yyyy-MM-ddTHH:mm:ss}</CreateStartDate>"
                    : $@"<CreateStartDate xmlns:xsi=""{xsi}"" xsi:nil=""true""/>";
                var createEndElement = useYearFilter
                    ? $@"<CreateEndDate>{yearEnd:yyyy-MM-ddTHH:mm:ss}</CreateEndDate>"
                    : $@"<CreateEndDate xmlns:xsi=""{xsi}"" xsi:nil=""true""/>";

                var body = $@"<GetOutboxInvoiceList xmlns=""{TempUri}"">
      <query PageIndex=""{pageIndex}"" PageSize=""{pageSize}"">
        {scenarioElement}
        <ExecutionStartDate xmlns:xsi=""{xsi}"" xsi:nil=""true""/>
        <ExecutionEndDate xmlns:xsi=""{xsi}"" xsi:nil=""true""/>
        {createStartElement}
        {createEndElement}
        {statusElement}
        <SortColumn>CreateDate</SortColumn>
        <SortMode>Descending</SortMode>
        <IsArchived>false</IsArchived>
      </query>
    </GetOutboxInvoiceList>";

                var xml = await SendSoapAsync("GetOutboxInvoiceList", body, user, pass, ct);
                lastRaw = xml;
                if (!TryGetResponseSuccess(xml, out _))
                    break;

                var pageNumbers = ParseOutboxListDocumentIds(xml);
                foreach (var number in pageNumbers)
                    collected.Add(number);
                foreach (var number in GibInvoiceNumber.ScanDocumentNumbersFromText(xml))
                    collected.Add(number);

                if (pageNumbers.Count == 0 && pageIndex == 0)
                    break;

                if (!TryReadTotalPages(xml, out var totalPages) || pageIndex >= totalPages - 1)
                    break;
            }
        }

        foreach (var scenario in scenariosToQuery)
        {
            await CollectOutboxListAsync(scenario, "Draft", true);
            if (GibInvoiceNumber.GetMaxSerial(prefix, year, collected) > 0 && quickQuery)
                break;
            await CollectOutboxListAsync(scenario, null, true);
        }

        if (!quickQuery && GibInvoiceNumber.GetMaxSerial(prefix, year, collected) <= 0)
            await CollectOutboxListAsync(null, null, true);

        if (!quickQuery && includeAllStatusQueries)
        {
            foreach (var scenario in scenariosToQuery)
            {
                await CollectOutboxListAsync(scenario, "Approved", true);
                await CollectOutboxListAsync(scenario, "SentToGib", true);
            }

            if (GibInvoiceNumber.GetMaxSerial(prefix, year, collected) <= 0)
                await CollectOutboxListAsync(null, null, false);

            for (var pageIndex = 0; pageIndex < Math.Min(pageLimit, 3); pageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var body = $@"<GetOutboxInvoices xmlns=""{TempUri}"">
      <query PageIndex=""{pageIndex}"" PageSize=""{pageSize}"" SetTaken=""false"" OnlyNewestInvoices=""false"">
        <ExecutionStartDate>{yearStart:yyyy-MM-ddTHH:mm:ss}</ExecutionStartDate>
        <ExecutionEndDate>{yearEnd:yyyy-MM-ddTHH:mm:ss}</ExecutionEndDate>
      </query>
    </GetOutboxInvoices>";

                var xml = await SendSoapAsync("GetOutboxInvoices", body, user, pass, ct);
                lastRaw = xml;
                if (!TryGetResponseSuccess(xml, out _))
                    break;

                foreach (var number in GibInvoiceNumber.ExtractKnownDocumentNumbers(xml))
                    collected.Add(number);

                if (!TryReadTotalPages(xml, out var totalPages) || pageIndex >= totalPages - 1)
                    break;
            }
        }

        var maxSerial = GibInvoiceNumber.GetMaxSerial(prefix, year, collected);
        if (maxSerial > 0)
            PortalSerialCache[cacheKey] = (DateTime.UtcNow.AddSeconds(45), maxSerial);

        return (maxSerial, lastRaw);
    }

    private static string? ResolvePreparedInvoiceNumber(string? payloadJson, string prefix, DateTime issueDate)
    {
        var prepared = ReadJson(payloadJson, "resolvedInvoiceNumber");
        if (string.IsNullOrWhiteSpace(prepared))
            return null;

        if (!GibInvoiceNumber.TryExtractSerialParts(prepared, out var preparedPrefix, out var preparedYear, out var preparedSerial))
            return null;

        if (!string.Equals(preparedPrefix, prefix, StringComparison.Ordinal) || preparedYear != issueDate.Year)
            return null;

        if (TryReadJsonInt(payloadJson, "lastKnownSeriesSerial", out var lastKnown) && lastKnown > 0)
        {
            return preparedSerial == lastKnown + 1 ? prepared : null;
        }

        return null;
    }

    private static List<string> ParseOutboxListDocumentIds(string xml)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(xml))
            return result;

        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "OutboxInvoiceListItem"))
            {
                foreach (var fieldName in new[] { "InvoiceNumber", "Number", "DocumentId" })
                {
                    var value = item.Elements().FirstOrDefault(e => e.Name.LocalName == fieldName)?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    if (GibInvoiceNumber.TryExtractSerialParts(value, out _, out _, out _))
                        result.Add(value);
                }
            }

            if (result.Count == 0)
            {
                foreach (var documentId in doc.Descendants().Where(e => e.Name.LocalName == "DocumentId"))
                {
                    var value = documentId.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(value) && value.Length == 16)
                        result.Add(value);
                }
            }
        }
        catch
        {
            foreach (var number in GibInvoiceNumber.ScanDocumentNumbersFromText(xml))
                result.Add(number);
        }

        return result;
    }

    private static bool TryReadTotalPages(string xml, out int totalPages)
    {
        totalPages = 1;
        if (string.IsNullOrWhiteSpace(xml))
            return false;

        try
        {
            var doc = XDocument.Parse(xml);
            var valueNode = doc.Descendants()
                .FirstOrDefault(e =>
                    string.Equals(e.Name.LocalName, "Value", StringComparison.Ordinal)
                    && (e.Attribute("TotalPages") is not null
                        || e.Elements().Any(x => string.Equals(x.Name.LocalName, "TotalPages", StringComparison.Ordinal))));
            if (valueNode is null)
                return false;

            if (valueNode.Attribute("TotalPages")?.Value is { } attrText
                && int.TryParse(attrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attrPages))
            {
                totalPages = Math.Max(1, attrPages);
                return true;
            }

            var elementText = valueNode.Elements().FirstOrDefault(e => e.Name.LocalName == "TotalPages")?.Value;
            if (!string.IsNullOrWhiteSpace(elementText)
                && int.TryParse(elementText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elementPages))
            {
                totalPages = Math.Max(1, elementPages);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsDocumentNumberValidationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("11222", StringComparison.Ordinal)
               || message.Contains("Belge Numarası Hatalı", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Belge Numarasi Hatali", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractInvoiceInnerXml(string? payloadJson)
    {
        var ublBase64 = ReadJson(payloadJson, "ublBase64");
        if (!string.IsNullOrWhiteSpace(ublBase64))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(ublBase64)).Trim();
                if (!string.IsNullOrWhiteSpace(decoded))
                    return StripXmlDeclaration(decoded);
            }
            catch
            {
                // ublXml yedeğine düş.
            }
        }

        var ublXml = ReadJson(payloadJson, "ublXml");
        if (!string.IsNullOrWhiteSpace(ublXml))
            return StripXmlDeclaration(ublXml.Trim());

        return null;
    }

    private static string StripXmlDeclaration(string xml)
    {
        if (!xml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            return xml;

        var idx = xml.IndexOf("?>", StringComparison.Ordinal);
        return idx >= 0 ? xml[(idx + 2)..].TrimStart() : xml;
    }

    private static string EnsureInvoiceUblNamespace(string ublInner)
    {
        // Uyumsoft gönderiminde UBL default xmlns Invoice kökünden zaten kaldırılır.
        // Bu metot yalnızca cbc/cac/ext prefix'lerinin varlığını sağlar.
        if (ublInner.Contains("xmlns:cbc=", StringComparison.OrdinalIgnoreCase))
            return ublInner;

        var match = Regex.Match(ublInner, @"<Invoice\b", RegexOptions.IgnoreCase);
        if (!match.Success)
            return ublInner;

        var replacement =
            $@"<Invoice xmlns:ext=""urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2"" xmlns:cac=""urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2"" xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2""";
        return ublInner[..match.Index] + replacement + ublInner[(match.Index + match.Length)..];
    }

    private async Task<(bool Success, string ResponseXml, string? ErrorMessage)> TrySaveAsDraftPathsAsync(
        string invoiceInfoXml,
        string user,
        string pass,
        CancellationToken ct)
    {
        var draftBody = BuildDraftSoapBody("SaveAsDraft", invoiceInfoXml);
        var draftXml = await SendSoapAsync("SaveAsDraft", draftBody, user, pass, ct);
        if (TryGetResponseSuccess(draftXml, out _))
            return (true, draftXml, null);

        var lastMessage = GetResultMessage(draftXml);

        // SaveAsDraft net bir iş hatası döndürdüyse (belge no / eksik bilgi / XML hatası değil),
        // CompressedSaveAsDraft'a düşme; gerçek mesajı sakla. Aksi halde compressed "(1,2)" gibi
        // yanıltıcı bir mesajla asıl nedeni maskeliyor.
        var isStructuralOrTransport = string.IsNullOrWhiteSpace(lastMessage)
            || IsMissingInvoiceInfoError(lastMessage)
            || IsXmlDocumentError(lastMessage);
        if (!isStructuralOrTransport)
            return (false, draftXml, lastMessage);

        if (IsDocumentNumberValidationError(lastMessage))
            return (false, draftXml, lastMessage);

        var compressed = await TryCompressedSaveAsDraftAsync(invoiceInfoXml, user, pass, ct);
        if (compressed.Success)
            return (true, compressed.ResponseXml, null);

        // Compressed de başarısızsa, SaveAsDraft'ın (varsa) daha anlamlı mesajını tercih et.
        var bestMessage = !string.IsNullOrWhiteSpace(lastMessage) ? lastMessage : compressed.ErrorMessage;
        return (false, compressed.ResponseXml ?? draftXml, bestMessage);
    }

    private static string BuildUyumsoftSendFailureMessage(string? lastMessage, string? documentId, string prefix)
    {
        if (IsMissingInvoiceInfoError(lastMessage) || IsXmlDocumentError(lastMessage))
        {
            return "Uyumsoft fatura XML'ini okuyamadı. " +
                   "InvoiceInfo/Invoice yapısı WSDL ile uyumsuz olabilir. " +
                   $"Denenen belge: {documentId ?? "-"}, seri: {prefix}. " +
                   "API'yi yeniden başlatıp yeni taslak gönderin. " +
                   $"Detay: {lastMessage}";
        }

        if (IsDocumentNumberValidationError(lastMessage))
        {
            return $"Belge numarası Uyumsoft tarafından reddedildi ({documentId ?? "-"}). " +
                   $"Portalda {prefix} serisinin son numarasını kontrol edip bağlantı testi yapın. Detay: {lastMessage}";
        }

        return string.IsNullOrWhiteSpace(lastMessage)
            ? "Uyumsoft taslak gönderimi başarısız."
            : lastMessage;
    }

    private static string BuildInvoiceInfoForUyumsoft(
        string ublInner,
        string localDocumentId,
        string receiverVkn,
        string? receiverAlias,
        string? buyerName,
        bool isEArchive,
        string scenario,
        string? buyerEmail)
    {
        // Uyumsoft alıcı unvanını zorunlu tutar (>=2 karakter). request.BuyerName boşsa UBL'den al.
        var resolvedTitle = string.IsNullOrWhiteSpace(buyerName)
            ? ExtractBuyerTitleFromUbl(ublInner)
            : buyerName.Trim();
        if (string.IsNullOrWhiteSpace(resolvedTitle) || resolvedTitle.Trim().Length < 2)
            resolvedTitle = "MUSTERI";

        var targetCustomer = new StringBuilder();
        targetCustomer.Append($@"<TargetCustomer VknTckn=""{XmlEscape(receiverVkn)}"" Title=""{XmlEscape(resolvedTitle)}""");
        if (!isEArchive && !string.IsNullOrWhiteSpace(receiverAlias))
            targetCustomer.Append($@" Alias=""{XmlEscape(receiverAlias)}""");
        targetCustomer.Append("/>");

        var eArchiveBlock = isEArchive
            ? @"<EArchiveInvoiceInfo DeliveryType=""Electronic""/>"
            : string.Empty;

        var notificationBlock = string.Empty;
        if (isEArchive && !string.IsNullOrWhiteSpace(buyerEmail))
        {
            notificationBlock = $@"<Notification>
        <Mailing Subject=""E-Arsiv Fatura"" EnableNotification=""true"" To=""{XmlEscape(buyerEmail)}"">
          <Attachment Xml=""true"" Pdf=""true""/>
        </Mailing>
      </Notification>";
        }

        var createDateUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var invoiceXml = PrepareUblInvoiceElementForUyumsoft(ublInner);

        // InvoiceInfo, SaveAsDraft xmlns=tempuri altında kalır; kendi xmlns'ini tekrar etmez.
        return $@"<InvoiceInfo LocalDocumentId=""{XmlEscape(localDocumentId)}"">
      {invoiceXml}
      {targetCustomer}
      {eArchiveBlock}
      <Scenario>{XmlEscape(scenario)}</Scenario>
      {notificationBlock}
      <CreateDateUtc>{createDateUtc}</CreateDateUtc>
    </InvoiceInfo>";
    }

    /// <summary>
    /// UBL AccountingCustomerParty içinden alıcı unvanı/adını çıkarır (PartyName &gt; Person).
    /// </summary>
    private static string ExtractBuyerTitleFromUbl(string ublInner)
    {
        try
        {
            var prepared = ublInner.Contains("xmlns:cbc=", StringComparison.OrdinalIgnoreCase)
                ? EnsureInvoiceUblNamespace(GibInvoiceNumber.SanitizeEmbeddedInvoice(ublInner))
                : GibInvoiceNumber.SanitizeEmbeddedInvoice(ublInner);
            var doc = XDocument.Parse(prepared);
            var customerParty = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "AccountingCustomerParty");
            var party = customerParty?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Party");
            if (party is null)
                return string.Empty;

            var name = party.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "PartyName")
                ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            var person = party.Descendants().FirstOrDefault(e => e.Name.LocalName == "Person");
            if (person is not null)
            {
                var first = person.Descendants().FirstOrDefault(e => e.Name.LocalName == "FirstName")?.Value?.Trim();
                var family = person.Descendants().FirstOrDefault(e => e.Name.LocalName == "FamilyName")?.Value?.Trim();
                var full = $"{first} {family}".Trim();
                if (!string.IsNullOrWhiteSpace(full))
                    return full;
            }
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    /// <summary>
    /// Uyumsoft WCF proxy: InvoiceInfo.Invoice öğesi tempuri.org altındadır (UBL Invoice-2 değil).
    /// Resmi XmlSerializer çıktısı: &lt;Invoice xmlns="http://tempuri.org/"&gt; + cbc/cac çocukları.
    /// Invoice köküne UBL default xmlns vermek Invoice'ı null yapar / XML (1,2) hatasına yol açar.
    /// </summary>
    private static string PrepareUblInvoiceElementForUyumsoft(string ublInner)
    {
        var xml = UyumsoftUblPartyNormalizer.NormalizeForPortalDisplay(
            GibInvoiceNumber.SanitizeEmbeddedInvoice(ublInner));
        xml = xml.Trim().TrimStart('\uFEFF');

        while (xml.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
        {
            var end = xml.IndexOf("?>", StringComparison.Ordinal);
            if (end < 0) break;
            xml = xml[(end + 2)..].TrimStart().TrimStart('\uFEFF');
        }

        if (!xml.StartsWith("<Invoice", StringComparison.OrdinalIgnoreCase))
        {
            var idx = xml.IndexOf("<Invoice", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                xml = xml[idx..];
        }

        // UBL default xmlns'i Invoice kökünden kaldır — tempuri parent'tan miras alınır.
        xml = Regex.Replace(
            xml,
            @"\sxmlns\s*=\s*""urn:oasis:names:specification:ubl:schema:xsd:Invoice-2""",
            string.Empty,
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        xml = Regex.Replace(
            xml,
            @"\sxmlns\s*=\s*'urn:oasis:names:specification:ubl:schema:xsd:Invoice-2'",
            string.Empty,
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        // Prefix bildirimlerini garanti et (çocuk elemanlar cbc:/cac:/ext: kullanır).
        if (!xml.Contains("xmlns:cbc=", StringComparison.OrdinalIgnoreCase))
            xml = Regex.Replace(xml, @"^(<Invoice\b)", $@"$1 xmlns:cbc=""{Cbc.NamespaceName}""", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!xml.Contains("xmlns:cac=", StringComparison.OrdinalIgnoreCase))
            xml = Regex.Replace(xml, @"^(<Invoice\b)", $@"$1 xmlns:cac=""{Cac.NamespaceName}""", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!xml.Contains("xmlns:ext=", StringComparison.OrdinalIgnoreCase))
            xml = Regex.Replace(xml, @"^(<Invoice\b)", @"$1 xmlns:ext=""urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2""", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        _ = XDocument.Parse(xml);
        return xml;
    }

    private static string BuildDraftSoapBody(string soapOperation, string invoiceInfoXml)
    {
        return $@"<{soapOperation} xmlns=""{TempUri}"">
      <invoices>
        {invoiceInfoXml}
      </invoices>
    </{soapOperation}>";
    }

    private static string BuildValidateInvoiceBody(string ublInner)
    {
        var invoiceForValidation = Regex.Replace(ublInner, @"</Invoice>\s*$", "</invoice>", RegexOptions.IgnoreCase);
        invoiceForValidation = Regex.Replace(invoiceForValidation, @"^<Invoice\b", "<invoice", RegexOptions.IgnoreCase);
        return $@"<ValidateInvoice xmlns=""{TempUri}"">{invoiceForValidation}</ValidateInvoice>";
    }

    private async Task<string?> ValidateUblAsync(
        string ublInner,
        string user,
        string pass,
        CancellationToken ct)
    {
        try
        {
            var body = BuildValidateInvoiceBody(ublInner);
            var xml = await SendSoapAsync("ValidateInvoice", body, user, pass, ct);
            if (TryGetResponseSuccess(xml, out var message) && IsFlagResponseTrue(xml))
                return null;

            return GetResultMessage(xml) ?? message ?? "UBL doğrulaması başarısız.";
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool Success, string ResponseXml, string? ErrorMessage)> TryCompressedSaveAsDraftAsync(
        string invoiceInfoXml,
        string user,
        string pass,
        CancellationToken ct)
    {
        var payloadXml = $@"<ArrayOfInvoiceInfo xmlns=""{TempUri}"">{invoiceInfoXml}</ArrayOfInvoiceInfo>";
        var payloadBytes = Encoding.UTF8.GetBytes(payloadXml);
        var zipBytes = CreateZipArchive("Invoices.xml", payloadBytes);
        var data = Convert.ToBase64String(zipBytes);
        var hash = Convert.ToBase64String(MD5.HashData(payloadBytes));

        var body = $@"<CompressedSaveAsDraft xmlns=""{TempUri}"">
      <data>
        <Hash>{XmlEscape(hash)}</Hash>
        <Data>{data}</Data>
      </data>
    </CompressedSaveAsDraft>";
        var xml = await SendSoapAsync("CompressedSaveAsDraft", body, user, pass, ct);
        if (TryGetResponseSuccess(xml, out _))
            return (true, xml, null);

        return (false, xml, GetResultMessage(xml));
    }

    private static byte[] CreateZipArchive(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(content, 0, content.Length);
        }

        return ms.ToArray();
    }

    private static bool IsXmlDocumentError(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
               && message.Contains("error in XML document", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingInvoiceInfoError(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
               && message.Contains("Fatura bilgisi yok", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetryableCompressedError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return true;

        return message.Contains("Zip", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Hash", StringComparison.OrdinalIgnoreCase)
               || message.Contains("MD5", StringComparison.OrdinalIgnoreCase)
               || IsMissingInvoiceInfoError(message);
    }

    private static string? GetResultMessage(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("Result", StringComparison.Ordinal));
            return result?.Attribute("Message")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static List<EInvoiceIncomingItem> ParseIncomingInvoiceInfos(string xml)
    {
        var result = new List<EInvoiceIncomingItem>();
        var doc = XDocument.Parse(xml);
        foreach (var info in doc.Descendants().Where(e => e.Name.LocalName == "InvoiceInfo"))
        {
            var invoiceEl = info.Elements().FirstOrDefault(e => e.Name.LocalName == "Invoice");
            if (invoiceEl is null)
                continue;

            var uuid = invoiceEl.Element(Cbc + "UUID")?.Value
                       ?? info.Attribute("LocalDocumentId")?.Value
                       ?? Guid.NewGuid().ToString("D");
            var invoiceNumber = invoiceEl.Element(Cbc + "ID")?.Value ?? "";
            var issueDateText = invoiceEl.Element(Cbc + "IssueDate")?.Value;
            DateTime? issueDate = DateTime.TryParse(issueDateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
                ? d
                : null;

            var supplierParty = invoiceEl.Element(Cac + "AccountingSupplierParty")
                ?.Element(Cac + "Party");
            var senderName = supplierParty?.Element(Cac + "PartyName")?.Element(Cbc + "Name")?.Value
                             ?? supplierParty?.Element(Cac + "Person")?.Element(Cbc + "FirstName")?.Value
                             ?? "";
            var senderTax = supplierParty?.Element(Cac + "PartyIdentification")
                ?.Elements()
                .Select(e => e.Element(Cbc + "ID")?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                ?? "";

            var payable = invoiceEl.Element(Cac + "LegalMonetaryTotal")?.Element(Cbc + "PayableAmount")?.Value;
            decimal.TryParse(payable, NumberStyles.Any, CultureInfo.InvariantCulture, out var payableAmount);
            var currency = invoiceEl.Element(Cbc + "DocumentCurrencyCode")?.Value ?? "TRY";
            var profileId = invoiceEl.Element(Cbc + "ProfileID")?.Value ?? "";
            var docType = string.Equals(profileId, "EARSIVFATURA", StringComparison.OrdinalIgnoreCase) ? "EArsiv" : "EFatura";

            result.Add(new EInvoiceIncomingItem(
                uuid,
                invoiceNumber,
                senderName.Trim(),
                senderTax.Trim(),
                docType,
                "Received",
                "",
                payableAmount,
                currency,
                issueDate,
                null,
                invoiceEl.ToString(SaveOptions.DisableFormatting),
                issueDate?.ToUniversalTime(),
                null,
                null,
                null,
                profileId));
        }

        return result;
    }

    private sealed record ParsedInvoiceIdentity(string? Id, string? Number, string? Scenario);

    private static ParsedInvoiceIdentity? ParseInvoiceIdentity(string xml)
    {
        var doc = XDocument.Parse(xml);
        var value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "InvoiceIdentity");
        if (value is null)
            return null;
        return new ParsedInvoiceIdentity(
            value.Attribute("Id")?.Value,
            value.Attribute("Number")?.Value,
            value.Attribute("InvoiceScenario")?.Value);
    }

    private static string? ReadJson(string? json, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindProperty(doc.RootElement, names);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadJsonInt(string? json, string name, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryFindIntProperty(doc.RootElement, name, out value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindIntProperty(JsonElement element, string name, out int value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out var direct))
            {
                if (direct.ValueKind == JsonValueKind.Number && direct.TryGetInt32(out value))
                    return true;
                if (direct.ValueKind == JsonValueKind.String
                    && int.TryParse(direct.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    return true;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (TryFindIntProperty(prop.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindIntProperty(item, name, out value))
                    return true;
            }
        }

        return false;
    }

    private static string? FindProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
                    return direct.GetString();
            }
            foreach (var p in element.EnumerateObject())
            {
                var nested = FindProperty(p.Value, names);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindProperty(item, names);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        return null;
    }

    private static string? GetXmlValue(string xml, string localName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var x = XDocument.Parse(xml);
        return x.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
    }

    private static string? GetAttributeValue(string xml, string attrName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var x = XDocument.Parse(xml);
        return x.Descendants()
            .Select(e => e.Attribute(attrName)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string? ExtractReceiverAlias(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var aliases = new List<string>();
        foreach (var el in XDocument.Parse(xml).Descendants())
        {
            var name = el.Name.LocalName;
            if (name is not ("ReceiverboxAliases" or "SystemUserAlias" or "Alias" or "UserAlias" or "ReceiverAlias"))
                continue;

            var fromAttr = el.Attribute("Alias")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(fromAttr))
                aliases.Add(fromAttr);

            var fromValue = el.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(fromValue) &&
                fromValue.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
                aliases.Add(fromValue);
        }

        return aliases.FirstOrDefault(a => a.StartsWith("urn:mail:", StringComparison.OrdinalIgnoreCase))
            ?? aliases.FirstOrDefault(a => a.StartsWith("urn:", StringComparison.OrdinalIgnoreCase));
    }

    private static string XmlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return SecurityElement.Escape(text) ?? string.Empty;
    }

    private static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Uyumsoft işlemi başarısız.";
        return message.Trim();
    }
}
