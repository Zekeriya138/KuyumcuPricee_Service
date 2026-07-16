namespace kuyumcu_domain.Entities;

/// <summary>Planlanmış kart altındaki tek bir gelir/gider hareketi.</summary>
public class PlannedCashTransactionLine : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid PlannedCashTransactionId { get; set; }
    public string TxType { get; set; } = "Income";
    public string Currency { get; set; } = "TL";
    public string PaymentMethod { get; set; } = "Nakit";
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public Guid? CashTransactionId { get; set; }

    public PlannedCashTransaction PlannedCashTransaction { get; set; } = null!;
}
