namespace kuyumcu_domain.Entities;

/// <summary>Tedarikçi işlem hareketi (Ödeme/Tahsilat) + dönüşüm kuru bilgisi.</summary>
public class SupplierTransaction : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? BranchId { get; set; }

    /// <summary>PAYMENT / COLLECTION</summary>
    public string TxType { get; set; } = "";

    /// <summary>Ödemenin fiziksel girdi birimi: TL/USD/EUR/HAS/GUMUS</summary>
    public string SourceUnit { get; set; } = "";
    public decimal SourceAmount { get; set; }

    /// <summary>Bakiyenin düşeceği/hedef birim: TL/USD/EUR/HAS/GUMUS</summary>
    public string TargetUnit { get; set; } = "";
    public decimal TargetAmount { get; set; }

    public bool IsConverted { get; set; }
    public decimal SourceUnitTlRate { get; set; }
    public decimal TargetUnitTlRate { get; set; }

    public string? Description { get; set; }
    public DateTime TxDate { get; set; } = DateTime.UtcNow;

    /// <summary>İşlemi yapan kullanıcı (JWT'den).</summary>
    public Guid? UserId { get; set; }
    /// <summary>İşlem anındaki kullanıcı görünen adı (Ad Soyad ya da kullanıcı adı).</summary>
    public string? KullaniciAdi { get; set; }

    public Supplier Supplier { get; set; } = null!;
}
