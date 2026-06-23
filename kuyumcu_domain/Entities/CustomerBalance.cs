namespace kuyumcu_domain.Entities;

/// <summary>Müşteri cari özet bakiyesi (3 katman: döviz + has).</summary>
public class CustomerBalance : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }

    /// <summary>TL cari net (+ alacak / - borç).</summary>
    public decimal BalanceTL { get; set; }
    /// <summary>USD cari net (+ alacak / - borç).</summary>
    public decimal BalanceUSD { get; set; }
    /// <summary>EUR cari net (+ alacak / - borç).</summary>
    public decimal BalanceEUR { get; set; }
    /// <summary>GBP cari net (+ alacak / - borç).</summary>
    public decimal BalanceGBP { get; set; }
    /// <summary>HAS (gram) cari net (+ alacak / - borç).</summary>
    public decimal BalanceHAS { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Customer Customer { get; set; } = null!;
}
