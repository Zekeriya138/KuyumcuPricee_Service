namespace kuyumcu_application.Abstractions;

public interface IEInvoiceProviderAdapter
{
    string ProviderCode { get; }

    Task<EInvoiceConnectionTestResult> TestConnectionAsync(EInvoiceConnectionTestRequest request, CancellationToken ct);
    Task<EInvoiceSendResult> SendOutgoingAsync(EInvoiceSendRequest request, CancellationToken ct);
    Task<EInvoiceStatusResult> GetStatusAsync(EInvoiceStatusRequest request, CancellationToken ct);
    Task<EInvoiceCancelResult> CancelAsync(EInvoiceCancelRequest request, CancellationToken ct);
    Task<EInvoiceWebhookVerificationResult> VerifyWebhookAsync(EInvoiceWebhookVerificationRequest request, CancellationToken ct);
}

public sealed record EInvoiceSendRequest(
    Guid TenantId,
    Guid BranchId,
    Guid DocumentId,
    string DocumentType,
    string InvoiceNumber,
    DateTime InvoiceDateUtc,
    decimal GrandTotal,
    string Currency,
    string BuyerName,
    string BuyerTaxNumber,
    string PayloadJson,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EInvoiceConnectionTestRequest(
    Guid TenantId,
    Guid BranchId,
    string ProviderCode,
    string? IntegratorUsername,
    string? IntegratorPassword,
    string TaxNumber,
    string TaxOffice,
    string CompanyAddress
);

public sealed record EInvoiceConnectionTestResult(
    bool IsSuccess,
    string Message,
    string? RawResponse = null
);

public sealed record EInvoiceSendResult(
    bool IsSuccess,
    string? IntegratorDocumentId,
    string? Uuid,
    string? Ettn,
    string? ProviderStatus,
    string? RawResponse,
    string? ErrorMessage
);

public sealed record EInvoiceStatusRequest(
    Guid TenantId,
    Guid BranchId,
    Guid DocumentId,
    string IntegratorDocumentId,
    string? Uuid,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EInvoiceStatusResult(
    bool IsSuccess,
    string? ProviderStatus,
    DateTime? StatusAtUtc,
    string? RawResponse,
    string? ErrorMessage
);

public sealed record EInvoiceCancelRequest(
    Guid TenantId,
    Guid BranchId,
    Guid DocumentId,
    string IntegratorDocumentId,
    string Reason,
    string? Uuid = null,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EInvoiceCancelResult(
    bool IsSuccess,
    string? ProviderStatus,
    string? RawResponse,
    string? ErrorMessage
);

public sealed record EInvoiceWebhookVerificationRequest(
    string ProviderCode,
    string SignatureHeader,
    string Payload,
    IReadOnlyDictionary<string, string> Headers
);

public sealed record EInvoiceWebhookVerificationResult(
    bool IsValid,
    string? EventId,
    string? EventType,
    string? DocumentId,
    string? ProviderStatus,
    string? ErrorMessage
);
