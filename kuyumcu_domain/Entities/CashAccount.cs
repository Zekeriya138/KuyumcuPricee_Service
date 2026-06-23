namespace kuyumcu_domain.Entities;

/// <summary>Şube bazlı kasa/vault/pos hesap bakiyesi.</summary>
public class CashAccount : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>Kasa, Vault, PosBanka, Emanet vb.</summary>
    public string AccountType { get; set; } = "Kasa";
    /// <summary>TL, USD, EUR, HAS</summary>
    public string Currency { get; set; } = "TL";
    public string Name { get; set; } = "";
    public decimal CurrentBalance { get; set; }
}

