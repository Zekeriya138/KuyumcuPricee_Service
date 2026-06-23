using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace kuyumcu_infrastructure.Services;

public sealed class UyumsoftEInvoiceProviderAdapter : IEInvoiceProviderAdapter
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;

    public UyumsoftEInvoiceProviderAdapter(HttpClient http, IConfiguration cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public string ProviderCode => "uyumsoft";

    public async Task<EInvoiceConnectionTestResult> TestConnectionAsync(EInvoiceConnectionTestRequest request, CancellationToken ct)
    {
        var endpoint = (_cfg["EInvoice:Uyumsoft:TestEndpoint"] ?? "api/einvoice/test-connection").TrimStart('/');
        using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ApplyAuthHeaders(msg, request.IntegratorUsername, request.IntegratorPassword);

        var payload = JsonSerializer.Serialize(new
        {
            taxNumber = request.TaxNumber,
            taxOffice = request.TaxOffice,
            companyAddress = request.CompanyAddress
        });
        msg.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return new EInvoiceConnectionTestResult(false, $"Uyumsoft test bağlantısı başarısız: {(int)res.StatusCode}", body);

        return new EInvoiceConnectionTestResult(true, "Uyumsoft bağlantısı başarılı.", body);
    }

    public async Task<EInvoiceSendResult> SendOutgoingAsync(EInvoiceSendRequest request, CancellationToken ct)
    {
        var endpoint = (_cfg["EInvoice:Uyumsoft:SendEndpoint"] ?? "api/einvoice/send").TrimStart('/');
        using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ApplyAuthHeaders(msg, request.IntegratorUsername, request.IntegratorPassword);
        msg.Content = new StringContent(request.PayloadJson, Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            return new EInvoiceSendResult(
                false, null, null, null, null, body,
                $"Uyumsoft gönderim hatası: {(int)res.StatusCode} {res.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        return new EInvoiceSendResult(
            IsSuccess: true,
            IntegratorDocumentId: TryGet(root, "documentId", "integratorDocumentId", "id"),
            Uuid: TryGet(root, "uuid"),
            Ettn: TryGet(root, "ettn"),
            ProviderStatus: TryGet(root, "status") ?? "Sent",
            RawResponse: body,
            ErrorMessage: null);
    }

    public async Task<EInvoiceStatusResult> GetStatusAsync(EInvoiceStatusRequest request, CancellationToken ct)
    {
        var template = _cfg["EInvoice:Uyumsoft:StatusEndpoint"] ?? "api/einvoice/status/{documentId}";
        var endpoint = template.Replace("{documentId}", Uri.EscapeDataString(request.IntegratorDocumentId)).TrimStart('/');

        using var msg = new HttpRequestMessage(HttpMethod.Get, endpoint);
        ApplyAuthHeaders(msg, request.IntegratorUsername, request.IntegratorPassword);
        var res = await _http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            return new EInvoiceStatusResult(false, null, null, body,
                $"Uyumsoft durum sorgu hatası: {(int)res.StatusCode} {res.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        return new EInvoiceStatusResult(
            true,
            TryGet(root, "status"),
            DateTime.UtcNow,
            body,
            null);
    }

    public async Task<EInvoiceCancelResult> CancelAsync(EInvoiceCancelRequest request, CancellationToken ct)
    {
        var endpoint = (_cfg["EInvoice:Uyumsoft:CancelEndpoint"] ?? "api/einvoice/cancel").TrimStart('/');
        using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ApplyAuthHeaders(msg, null, null);
        msg.Content = new StringContent(JsonSerializer.Serialize(new
        {
            documentId = request.IntegratorDocumentId,
            reason = request.Reason
        }), Encoding.UTF8, "application/json");

        var res = await _http.SendAsync(msg, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            return new EInvoiceCancelResult(false, null, body,
                $"Uyumsoft iptal hatası: {(int)res.StatusCode} {res.ReasonPhrase}");
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = doc.RootElement;
        return new EInvoiceCancelResult(
            true,
            TryGet(root, "status") ?? "Cancelled",
            body,
            null);
    }

    public Task<EInvoiceWebhookVerificationResult> VerifyWebhookAsync(EInvoiceWebhookVerificationRequest request, CancellationToken ct)
    {
        var secret = _cfg["EInvoice:WebhookSecret"] ?? "";
        var signature = request.SignatureHeader ?? "";
        var expected = HmacHex(request.Payload, secret);
        if (!string.IsNullOrWhiteSpace(secret) && !string.Equals(signature, expected, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new EInvoiceWebhookVerificationResult(false, null, null, null, null, "Invalid webhook signature."));
        }

        try
        {
            using var doc = JsonDocument.Parse(request.Payload);
            var root = doc.RootElement;
            return Task.FromResult(new EInvoiceWebhookVerificationResult(
                true,
                TryGet(root, "eventId", "id"),
                TryGet(root, "eventType", "type"),
                TryGet(root, "documentId", "integratorDocumentId"),
                TryGet(root, "status"),
                null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EInvoiceWebhookVerificationResult(false, null, null, null, null, ex.Message));
        }
    }

    private void ApplyAuthHeaders(HttpRequestMessage msg, string? username, string? password)
    {
        var user = string.IsNullOrWhiteSpace(username) ? _cfg["EInvoice:Uyumsoft:Username"] : username;
        var pass = string.IsNullOrWhiteSpace(password) ? _cfg["EInvoice:Uyumsoft:Password"] : password;
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
            msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var apiKey = _cfg["EInvoice:Uyumsoft:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
            msg.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
    }

    private static string? TryGet(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static string HmacHex(string payload, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return string.Empty;
        using var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
