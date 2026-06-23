using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using kuyumcu_application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace kuyumcu_infrastructure.Services;

public sealed class StubEInvoiceProviderAdapter : IEInvoiceProviderAdapter
{
    private readonly IConfiguration _cfg;

    public StubEInvoiceProviderAdapter(IConfiguration cfg)
    {
        _cfg = cfg;
    }

    public string ProviderCode => "stub-integrator";

    public Task<EInvoiceConnectionTestResult> TestConnectionAsync(EInvoiceConnectionTestRequest request, CancellationToken ct)
    {
        return Task.FromResult(new EInvoiceConnectionTestResult(
            IsSuccess: true,
            Message: "Stub entegratör bağlantısı başarılı.",
            RawResponse: "{\"provider\":\"stub-integrator\",\"ok\":true}"
        ));
    }

    public Task<EInvoiceSendResult> SendOutgoingAsync(EInvoiceSendRequest request, CancellationToken ct)
    {
        var seed = $"{request.TenantId:N}-{request.BranchId:N}-{request.DocumentId:N}-{request.InvoiceNumber}";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        var docId = $"STUB-{digest[..16]}";
        var uuid = $"{digest[..8]}-{digest[8..12]}-{digest[12..16]}-{digest[16..20]}-{digest[20..32]}";
        var ettn = $"{digest[32..40]}-{digest[40..44]}-{digest[44..48]}-{digest[48..52]}-{digest[52..64]}";

        var raw = JsonSerializer.Serialize(new
        {
            provider = ProviderCode,
            acceptedAtUtc = DateTime.UtcNow,
            documentId = docId,
            uuid,
            ettn,
            simulatedStatus = "Sent"
        });

        return Task.FromResult(new EInvoiceSendResult(
            IsSuccess: true,
            IntegratorDocumentId: docId,
            Uuid: uuid,
            Ettn: ettn,
            ProviderStatus: "Sent",
            RawResponse: raw,
            ErrorMessage: null
        ));
    }

    public Task<EInvoiceStatusResult> GetStatusAsync(EInvoiceStatusRequest request, CancellationToken ct)
    {
        var raw = JsonSerializer.Serialize(new
        {
            provider = ProviderCode,
            request.IntegratorDocumentId,
            status = "Delivered",
            statusAtUtc = DateTime.UtcNow
        });

        return Task.FromResult(new EInvoiceStatusResult(
            IsSuccess: true,
            ProviderStatus: "Delivered",
            StatusAtUtc: DateTime.UtcNow,
            RawResponse: raw,
            ErrorMessage: null
        ));
    }

    public Task<EInvoiceCancelResult> CancelAsync(EInvoiceCancelRequest request, CancellationToken ct)
    {
        var raw = JsonSerializer.Serialize(new
        {
            provider = ProviderCode,
            request.IntegratorDocumentId,
            request.Reason,
            cancelledAtUtc = DateTime.UtcNow
        });

        return Task.FromResult(new EInvoiceCancelResult(
            IsSuccess: true,
            ProviderStatus: "Cancelled",
            RawResponse: raw,
            ErrorMessage: null
        ));
    }

    public Task<EInvoiceWebhookVerificationResult> VerifyWebhookAsync(EInvoiceWebhookVerificationRequest request, CancellationToken ct)
    {
        var secret = _cfg["EInvoice:WebhookSecret"] ?? "stub-secret";
        var expected = ComputeHmac(request.Payload, secret);
        var isValid = string.Equals(expected, request.SignatureHeader ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        if (!isValid)
        {
            return Task.FromResult(new EInvoiceWebhookVerificationResult(
                IsValid: false,
                EventId: null,
                EventType: null,
                DocumentId: null,
                ProviderStatus: null,
                ErrorMessage: "Invalid webhook signature."
            ));
        }

        try
        {
            using var doc = JsonDocument.Parse(request.Payload);
            var root = doc.RootElement;
            return Task.FromResult(new EInvoiceWebhookVerificationResult(
                IsValid: true,
                EventId: root.TryGetProperty("eventId", out var ev) ? ev.GetString() : null,
                EventType: root.TryGetProperty("eventType", out var et) ? et.GetString() : null,
                DocumentId: root.TryGetProperty("documentId", out var did) ? did.GetString() : null,
                ProviderStatus: root.TryGetProperty("status", out var st) ? st.GetString() : null,
                ErrorMessage: null
            ));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new EInvoiceWebhookVerificationResult(
                IsValid: false,
                EventId: null,
                EventType: null,
                DocumentId: null,
                ProviderStatus: null,
                ErrorMessage: $"Webhook payload parse error: {ex.Message}"
            ));
        }
    }

    private static string ComputeHmac(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
