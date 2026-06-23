namespace kuyumcu_domain.Entities;

public class ExpenseSlipDocument : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? SourceSaleId { get; set; }
    public string DocumentNo { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public string Currency { get; set; } = "TRY";
    public decimal GrandTotal { get; set; }
    public string BuyerName { get; set; } = "";
    public string BuyerTaxNumber { get; set; } = "";
    public string? Description { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? RawLastResponse { get; set; }
    public string? IntegratorDocumentId { get; set; }
    public string? Uuid { get; set; }
    public string? LastError { get; set; }
    public int RetryCount { get; set; }
    public DateTime? SubmittedAt { get; set; }

    public Branch Branch { get; set; } = null!;
}

public class ExpenseSlipAuditLog : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid DocumentId { get; set; }
    public string Action { get; set; } = "";
    public string? StatusBefore { get; set; }
    public string? StatusAfter { get; set; }
    public bool IsSuccess { get; set; }
    public string? RequestJson { get; set; }
    public string? ResponseRaw { get; set; }
    public string? ErrorMessage { get; set; }

    public Branch Branch { get; set; } = null!;
    public ExpenseSlipDocument Document { get; set; } = null!;
}
