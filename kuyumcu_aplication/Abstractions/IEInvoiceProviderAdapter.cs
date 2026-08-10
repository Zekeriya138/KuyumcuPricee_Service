namespace kuyumcu_application.Abstractions;

public interface IEInvoiceProviderAdapter
{
    string ProviderCode { get; }

    Task<EInvoiceConnectionTestResult> TestConnectionAsync(EInvoiceConnectionTestRequest request, CancellationToken ct);
    Task<EInvoiceSendResult> SendOutgoingAsync(EInvoiceSendRequest request, CancellationToken ct);
    Task<EInvoiceStatusResult> GetStatusAsync(EInvoiceStatusRequest request, CancellationToken ct);
    Task<EInvoiceCancelResult> CancelAsync(EInvoiceCancelRequest request, CancellationToken ct);
    Task<EInvoiceWebhookVerificationResult> VerifyWebhookAsync(EInvoiceWebhookVerificationRequest request, CancellationToken ct);

    /// <summary>
    /// VKN/TCKN mükellef sorgusu. Desteklemeyen sağlayıcılar varsayılan olarak hata döner.
    /// </summary>
    Task<IntegratorTaxpayerQueryResult> QueryTaxpayerAsync(string? username, string? password, string taxNumber, CancellationToken ct)
        => Task.FromResult(new IntegratorTaxpayerQueryResult(false, null, null, null, null, "Bu sağlayıcı mükellef sorgusunu desteklemiyor."));

    /// <summary>
    /// Ünvan / ad soyad ile mükellef araması. TCKN/VKN bilinmediğinde kullanılır.
    /// Desteklemeyen sağlayıcılar varsayılan olarak hata döner.
    /// </summary>
    Task<IntegratorTaxpayerSearchResult> SearchTaxpayersByTitleAsync(
        string? username,
        string? password,
        string title,
        CancellationToken ct)
        => Task.FromResult(IntegratorTaxpayerSearchResult.Fail("Bu sağlayıcı ünvan ile mükellef aramasını desteklemiyor. TCKN/VKN girin."));

    /// <summary>GİB mükellef listesi önbelleğini arka planda ısıtır (EDM ünvan araması için).</summary>
    Task WarmTaxpayerSearchCacheAsync(string? username, string? password, CancellationToken ct)
        => Task.CompletedTask;

    /// <summary>
    /// Entegratör portalındaki son seri numarasını sorgular (Uyumsoft gibi).
    /// </summary>
    Task<IntegratorSeriesCounterResult> QuerySeriesCounterAsync(
        string? username,
        string? password,
        string prefix,
        int year,
        bool isEArchive,
        CancellationToken ct)
        => Task.FromResult(new IntegratorSeriesCounterResult(false, null, null, null, "Bu sağlayıcı seri sayacı sorgusunu desteklemiyor."));

    /// <summary>
    /// Entegratörün gelen kutusundaki (gelen) e-Faturaları döner.
    /// Varsayılan olarak desteklenmez; yalnızca destekleyen sağlayıcılar (ör. EDM) override eder.
    /// </summary>
    Task<EInvoiceIncomingResult> GetIncomingInvoicesAsync(EInvoiceIncomingRequest request, CancellationToken ct)
        => Task.FromResult(new EInvoiceIncomingResult(false, new List<EInvoiceIncomingItem>(), null, "Bu sağlayıcı gelen fatura sorgusunu desteklemiyor."));
}

public sealed record EInvoiceIncomingRequest(
    Guid TenantId,
    Guid BranchId,
    DateTime StartDateUtc,
    DateTime EndDateUtc,
    int Limit,
    string? IntegratorUsername = null,
    string? IntegratorPassword = null
);

public sealed record EInvoiceIncomingItem(
    string Uuid,
    string InvoiceNumber,
    string SenderName,
    string SenderTaxNumber,
    string DocumentType,
    string Status,
    string StatusDescription,
    decimal PayableAmount,
    string Currency,
    DateTime? IssueDate,
    string? EnvelopeIdentifier,
    string? RawContent,
    DateTime? CreatedAt = null,
    string? ReceiverName = null,
    string? ReceiverTaxNumber = null,
    string? GibStatusDescription = null,
    string? ProfileId = null
);

public sealed record EInvoiceIncomingResult(
    bool IsSuccess,
    IReadOnlyList<EInvoiceIncomingItem> Items,
    string? RawResponse,
    string? ErrorMessage
);

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

public sealed record IntegratorTaxpayerQueryResult(
    bool IsSuccess,
    bool? IsEInvoiceTaxpayer,
    string? Title,
    string? ReceiverAlias,
    string? RawResponse,
    string? Message
);

public sealed record IntegratorTaxpayerCandidate(
    string TaxNo,
    string? Title,
    string? ReceiverAlias,
    bool IsEInvoiceTaxpayer
);

public sealed record IntegratorTaxpayerSearchResult(
    bool IsSuccess,
    IReadOnlyList<IntegratorTaxpayerCandidate> Candidates,
    string? RawResponse,
    string? Message
)
{
    public static IntegratorTaxpayerSearchResult Fail(string message)
        => new(false, Array.Empty<IntegratorTaxpayerCandidate>(), null, message);

    public static IntegratorTaxpayerSearchResult Ok(
        IReadOnlyList<IntegratorTaxpayerCandidate> candidates,
        string? rawResponse,
        string message)
        => new(true, candidates, rawResponse, message);
}

public sealed record IntegratorSeriesCounterResult(
    bool IsSuccess,
    int? LastSerial,
    string? NextInvoiceNumber,
    string? RawResponse,
    string? ErrorMessage
);
