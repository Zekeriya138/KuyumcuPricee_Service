using System;

namespace kuyumcu_domain.Entities
{
    public class SaleItem : Entity
    {
        public Guid TenantId { get; set; }
        public Guid SaleId { get; set; }
        public int LineNo { get; set; }

        public ItemKind Kind { get; set; } = ItemKind.Unknown;
        public string ProductCode { get; set; } = "";   // stok/seri kodu (opsiyonel kullanım)
        public string ProductName { get; set; } = "";   // kullanıcıya özel ad
        public string Karat { get; set; } = "";         // 14K/22K/24K...
        public string? Category { get; set; }           // bilezik, yüzük, kolye...

        // Miktar & fiyatlar
        public decimal Quantity { get; set; }           // gram/adet
        /// <summary>Kısmi teslimde kasadan/stoktan düşülen miktar. Boş ise tamamı teslim edilir.</summary>
        public decimal? DeliveredQuantity { get; set; }
        public decimal UnitPrice { get; set; }          // TL
        public decimal Discount { get; set; }           // satır indirimi TL
        public decimal TaxRate { get; set; }            // % (ör. 0,00/0,10)
        public decimal LineTotal { get; set; }          // (UnitPrice*Quantity - Discount) * (1+TaxRate)

        // YENİ: Tekil parça takibi
        public Guid? ProductItemId { get; set; }

        // Navigations
        public Sale Sale { get; set; } = null!;
        public ProductItem? ProductItem { get; set; } // << YENİ Navigasyon
    }
    //public class SaleItem : Entity
    //{
    //    public Guid TenantId { get; set; }
    //    public Guid SaleId { get; set; }
    //    public int LineNo { get; set; }

    //    public ItemKind Kind { get; set; } = ItemKind.Unknown;
    //    public string ProductCode { get; set; } = "";   // stok/seri kodu (opsiyonel kullanım)
    //    public string ProductName { get; set; } = "";   // kullanıcıya özel ad
    //    public string Karat { get; set; } = "";         // 14K/22K/24K...
    //    public string? Category { get; set; }           // bilezik, yüzük, kolye...

    //    // Miktar & fiyatlar
    //    public decimal Quantity { get; set; }           // gram/adet
    //    public decimal UnitPrice { get; set; }          // TL
    //    public decimal Discount { get; set; }           // satır indirimi TL
    //    public decimal TaxRate { get; set; }            // % (ör. 0,00/0,10)
    //    public decimal LineTotal { get; set; }          // (UnitPrice*Quantity - Discount) * (1+TaxRate)

    //    // Navigations
    //    public Sale Sale { get; set; } = null!;
    //}
}
