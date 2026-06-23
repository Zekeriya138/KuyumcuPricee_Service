using System.Collections.Concurrent;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace kuyumcu_infrastructure.Services;

public sealed class EdmSoapEInvoiceProviderAdapter : IEInvoiceProviderAdapter
{
    private static readonly ConcurrentDictionary<string, (string SessionId, DateTime ExpiresAtUtc)> SessionCache = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;

    public EdmSoapEInvoiceProviderAdapter(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public string ProviderCode => "edm";

    public async Task<EInvoiceConnectionTestResult> TestConnectionAsync(EInvoiceConnectionTestRequest request, CancellationToken ct)
    {
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            if (string.IsNullOrWhiteSpace(session))
                return new EInvoiceConnectionTestResult(false, "EDM login başarısız: SESSION_ID alınamadı.");

            return new EInvoiceConnectionTestResult(true, "EDM login başarılı, SESSION_ID alındı.");
        }
        catch (Exception ex)
        {
            return new EInvoiceConnectionTestResult(false, NormalizeEdmErrorMessage(ex.Message));
        }
    }

    public async Task<EInvoiceSendResult> SendOutgoingAsync(EInvoiceSendRequest request, CancellationToken ct)
    {
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            var senderVkn = ReadJson(request.PayloadJson, "senderVkn");
            var receiverVkn = ReadJson(request.PayloadJson, "receiverVkn", "buyerTaxNo", "buyerTaxNumber");
            var receiverAlias = ReadJson(request.PayloadJson, "receiverAlias", "buyerAlias", "buyerEmail");
            var fromAlias = ReadJson(request.PayloadJson, "fromAlias", "senderAlias");
            var toAlias = ReadJson(request.PayloadJson, "toAlias", "receiverAlias", "buyerEmail");
            var isEArchive = string.Equals(request.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
            var content = ReadJson(request.PayloadJson, "ublBase64");

            senderVkn = string.IsNullOrWhiteSpace(senderVkn) ? _cfg["EInvoice:Edm:CompanyTaxNumber"] : senderVkn;
            receiverVkn = string.IsNullOrWhiteSpace(receiverVkn) ? request.BuyerTaxNumber : receiverVkn;
            receiverAlias = string.IsNullOrWhiteSpace(receiverAlias) ? _cfg["EInvoice:Edm:DefaultReceiverAlias"] : receiverAlias;
            fromAlias = string.IsNullOrWhiteSpace(fromAlias) ? _cfg["EInvoice:Edm:SenderAlias"] : fromAlias;
            toAlias = string.IsNullOrWhiteSpace(toAlias) ? receiverAlias : toAlias;

            if (string.IsNullOrWhiteSpace(senderVkn))
                return new EInvoiceSendResult(false, null, null, null, null, null, "EDM gönderim hatası: Gönderici vergi numarası (senderVkn) boş.");
            if (string.IsNullOrWhiteSpace(receiverVkn))
                return new EInvoiceSendResult(false, null, null, null, null, null, "EDM gönderim hatası: Alıcı vergi numarası (receiverVkn) boş.");
            if (string.IsNullOrWhiteSpace(fromAlias))
                return new EInvoiceSendResult(false, null, null, null, null, null, "EDM gönderim hatası: Gönderici etiketi (SenderAlias/SenderLabel) tanımlı değil.");
            if (!isEArchive && (string.IsNullOrWhiteSpace(receiverAlias) || string.IsNullOrWhiteSpace(toAlias)))
            {
                return new EInvoiceSendResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "EDM gönderim hatası: e-Fatura için alıcı etiketi (receiverAlias/toAlias) zorunlu. Alıcı mükellef etiketi girin veya belge tipini e-Arşiv kullanın.");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                // Geçiş döneminde UBL hazır değilse payload'ı base64'e çevirerek hata yönetimini görünür tutuyoruz.
                content = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.PayloadJson ?? "{}"));
            }

            var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <SendInvoiceRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml(session)}
            <RECEIVER vkn=""{XmlEscape(receiverVkn)}"" alias=""{XmlEscape(receiverAlias)}"" xmlns="""" />
            <INVOICE TRXID=""0"" xmlns="""">
                <HEADER>
                    <SENDER>{XmlEscape(senderVkn)}</SENDER>
                    <RECEIVER>{XmlEscape(receiverVkn)}</RECEIVER>
                    <FROM>{XmlEscape(fromAlias)}</FROM>
                    <TO>{XmlEscape(toAlias)}</TO>
                    <INTERNETSALES>false</INTERNETSALES>
                    <EARCHIVE>{(isEArchive ? "true" : "false")}</EARCHIVE>
                    <EARCHIVE_REPORT_SENDDATE>0001-01-01</EARCHIVE_REPORT_SENDDATE>
                    <CANCEL_EARCHIVE_REPORT_SENDDATE>0001-01-01</CANCEL_EARCHIVE_REPORT_SENDDATE>
                </HEADER>
                <CONTENT>{content}</CONTENT>
            </INVOICE>
        </SendInvoiceRequest>
    </s:Body>
