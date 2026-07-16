namespace kuyumcu_domain.Entities;

/// <summary>Planlanmış gelir/gider kartı — kayıt kasaya yansır.</summary>
public class PlannedCashTransaction : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Income / Expense</summary>
    public string TxType { get; set; } = "Income";
    public string Currency { get; set; } = "TL";
    public string PaymentMethod { get; set; } = "Nakit";
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public Guid? CashTransactionId { get; set; }
}
