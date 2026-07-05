using System;

namespace kuyumcu_domain.Entities;

/// <summary>
/// Ürün sayımı (envanter/stok sayım) oturumu. Barkodlanan/etiketlenen ürünlerin fiziksel sayımı için;
/// hem masaüstü (kamera/okuyucu) hem mobil uygulama tarafından kullanılır. Şube + kiracı bazlıdır.
/// </summary>
public sealed class StockCountSession : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Open, Completed, Cancelled.</summary>
    public string Status { get; set; } = "Open";
    /// <summary>Sayım başlangıcında/anlık beklenen (barkodlu ürün) sayısı.</summary>
    public int ExpectedCount { get; set; }
    public int MatchedCount { get; set; }
    public int UnknownCount { get; set; }
    public int MissingCount { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Sayım oturumunda okutulan tek bir barkod kaydı.</summary>
public sealed class StockCountScan : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public string Barcode { get; set; } = "";
    public Guid? ProductId { get; set; }
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    /// <summary>Matched (ilk eşleşme), Unknown (sistemde yok), Duplicate (bu oturumda tekrar okutuldu).</summary>
    public string MatchStatus { get; set; } = "Unknown";
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
    public Guid? ScannedByUserId { get; set; }
    public string? ScannedByName { get; set; }
}