</s:Envelope>";

            var xml = await SendSoapAsync("SendInvoice", body, ct);
            EnsureSoapFault(xml);

            var returnCode = GetXmlValue(xml, "RETURN_CODE");
            if (!string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase))
            {
                return new EInvoiceSendResult(false, null, null, null, null, xml, $"EDM SendInvoice RETURN_CODE={returnCode}");
            }

            var intlTxn = GetXmlValue(xml, "INTL_TXN_ID");
            var uuid = GetInvoiceAttribute(xml, "UUID");
            var id = GetInvoiceAttribute(xml, "ID");
            var status = GetXmlValue(xml, "STATUS");

            return new EInvoiceSendResult(
                true,
                string.IsNullOrWhiteSpace(intlTxn) ? null : intlTxn,
                uuid,
                id,
                NormalizeEdmStatus(status),
                xml,
                null);
        }
        catch (Exception ex)
        {
            return new EInvoiceSendResult(false, null, null, null, null, null, $"EDM gönderim hatası: {NormalizeEdmErrorMessage(ex.Message)}");
        }
    }

    public async Task<EInvoiceStatusResult> GetStatusAsync(EInvoiceStatusRequest request, CancellationToken ct)
    {
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            var uuid = request.Uuid ?? request.IntegratorDocumentId;
            var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <GetInvoiceStatusRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml(session)}
            <INVOICE TRXID=""0"" UUID=""{XmlEscape(uuid)}"" xmlns="""" />
        </GetInvoiceStatusRequest>
    </s:Body>
</s:Envelope>";

            var xml = await SendSoapAsync("GetInvoiceStatus", body, ct);
            EnsureSoapFault(xml);
            var status = GetXmlValue(xml, "STATUS");
            return new EInvoiceStatusResult(true, NormalizeEdmStatus(status), DateTime.UtcNow, xml, null);
        }
        catch (Exception ex)
        {
            return new EInvoiceStatusResult(false, null, null, null, $"EDM durum sorgu hatası: {NormalizeEdmErrorMessage(ex.Message)}");
        }
    }

    public async Task<EInvoiceCancelResult> CancelAsync(EInvoiceCancelRequest request, CancellationToken ct)
    {
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            var uuid = string.IsNullOrWhiteSpace(request.Uuid) ? request.IntegratorDocumentId : request.Uuid;
            var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <CancelInvoiceRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml(session, request.Reason)}
            <INVOICE TRXID=""0"" UUID=""{XmlEscape(uuid)}"" xmlns="""" />
        </CancelInvoiceRequest>
    </s:Body>
