namespace kuyumcu_domain.Entities;

/// <summary>
/// Müşteri hurdası birikimi (şube + ayar bazında). ProductItem’dan ayrı tutulur.
/// </summary>
public class ScrapStock : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>Normalize ayar kodu: 14K, 18K, 22K, 24K.</summary>
    public string Karat { get; set; } = "22K";

    /// <summary>Toplam hurda ağırlığı (gram).</summary>
    public decimal WeightGram { get; set; }

    /// <summary>Toplam has altın (gram) — WeightGram × (ayar/24).</summary>
    public decimal PureGoldGram { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
