namespace kuyumcu_domain.Entities;

public class EInvoiceProfile : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string ProviderCode { get; set; } = "edm";
    public string CompanyName { get; set; } = "";
    public string CompanyAddress { get; set; } = "";
    public string TaxNumber { get; set; } = "";
    public string TaxOffice { get; set; } = "";
    public string? SenderLabel { get; set; }
    public string? IntegratorCompanyCode { get; set; }
    public string? IntegratorUsername { get; set; }
    public string? IntegratorSecretRef { get; set; }
    public string DefaultInvoicePrefix { get; set; } = "SAT";
    public string DefaultArchivePrefix { get; set; } = "ARS";
    public bool IsActive { get; set; } = true;

    public Branch Branch { get; set; } = null!;
}

public class EInvoiceDocument : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid? CustomerId { get; set; }
    public string Direction { get; set; } = "Outgoing";
    public string DocumentType { get; set; } = "EArsiv";
    public string Scenario { get; set; } = "TemelFatura";
    public string Status { get; set; } = "Draft";
    public string InvoiceNumber { get; set; } = "";
    public string Currency { get; set; } = "TRY";
    public decimal GrandTotal { get; set; }
    public string? IntegratorDocumentId { get; set; }
    public string? Uuid { get; set; }
    public string? Ettn { get; set; }
    public string? RawLastResponse { get; set; }
    public string? LastError { get; set; }
    public int RetryCount { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public Branch Branch { get; set; } = null!;
    public Invoice Invoice { get; set; } = null!;
    public Customer? Customer { get; set; }
}

public class EInvoiceOutbox : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid InvoiceId { get; set; }
    public string Operation { get; set; } = "Send";
    public string Status { get; set; } = "Pending";
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LockedAt { get; set; }
    public int RetryCount { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? LastError { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public class EInvoiceWebhookLog : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string ProviderCode { get; set; } = "";
    public string? Signature { get; set; }
    public string? EventId { get; set; }
    public string? EventType { get; set; }
    public string? IntegratorDocumentId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public bool IsVerified { get; set; }
    public bool IsProcessed { get; set; }
    public string? ProcessError { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
}
