using kuyumcu_domain.Enums;

namespace kuyumcu_domain.Entities;

/// <summary>Hurda hareket defteri (denetim / rapor).</summary>
public class ScrapLedger : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public ScrapLedgerKind Kind { get; set; }

    public string Karat { get; set; } = "";

    /// <summary>Pozitif: giriş, negatif: çıkış.</summary>
    public decimal DeltaWeightGram { get; set; }

    public decimal DeltaPureGoldGram { get; set; }

    public decimal? GoldPricePerGram { get; set; }
    public decimal? AmountTl { get; set; }

    public Guid? CustomerId { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? PurchaseId { get; set; }
    public Guid? ProductId { get; set; }

    public string? Note { get; set; }
}
