namespace kuyumcu_domain.Entities;

/// <summary>Satış ödeme dağılımı ve kasa/vault gelir hareketi kaydı.</summary>
public class SalePayment : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid SaleId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>Nakit, Kart, Veresiye, IBAN, Takas, USD, Euro</summary>
    public string Method { get; set; } = "";
    /// <summary>TL, USD, EUR</summary>
    public string Currency { get; set; } = "TL";
    public decimal Amount { get; set; }
    /// <summary>Gelir / Gider</summary>
    public string Direction { get; set; } = "Gelir";
    /// <summary>Kasa, PosBanka, Vault, Veresiye, Takas</summary>
    public string LedgerType { get; set; } = "";
    public string? Account { get; set; }
    public string? Note { get; set; }

    public Sale Sale { get; set; } = null!;
}