</s:Envelope>";

            var xml = await SendSoapAsync("CancelInvoice", body, ct);
            EnsureSoapFault(xml);
            var returnCode = GetXmlValue(xml, "RETURN_CODE");
            if (!string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase))
                return new EInvoiceCancelResult(false, null, xml, $"EDM CancelInvoice RETURN_CODE={returnCode}");

            return new EInvoiceCancelResult(true, "Cancelled", xml, null);
        }
        catch (Exception ex)
        {
            return new EInvoiceCancelResult(false, null, null, $"EDM iptal hatası: {NormalizeEdmErrorMessage(ex.Message)}");
        }
    }

    public Task<EInvoiceWebhookVerificationResult> VerifyWebhookAsync(EInvoiceWebhookVerificationRequest request, CancellationToken ct)
    {
        // EDM SOAP akışında durumlar aktif polling ile takip edilir; webhook doğrulaması kullanılmıyor.
        return Task.FromResult(new EInvoiceWebhookVerificationResult(false, null, null, null, null, "EDM SOAP webhook doğrulaması tanımlı değil."));
    }

    public async Task<EdmMmSendResult> SendMmAsync(EdmMmSendRequest request, CancellationToken ct)
    {
        string? requestEnvelope = null;
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            var payload = request.PayloadJson ?? "{}";
            var mmNo = ReadJson(payload, "documentNo", "mmNo", "expenseSlipNo") ?? request.DocumentNo;
            // Prod güvenliği: gönderici VKN/TCKN yalnızca aktif şube profilinden gelir.
            var senderVkn = NormalizeTaxNumber(request.SenderTaxNumber);
            var receiverVkn = NormalizeTaxNumber(ReadJson(payload, "receiverVkn", "buyerTaxNo", "buyerTaxNumber"))
                              ?? NormalizeTaxNumber(request.BuyerTaxNumber);
            var grandTotal = ParseDecimal(ReadJson(payload, "grandTotal", "total", "amount")) ?? 0m;
            if (grandTotal <= 0m)
                grandTotal = ParseDecimal(ReadJson(payload, "Toplam", "ToolamOdenecekTutar")) ?? 0m;
            if (grandTotal <= 0m)
                grandTotal = 1m;

            var mmParts = mmNo.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var seri = mmParts.FirstOrDefault() ?? "GPS";
            var siraNo = mmParts.Length > 1 ? mmParts[^1] : mmNo;
            var buyerName = ReadJson(payload, "buyerName", "unvan", "adSoyad") ?? "Gider Pusulası Alıcısı";
            var buyerAddress = ReadJson(payload, "buyerAddress", "address");
            var senderName = ReadJson(payload, "senderName", "companyName")
                             ?? request.SenderName
                             ?? "Gönderici";
            var senderAddress = ReadJson(payload, "senderAddress", "companyAddress")
                                ?? request.SenderAddress
                                ?? "-";
            var senderTaxOffice = ReadJson(payload, "senderTaxOffice", "taxOffice")
                                  ?? request.SenderTaxOffice
                                  ?? "-";
            var senderMail = ReadJson(payload, "senderMail", "mail")
                             ?? request.SenderMail
                             ?? "no-reply@example.com";
            var city = ReadJson(payload, "city", "sehir")
                       ?? request.SenderCity
                       ?? "-";
            var cityPlateCode = ResolveCityPlateCode(city);
            if (string.IsNullOrWhiteSpace(cityPlateCode))
                return new EdmMmSendResult(false, null, null, null, null, "EDM MM gönderim hatası: Sehir için geçerli il plaka kodu bulunamadı. (Örn: 34)");
            var lineMahiyet = ReadJson(payload, "workmanship", "lineMahiyet", "isinMahiyeti", "mahiyet") ?? "Hizmet";
            var lineCinsi = ReadJson(payload, "productType", "lineType", "cinsi", "description", "lineName") ?? "Gider Pusulası";
            var quantity = ParseDecimal(ReadJson(payload, "quantityGram", "quantity", "adet")) ?? 0m;
            var unitPrice = ParseDecimal(ReadJson(payload, "unitPrice", "fiyat")) ?? 0m;
            var lineTotal = ParseDecimal(ReadJson(payload, "lineTotal", "tutar", "grandTotal", "total", "amount")) ?? 0m;
            if (lineTotal <= 0m) lineTotal = grandTotal;
            if (quantity <= 0m) quantity = 1m;
            if (unitPrice <= 0m) unitPrice = quantity > 0m ? (lineTotal / quantity) : lineTotal;
            if (unitPrice <= 0m) unitPrice = lineTotal;
            var buyerMail = ReadJson(payload, "buyerEmail", "aliciMail", "mail") ?? "no-reply@example.com";
            var amountTextTr = lineTotal.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
            var totalAmountTextInvariant = lineTotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            var unitPriceTextTr = unitPrice.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
            var quantityText = quantity.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

            var sessionCompany = await TryGetSessionCompanyInfoAsync(session, ct);
            if (IsGenericSenderName(senderName) && !string.IsNullOrWhiteSpace(sessionCompany?.CompanyName))
                senderName = sessionCompany!.CompanyName!;
            if (IsPlaceholder(senderAddress) && !string.IsNullOrWhiteSpace(sessionCompany?.Address))
                senderAddress = sessionCompany!.Address!;
            if (IsPlaceholder(senderTaxOffice) && !string.IsNullOrWhiteSpace(sessionCompany?.TaxOffice))
                senderTaxOffice = sessionCompany!.TaxOffice!;
            if (string.IsNullOrWhiteSpace(senderMail) && !string.IsNullOrWhiteSpace(sessionCompany?.Email))
                senderMail = sessionCompany!.Email!;

            if (string.IsNullOrWhiteSpace(senderVkn))
                return new EdmMmSendResult(false, null, null, null, null, "EDM MM gönderim hatası: aktif şube e-fatura profilinde Vergi No (TaxNumber) zorunludur.");
            if (string.IsNullOrWhiteSpace(receiverVkn))
                return new EdmMmSendResult(false, null, null, null, null, "EDM MM gönderim hatası: alıcı TCKN/VKN boş.");

            var headerXml = BuildRequestHeaderXml(session, "Gider Pusulası gönderim işlemi");
            var expenseSlipXml = BuildExpenseSlipXml(
                seri,
                siraNo,
                DateTime.Now,
                cityPlateCode,
                senderVkn,
                senderName,
                senderAddress,
                senderMail,
                senderTaxOffice,
                buyerName,
                receiverVkn,
                buyerAddress ?? "-",
                buyerMail,
                cityPlateCode,
                lineMahiyet,
                lineCinsi,
                quantityText,
                unitPriceTextTr,
                amountTextTr,
                totalAmountTextInvariant);
            var expenseSlipXmlUnqualifiedRoot = BuildExpenseSlipXmlUnqualifiedRootQualifiedChildren(
                seri,
                siraNo,
                DateTime.Now,
                cityPlateCode,
                senderVkn,
                senderName,
                senderAddress,
                senderMail,
                senderTaxOffice,
                buyerName,
                receiverVkn,
                buyerAddress ?? "-",
                buyerMail,
                cityPlateCode,
                lineMahiyet,
                lineCinsi,
                quantityText,
                unitPriceTextTr,
                amountTextTr,
                totalAmountTextInvariant);

            // EDM tarafı farklı namespace serileştirmelerini farklı ortamlarda farklı şekilde kabul edebiliyor.
            var candidateBodies = new[]
            {
                $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><LoadExpenseSlipRequest xmlns=""http://tempuri.org/"">{headerXml}{expenseSlipXmlUnqualifiedRoot}</LoadExpenseSlipRequest></s:Body></s:Envelope>",
                $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><LoadExpenseSlipRequest xmlns=""http://tempuri.org/"">{headerXml}{expenseSlipXml}</LoadExpenseSlipRequest></s:Body></s:Envelope>",
                $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/""><s:Body><LoadExpenseSlipRequest xmlns=""http://tempuri.org/"">{headerXml}<ExpenseSlip xmlns="""">{expenseSlipXml.Replace("<ExpenseSlip>", string.Empty).Replace("</ExpenseSlip>", string.Empty)}</ExpenseSlip></LoadExpenseSlipRequest></s:Body></s:Envelope>"
            };

            string? lastError = null;
            string? lastDebug = null;
            var attemptDebug = new List<string>();
            var attemptNo = 0;
            foreach (var body in candidateBodies)
            {
                attemptNo++;
                requestEnvelope = body;
                try
                {
                    var xml = await SendSoapWithActionFallbackAsync("LoadExpenseSlip", body, ct);
                    EnsureSoapFault(xml);
                    var returnCode = GetXmlValue(xml, "RETURN_CODE");
                    var successText = GetXmlValue(xml, "SUCCES") ?? GetXmlValue(xml, "SUCCESS");
                    var message = GetXmlValue(xml, "MESSAGE");

                    if (!string.IsNullOrWhiteSpace(returnCode) &&
                        !string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = $"EDM LoadExpenseSlip RETURN_CODE={returnCode}";
                        lastDebug = ComposeRequestResponseDebug(requestEnvelope, xml);
                        attemptDebug.Add($"ATTEMPT {attemptNo}: RETURN_CODE={returnCode}\n{lastDebug}");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(successText))
                    {
                        var success = IsSoapTrue(successText);
                        if (!success)
                        {
                            var businessMessage = string.IsNullOrWhiteSpace(message)
                                ? "EDM MM gönderim hatası."
                                : $"EDM MM gönderim hatası: {message.Trim()}";
                            var businessDebug = ComposeRequestResponseDebug(requestEnvelope, xml);
                            return new EdmMmSendResult(false, null, null, businessDebug, null, businessMessage);
                        }
                    }

                    var intlTxn = GetXmlValue(xml, "INTL_TXN_ID");
                    var uuid = GetXmlValue(xml, "UUID") ?? GetInvoiceAttribute(xml, "UUID");
                    var status = GetXmlValue(xml, "STATUS");
                    return new EdmMmSendResult(true, string.IsNullOrWhiteSpace(intlTxn) ? null : intlTxn, uuid, xml, NormalizeEdmMmStatus(status));
                }
                catch (Exception ex)
                {
                    var (errorMessage, faultXml) = ExtractSoapException(ex);
                    lastError = $"EDM MM gönderim hatası: {NormalizeEdmErrorMessage(errorMessage)}";
                    lastDebug = ComposeRequestResponseDebug(requestEnvelope, faultXml);
                    attemptDebug.Add($"ATTEMPT {attemptNo}: {lastError}\n{lastDebug}");
                    // NullReference ise diğer şablonu da dene; farklı hata ise doğrudan bırak.
                    if (!errorMessage.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }

            var combinedDebug = attemptDebug.Count == 0
                ? lastDebug
                : string.Join("\n\n==============================\n\n", attemptDebug);
            return new EdmMmSendResult(false, null, null, combinedDebug, null, lastError ?? "EDM MM gönderim hatası.");
        }
        catch (Exception ex)
        {
            var (errorMessage, faultXml) = ExtractSoapException(ex);
            return new EdmMmSendResult(
                false,
                null,
                null,
                ComposeRequestResponseDebug(requestEnvelope, faultXml),
                null,
                $"EDM MM gönderim hatası: {NormalizeEdmErrorMessage(errorMessage)}");
        }
    }

    public async Task<EdmMmStatusResult> GetMmStatusAsync(EdmMmStatusRequest request, CancellationToken ct)
    {
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            var mmNo = request.DocumentNo;
            if (string.IsNullOrWhiteSpace(mmNo))
                mmNo = string.IsNullOrWhiteSpace(request.IntegratorDocumentId) ? request.Uuid : request.IntegratorDocumentId;
            if (string.IsNullOrWhiteSpace(mmNo))
                return new EdmMmStatusResult(false, null, null, "EDM MM durum sorgu hatası: referans yok.");

            var mmParts = mmNo.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var seri = mmParts.FirstOrDefault() ?? "GPS";
            var siraNo = mmParts.Length > 1 ? mmParts[^1] : mmNo;
            var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <GetExpenseSlipRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml(session, "Gider Pusulası durum sorgu işlemi")}
            <ExpenseSlip_SEARCH_KEY xmlns="""">
                <SERINO>{XmlEscape(seri)}</SERINO>
                <SIRANO>{XmlEscape(siraNo)}</SIRANO>
            </ExpenseSlip_SEARCH_KEY>
        </GetExpenseSlipRequest>
    </s:Body>
</s:Envelope>";

            var xml = await SendSoapWithActionFallbackAsync("GetExpenseSlip", body, ct);
            EnsureSoapFault(xml);
            var status = GetXmlValue(xml, "STATUS");
            if (string.IsNullOrWhiteSpace(status))
            {
                var resultCode = GetXmlValue(xml, "result");
                var totalCountRaw = GetXmlValue(xml, "TotalEsListCount");
                if (int.TryParse(totalCountRaw, out var totalCount) && totalCount > 0)
                    status = "Delivered";
                else if (int.TryParse(resultCode, out var rc) && rc > 0)
                    status = "Sent";
            }
            return new EdmMmStatusResult(true, NormalizeEdmMmStatus(status), xml, null);
        }
        catch (Exception ex)
        {
            return new EdmMmStatusResult(false, null, null, $"EDM MM durum sorgu hatası: {NormalizeEdmErrorMessage(ex.Message)}");
        }
    }

    public async Task<EdmMmCancelResult> CancelMmAsync(EdmMmCancelRequest request, CancellationToken ct)
    {
        try
        {
            var session = await LoginAsync(request.IntegratorUsername, request.IntegratorPassword, ct);
            var refNo = string.IsNullOrWhiteSpace(request.IntegratorDocumentId) ? request.Uuid : request.IntegratorDocumentId;
            if (string.IsNullOrWhiteSpace(refNo))
                return new EdmMmCancelResult(false, null, null, "EDM MM iptal hatası: referans yok.");

            var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <CancelMMRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml(session, request.Reason)}
            <MM TRXID=""0"" UUID=""{XmlEscape(refNo)}"" xmlns="""" />
        </CancelMMRequest>
    </s:Body>
</s:Envelope>";

            var xml = await SendSoapWithActionFallbackAsync("CancelMM", body, ct);
            EnsureSoapFault(xml);
            var returnCode = GetXmlValue(xml, "RETURN_CODE");
            if (!string.Equals(returnCode, "0", StringComparison.OrdinalIgnoreCase))
                return new EdmMmCancelResult(false, null, xml, $"EDM CancelMM RETURN_CODE={returnCode}");

            return new EdmMmCancelResult(true, "Cancelled", xml, null);
        }
        catch (Exception ex)
        {
            return new EdmMmCancelResult(false, null, null, $"EDM MM iptal hatası: {NormalizeEdmErrorMessage(ex.Message)}");
        }
    }

    public async Task<EdmTaxpayerQueryResult> QueryTaxpayerAsync(string? username, string? password, string taxNumber, CancellationToken ct)
    {
        var normalizedTaxNo = new string((taxNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (normalizedTaxNo.Length != 10 && normalizedTaxNo.Length != 11)
            return new EdmTaxpayerQueryResult(false, null, null, null, "TCKN/VKN 10 veya 11 hane olmalıdır.");

        try
        {
            var session = await LoginAsync(username, password, ct);
            if (string.IsNullOrWhiteSpace(session))
                return new EdmTaxpayerQueryResult(false, null, null, null, "EDM oturumu açılamadı.");

            // EDM dokümantasyonundaki mükellef sorgu endpointleri tenant bazında farklılaşabildiği için
            // burada güvenli fallback olarak VKN/TCKN kuralını uyguluyoruz.
            // İleride kurumunuzdaki CheckUser/GetUserList SOAP aksiyonu netleştiğinde bu bölüm doğrudan EDM sorgusuna çevrilebilir.
            var isEInvoice = normalizedTaxNo.Length == 10;
            return new EdmTaxpayerQueryResult(
                true,
                isEInvoice,
                null,
                null,
                "EDM oturumu doğrulandı; mükellefiyet VKN/TCKN kuralı ile belirlendi.");
        }
        catch (Exception ex)
        {
            return new EdmTaxpayerQueryResult(false, null, null, null, NormalizeEdmErrorMessage(ex.Message));
        }
    }

    private async Task<string> LoginAsync(string? username, string? password, CancellationToken ct)
    {
        var user = !string.IsNullOrWhiteSpace(username) ? username : _cfg["EInvoice:Edm:Username"];
        var pass = !string.IsNullOrWhiteSpace(password) ? password : _cfg["EInvoice:Edm:Password"];
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            throw new InvalidOperationException("EDM kullanıcı adı/şifre tanımlı değil.");

        var cacheKey = $"{_http.BaseAddress}|{user}";
        if (SessionCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
            return cached.SessionId;

        var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <LoginRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml("0")}
            <USER_NAME xmlns="""">{XmlEscape(user)}</USER_NAME>
            <PASSWORD xmlns="""">{XmlEscape(pass)}</PASSWORD>
        </LoginRequest>
    </s:Body>
</s:Envelope>";

        var xml = await SendSoapAsync("Login", body, ct);
        EnsureSoapFault(xml);
        var sessionId = GetXmlValue(xml, "SESSION_ID");
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("EDM Login response içinde SESSION_ID yok.");

        SessionCache[cacheKey] = (sessionId, DateTime.UtcNow.AddMinutes(20));
        return sessionId;
    }

    private async Task<string> SendSoapAsync(string operation, string envelopeXml, CancellationToken ct, string? soapActionOverride = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "");
        req.Content = new StringContent(envelopeXml, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPAction", soapActionOverride ?? ResolveSoapAction(operation));
        var res = await _http.SendAsync(req, ct);
        var xml = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode && !HasSoapFault(xml))
        {
            var bodyPreview = string.IsNullOrWhiteSpace(xml)
                ? string.Empty
                : (xml.Length > 600 ? xml[..600] + "..." : xml);
            throw new InvalidOperationException($"EDM SOAP HTTP {(int)res.StatusCode}: {res.ReasonPhrase}. {bodyPreview}".Trim());
        }
        return xml;
    }

    private async Task<string> SendSoapWithActionFallbackAsync(string operation, string envelopeXml, CancellationToken ct)
    {
        var configured = _cfg[$"EInvoice:Edm:SoapActions:{operation}"];
        var candidates = BuildSoapActionCandidates(operation, configured).ToList();
        string? lastXml = null;
        Exception? lastEx = null;

        foreach (var action in candidates)
        {
            try
            {
                var xml = await SendSoapAsync(operation, envelopeXml, ct, action);
                if (!IsContractFilterMismatch(xml))
                    return xml;
                lastXml = xml;
            }
            catch (Exception ex)
            {
                if (!IsContractFilterMismatch(ex.Message))
                    throw;
                lastEx = ex;
            }
        }

        if (!string.IsNullOrWhiteSpace(lastXml))
            return lastXml;
        if (lastEx is not null)
            throw new InvalidOperationException(
                "EDM e-MM action uyuşmazlığı: SendMM/GetMMStatus/CancelMM için endpoint veya SOAPAction eşleşmiyor. " +
                "EInvoice:Edm:SoapActions altında MM aksiyonlarını EDM servisinizdeki gerçek action değerleriyle tanımlayın.",
                lastEx);

        throw new InvalidOperationException("EDM e-MM action fallback başarısız.");
    }

    private static IEnumerable<string> BuildSoapActionCandidates(string operation, string? configured)
    {
        var baseCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            baseCandidates.Add(configured!.Trim());

        // EDM uç noktalarında action formatı ortama göre değişebildiği için birkaç güvenli aday denenir.
        baseCandidates.Add($"{operation}Request");
        baseCandidates.Add(operation);
        baseCandidates.Add($"http://tempuri.org/{operation}");
        baseCandidates.Add($"http://tempuri.org/{operation}Request");
        baseCandidates.Add($"http://tempuri.org/IEFaturaEDM/{operation}");
        baseCandidates.Add($"http://tempuri.org/IEFaturaEDM/{operation}Request");

        return baseCandidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsContractFilterMismatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("ContractFilter mismatch", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("cannot be processed at the receiver", StringComparison.OrdinalIgnoreCase);
    }

    private static (string errorMessage, string? rawFaultXml) ExtractSoapException(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        const string marker = "---SOAP-FAULT-XML---";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return (message, null);

        var err = message[..idx].Trim();
        var raw = message[(idx + marker.Length)..].Trim();
        return (string.IsNullOrWhiteSpace(err) ? "EDM SOAP fault" : err, string.IsNullOrWhiteSpace(raw) ? null : raw);
    }

    private static string? ComposeRequestResponseDebug(string? requestXml, string? responseXml)
    {
        if (string.IsNullOrWhiteSpace(requestXml) && string.IsNullOrWhiteSpace(responseXml))
            return null;
        var req = string.IsNullOrWhiteSpace(requestXml) ? "<yok>" : requestXml!;
        var res = string.IsNullOrWhiteSpace(responseXml) ? "<yok>" : responseXml!;
        return $"REQUEST_XML:\n{req}\n\nRESPONSE_XML:\n{res}";
    }

    private string ResolveSoapAction(string operation)
    {
        return _cfg[$"EInvoice:Edm:SoapActions:{operation}"] ?? $"{operation}Request";
    }

    private static void EnsureSoapFault(string xml)
    {
        if (!HasSoapFault(xml))
            return;
        var shortErr = GetXmlValue(xml, "ERROR_SHORT_DES");
        var longErr = GetXmlValue(xml, "ERROR_LONG_DES");
        if (string.IsNullOrWhiteSpace(shortErr))
            shortErr = GetXmlValue(xml, "faultstring");
        var msg = string.IsNullOrWhiteSpace(shortErr) ? "EDM SOAP fault." : shortErr;
        if (!string.IsNullOrWhiteSpace(longErr)) msg += " " + longErr;
        throw new InvalidOperationException(msg + "\n---SOAP-FAULT-XML---\n" + xml);
    }

    private static bool HasSoapFault(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return false;
        try
        {
            var x = XDocument.Parse(xml);
            return x.Descendants().Any(e => string.Equals(e.Name.LocalName, "Fault", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return xml.Contains(":Fault", StringComparison.OrdinalIgnoreCase) ||
                   xml.Contains("<Fault", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string BuildRequestHeaderXml(string sessionId, string? reason = null)
    {
        return $@"<REQUEST_HEADER xmlns="""">
    <SESSION_ID>{XmlEscape(sessionId)}</SESSION_ID>
    <CLIENT_TXN_ID>{Guid.NewGuid()}</CLIENT_TXN_ID>
    <ACTION_DATE>{DateTimeOffset.Now:O}</ACTION_DATE>
    <REASON>{XmlEscape(string.IsNullOrWhiteSpace(reason) ? "E-Fatura gönderim işlemi" : reason)}</REASON>
    <APPLICATION_NAME>KuyumcuPricee_Service</APPLICATION_NAME>
    <HOSTNAME>{XmlEscape(Environment.MachineName)}</HOSTNAME>
    <CHANNEL_NAME>API</CHANNEL_NAME>
    <COMPRESSED>N</COMPRESSED>
</REQUEST_HEADER>";
    }

    private static string NormalizeEdmStatus(string? edmStatus)
    {
        var s = (edmStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (s.Contains("SUCCEED")) return "Delivered";
        if (s.Contains("PROCESSING")) return "Sent";
        if (s.Contains("FAIL")) return "Failed";
        if (s.Contains("REJECT")) return "Rejected";
        return "Sent";
    }

    private static string NormalizeEdmMmStatus(string? edmStatus)
    {
        var s = (edmStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (s.Contains("SUCCEED") || s.Contains("DELIVER")) return "Delivered";
        if (s.Contains("PROCESSING") || s.Contains("SENT") || s.Contains("QUEUE")) return "Sent";
        if (s.Contains("CANCEL")) return "Cancelled";
        if (s.Contains("REJECT")) return "Rejected";
        if (s.Contains("FAIL") || s.Contains("ERROR")) return "Failed";
        return "Sent";
    }

    private static string NormalizeEdmErrorMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return "EDM servisine bağlanılamadı.";

        var msg = rawMessage.Trim();
        if (msg.Contains("503", StringComparison.OrdinalIgnoreCase))
            return "EDM test servisi geçici kapalı, sonra tekrar deneyin.";
        if (msg.Contains("oturum açtığınız firmanızın vergi kimlik numarası", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("uyuşmuyor", StringComparison.OrdinalIgnoreCase))
            return "EDM gönderim hatası: Entegratör kullanıcı hesabının VKN/TCKN bilgisi ile uygulamadaki gönderici vergi numarası farklı. E-Fatura ayarlarında Vergi Numarası alanını, EDM hesabınızın bağlı olduğu firmayla aynı yapın veya doğru EDM kullanıcı hesabıyla giriş yapın.";

        return msg;
    }

    private async Task<EdmSessionCompanyInfo?> TryGetSessionCompanyInfoAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var body = $@"<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
    <s:Body>
        <GetSessionInfoRequest xmlns=""http://tempuri.org/"">
            {BuildRequestHeaderXml(sessionId, "Oturum firma bilgisi sorgu")}
        </GetSessionInfoRequest>
    </s:Body>
</s:Envelope>";

            var xml = await SendSoapWithActionFallbackAsync("GetSessionInfo", body, ct);
            if (HasSoapFault(xml))
                return null;

            var name = GetXmlValue(xml, "COMPANY_NAME")
                       ?? GetXmlValue(xml, "CompanyName")
                       ?? GetXmlValue(xml, "TITLE")
                       ?? GetXmlValue(xml, "UNVAN");
            var address = GetXmlValue(xml, "ADDRESS")
                          ?? GetXmlValue(xml, "CompanyAddress");
            var taxOffice = GetXmlValue(xml, "TAX_OFFICE")
                            ?? GetXmlValue(xml, "TaxOffice")
                            ?? GetXmlValue(xml, "VERGI_DAIRESI");
            var email = GetXmlValue(xml, "EMAIL")
                        ?? GetXmlValue(xml, "Mail");

            if (string.IsNullOrWhiteSpace(name) &&
                string.IsNullOrWhiteSpace(address) &&
                string.IsNullOrWhiteSpace(taxOffice) &&
                string.IsNullOrWhiteSpace(email))
                return null;

            return new EdmSessionCompanyInfo(name, address, taxOffice, email);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGenericSenderName(string? senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName))
            return true;
        var value = senderName.Trim();
        return value.Equals("Firma", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Gönderici", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var normalized = value.Trim();
        return normalized == "-" || normalized.Equals("MERKEZ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadJson(string? json, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var name in names)
            {
                if (TryFindProperty(doc.RootElement, name, out var val))
                    return val;
            }
        }
        catch
        {
            // ignore parse errors
        }
        return null;
    }

    private static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().Replace(',', '.');
        if (decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private static string? ResolveCityPlateCode(string? cityOrCode)
    {
        if (string.IsNullOrWhiteSpace(cityOrCode))
            return null;

        var digits = new string(cityOrCode.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var numericCode) && numericCode is >= 1 and <= 81)
            return numericCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var key = NormalizeCityKey(cityOrCode);
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (CityPlateCodes.TryGetValue(key, out var code))
            return code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    private static string NormalizeCityKey(string input)
    {
        var text = input.Trim().ToUpperInvariant();
        text = text
            .Replace('Ç', 'C')
            .Replace('Ğ', 'G')
            .Replace('İ', 'I')
            .Replace('I', 'I')
            .Replace('Ö', 'O')
            .Replace('Ş', 'S')
            .Replace('Ü', 'U');
        var chars = text.Where(char.IsLetter).ToArray();
        return new string(chars);
    }

    private static readonly IReadOnlyDictionary<string, int> CityPlateCodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["ADANA"] = 1, ["ADIYAMAN"] = 2, ["AFYONKARAHISAR"] = 3, ["AGRI"] = 4, ["AMASYA"] = 5, ["ANKARA"] = 6, ["ANTALYA"] = 7, ["ARTVIN"] = 8, ["AYDIN"] = 9,
            ["BALIKESIR"] = 10, ["BILECIK"] = 11, ["BINGOL"] = 12, ["BITLIS"] = 13, ["BOLU"] = 14, ["BURDUR"] = 15, ["BURSA"] = 16, ["CANAKKALE"] = 17, ["CANKIRI"] = 18,
            ["CORUM"] = 19, ["DENIZLI"] = 20, ["DIYARBAKIR"] = 21, ["EDIRNE"] = 22, ["ELAZIG"] = 23, ["ERZINCAN"] = 24, ["ERZURUM"] = 25, ["ESKISEHIR"] = 26, ["GAZIANTEP"] = 27,
            ["GIRESUN"] = 28, ["GUMUSHANE"] = 29, ["HAKKARI"] = 30, ["YUKSEKOVA"] = 30, ["HATAY"] = 31, ["ISPARTA"] = 32, ["MERSIN"] = 33, ["ISTANBUL"] = 34,
            ["IZMIR"] = 35, ["KARS"] = 36, ["KASTAMONU"] = 37, ["KAYSERI"] = 38, ["KIRKLARELI"] = 39, ["KIRSEHIR"] = 40, ["KOCAELI"] = 41, ["KONYA"] = 42, ["KUTAHYA"] = 43,
            ["MALATYA"] = 44, ["MANISA"] = 45, ["KAHRAMANMARAS"] = 46, ["MARDIN"] = 47, ["MUGLA"] = 48, ["MUS"] = 49, ["NEVSEHIR"] = 50, ["NIGDE"] = 51, ["ORDU"] = 52,
            ["RIZE"] = 53, ["SAKARYA"] = 54, ["SAMSUN"] = 55, ["SIIRT"] = 56, ["SINOP"] = 57, ["SIVAS"] = 58, ["TEKIRDAG"] = 59, ["TOKAT"] = 60, ["TRABZON"] = 61,
            ["TUNCELI"] = 62, ["SANLIURFA"] = 63, ["USAK"] = 64, ["VAN"] = 65, ["YOZGAT"] = 66, ["ZONGULDAK"] = 67, ["AKSARAY"] = 68, ["BAYBURT"] = 69, ["KARAMAN"] = 70,
            ["KIRIKKALE"] = 71, ["BATMAN"] = 72, ["SIRNAK"] = 73, ["BARTIN"] = 74, ["ARDAHAN"] = 75, ["IGDIR"] = 76, ["YALOVA"] = 77, ["KARABUK"] = 78, ["KILIS"] = 79,
            ["OSMANIYE"] = 80, ["DUZCE"] = 81
        };

    private static string BuildExpenseSlipXml(
        string seri,
        string siraNo,
        DateTime date,
        string city,
        string senderVkn,
        string senderName,
        string senderAddress,
        string senderMail,
        string senderTaxOffice,
        string buyerName,
        string buyerTaxNo,
        string buyerAddress,
        string buyerMail,
        string buyerPlateCode,
        string lineMahiyet,
        string lineCinsi,
        string lineQuantity,
        string lineUnitPriceTextTr,
        string amountTextTr,
        string totalAmountTextInvariant)
    {
        return $@"<ExpenseSlip>
                <Id>0</Id>
                <Seri>{XmlEscape(seri)}</Seri>
                <SiraNo>{XmlEscape(siraNo)}</SiraNo>
                <Tarih>{date:yyyy-MM-dd}</Tarih>
                <Sehir>{XmlEscape(city)}</Sehir>
                <Gonderici>
                    <VKNorTCKN>{XmlEscape(senderVkn)}</VKNorTCKN>
                    <UnvanorAdSoyad>{XmlEscape(senderName)}</UnvanorAdSoyad>
                    <Adres>{XmlEscape(senderAddress)}</Adres>
                    <Mail>{XmlEscape(senderMail)}</Mail>
                    <VergiDairesi>{XmlEscape(senderTaxOffice)}</VergiDairesi>
                </Gonderici>
                <Alici>
                    <AdSoyad>{XmlEscape(buyerName)}</AdSoyad>
                    <TCKN>{XmlEscape(buyerTaxNo)}</TCKN>
                    <Adres>{XmlEscape(buyerAddress)}</Adres>
                    <PlakaNo>{XmlEscape(buyerPlateCode)}</PlakaNo>
                    <Mail>{XmlEscape(buyerMail)}</Mail>
                </Alici>
                <Satir>
                    <SatirBilgi>
                        <Mahiyet>{XmlEscape(lineMahiyet)}</Mahiyet>
                        <Cinsi>{XmlEscape(lineCinsi)}</Cinsi>
                        <Adet>{XmlEscape(lineQuantity)}</Adet>
                        <Fiyat>{lineUnitPriceTextTr}</Fiyat>
                        <Tutar>{amountTextTr}</Tutar>
                    </SatirBilgi>
                </Satir>
                <Toplam>
                    <Toplam>{totalAmountTextInvariant}</Toplam>
                    <GelirVergisiOran>0</GelirVergisiOran>
                    <GelirVergisiTutar>0</GelirVergisiTutar>
                    <DigerKesintiler>0</DigerKesintiler>
                    <KesintiToplami>0</KesintiToplami>
                    <KDV>0</KDV>
                    <ToolamOdenecekTutar>{totalAmountTextInvariant}</ToolamOdenecekTutar>
                </Toplam>
            </ExpenseSlip>";
    }

    private static string BuildExpenseSlipXmlUnqualifiedRootQualifiedChildren(
        string seri,
        string siraNo,
        DateTime date,
        string city,
        string senderVkn,
        string senderName,
        string senderAddress,
        string senderMail,
        string senderTaxOffice,
        string buyerName,
        string buyerTaxNo,
        string buyerAddress,
        string buyerMail,
        string buyerPlateCode,
        string lineMahiyet,
        string lineCinsi,
        string lineQuantity,
        string lineUnitPriceTextTr,
        string amountTextTr,
        string totalAmountTextInvariant)
    {
        return $@"<ExpenseSlip xmlns="""" xmlns:tns=""http://tempuri.org/"">
                <tns:Id>0</tns:Id>
                <tns:Seri>{XmlEscape(seri)}</tns:Seri>
                <tns:SiraNo>{XmlEscape(siraNo)}</tns:SiraNo>
                <tns:Tarih>{date:yyyy-MM-dd}</tns:Tarih>
                <tns:Sehir>{XmlEscape(city)}</tns:Sehir>
                <tns:Gonderici>
                    <tns:VKNorTCKN>{XmlEscape(senderVkn)}</tns:VKNorTCKN>
                    <tns:UnvanorAdSoyad>{XmlEscape(senderName)}</tns:UnvanorAdSoyad>
                    <tns:Adres>{XmlEscape(senderAddress)}</tns:Adres>
                    <tns:Mail>{XmlEscape(senderMail)}</tns:Mail>
                    <tns:VergiDairesi>{XmlEscape(senderTaxOffice)}</tns:VergiDairesi>
                </tns:Gonderici>
                <tns:Alici>
                    <tns:AdSoyad>{XmlEscape(buyerName)}</tns:AdSoyad>
                    <tns:TCKN>{XmlEscape(buyerTaxNo)}</tns:TCKN>
                    <tns:Adres>{XmlEscape(buyerAddress)}</tns:Adres>
                    <tns:PlakaNo>{XmlEscape(buyerPlateCode)}</tns:PlakaNo>
                    <tns:Mail>{XmlEscape(buyerMail)}</tns:Mail>
                </tns:Alici>
                <tns:Satir>
                    <tns:SatirBilgi>
                        <tns:Mahiyet>{XmlEscape(lineMahiyet)}</tns:Mahiyet>
                        <tns:Cinsi>{XmlEscape(lineCinsi)}</tns:Cinsi>
                        <tns:Adet>{XmlEscape(lineQuantity)}</tns:Adet>
                        <tns:Fiyat>{lineUnitPriceTextTr}</tns:Fiyat>
                        <tns:Tutar>{amountTextTr}</tns:Tutar>
                    </tns:SatirBilgi>
                </tns:Satir>
                <tns:Toplam>
                    <tns:Toplam>{totalAmountTextInvariant}</tns:Toplam>
                    <tns:GelirVergisiOran>0</tns:GelirVergisiOran>
                    <tns:GelirVergisiTutar>0</tns:GelirVergisiTutar>
                    <tns:DigerKesintiler>0</tns:DigerKesintiler>
                    <tns:KesintiToplami>0</tns:KesintiToplami>
                    <tns:KDV>0</tns:KDV>
                    <tns:ToolamOdenecekTutar>{totalAmountTextInvariant}</tns:ToolamOdenecekTutar>
                </tns:Toplam>
            </ExpenseSlip>";
    }

    private static bool TryFindProperty(System.Text.Json.JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    return !string.IsNullOrWhiteSpace(value);
                }
                if (TryFindProperty(p.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFindProperty(item, name, out value))
                    return true;
        }
        return false;
    }

    private static string? GetXmlValue(string xml, string localName)
    {
        var x = XDocument.Parse(xml);
        return x.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
    }

    private static bool IsSoapTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var normalized = value.Trim();
        return string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetInvoiceAttribute(string xml, string attr)
    {
        var x = XDocument.Parse(xml);
        var inv = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "INVOICE");
        if (inv is null) return null;
        return inv.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, attr, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string XmlEscape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return SecurityElement.Escape(text) ?? string.Empty;
    }

    private static string? NormalizeTaxNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length is 10 or 11)
            return digits;
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

public sealed record EdmTaxpayerQueryResult(
    bool IsSuccess,
    bool? IsEInvoiceTaxpayer,
    string? ReceiverAlias,
    string? RawResponse,
    string? Message
);

public sealed record EdmSessionCompanyInfo(
    string? CompanyName,
    string? Address,
    string? TaxOffice,
    string? Email
);

public sealed record EdmMmSendRequest(
    Guid TenantId,
    Guid BranchId,
    Guid DocumentId,
    string DocumentNo,
    string? SenderTaxNumber,
    string? SenderName,
    string? SenderAddress,
    string? SenderTaxOffice,
    string? SenderMail,
    string? SenderCity,
    string BuyerTaxNumber,
    string PayloadJson,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EdmMmSendResult(
    bool IsSuccess,
    string? IntegratorDocumentId,
    string? Uuid,
    string? RawResponse,
    string? ProviderStatus,
    string? ErrorMessage = null
);

public sealed record EdmMmStatusRequest(
    Guid TenantId,
    Guid BranchId,
    Guid DocumentId,
    string? DocumentNo,
    string? IntegratorDocumentId,
    string? Uuid,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EdmMmStatusResult(
    bool IsSuccess,
    string? ProviderStatus,
    string? RawResponse,
    string? ErrorMessage
);

public sealed record EdmMmCancelRequest(
    Guid TenantId,
    Guid BranchId,
    Guid DocumentId,
    string? IntegratorDocumentId,
    string? Uuid,
    string Reason,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EdmMmCancelResult(
    bool IsSuccess,
    string? ProviderStatus,
    string? RawResponse,
    string? ErrorMessage
);
