namespace kuyumcu_domain.Entities;

/// <summary>Tedarikçi cari bakiyeleri (5 birim): TL, USD, EUR, HAS, GÜMÜŞ.</summary>
public class SupplierBalance : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid SupplierId { get; set; }

    /// <summary>İşletmenin tedarikçiden alacağı (+) / tedarikçiye borcu (-), TL.</summary>
    public decimal BalanceTL { get; set; }
    /// <summary>USD bakiyesi (+ alacak / - borç).</summary>
    public decimal BalanceUSD { get; set; }
    /// <summary>EUR bakiyesi (+ alacak / - borç).</summary>
    public decimal BalanceEUR { get; set; }
    /// <summary>GBP bakiyesi (+ alacak / - borç).</summary>
    public decimal BalanceGBP { get; set; }
    /// <summary>Has altın (gram) bakiyesi (+ alacak / - borç).</summary>
    public decimal BalanceHAS { get; set; }
    /// <summary>Gümüş (gram) bakiyesi (+ alacak / - borç).</summary>
    public decimal BalanceGUMUS { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public Supplier Supplier { get; set; } = null!;
}
