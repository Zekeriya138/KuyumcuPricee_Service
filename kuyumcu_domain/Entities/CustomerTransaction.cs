namespace kuyumcu_domain.Entities;

/// <summary>Müşteri finans hareketi: doviz / ziynet / iscilikli.</summary>
public class CustomerTransaction : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid? BranchId { get; set; }

    /// <summary>DOVIZ, ZIYNET, ISCILIKLI</summary>
    public string GroupCode { get; set; } = "";
    /// <summary>Kalem adı: USD, CEYREK, ATA5, YUZUK vb.</summary>
    public string ItemName { get; set; } = "";
    /// <summary>Yeni/Eski vb.</summary>
    public string? ItemType { get; set; }

    /// <summary>Ziynet adedi veya döviz birim miktarı.</summary>
    public decimal Quantity { get; set; }
    public decimal? Gram { get; set; }
    public string? Ayar { get; set; }
    public decimal? Milyem { get; set; }
    public decimal? HasEquivalent { get; set; }
    public decimal? UnitPriceTl { get; set; }
    public decimal? TotalPriceTl { get; set; }

    /// <summary>+1 alacak, -1 borç.</summary>
    public int Direction { get; set; }
    public DateTime TxDate { get; set; } = DateTime.UtcNow;

    public string? CariDurum { get; set; } // Alacaklı / Borçlu
    public string? RefType { get; set; } // SALE / PURCHASE / MANUAL
    public Guid? RefId { get; set; }
    public string? Note { get; set; }

    /// <summary>İşlemi yapan kullanıcı (JWT'den).</summary>
    public Guid? UserId { get; set; }
    /// <summary>İşlem anındaki kullanıcı görünen adı (Ad Soyad ya da kullanıcı adı).</summary>
    public string? KullaniciAdi { get; set; }

    public Customer Customer { get; set; } = null!;
}
