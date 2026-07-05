using System;
using kuyumcu_domain.Enums;

namespace kuyumcu_domain.Entities
{
    /// <summary>
    /// Mağaza kataloğu ürünü (bilezik, yüzük, kolye, vs.)
    /// Tekil = parça bazlı (ProductItem); Ziynet = adetli stok (Çeyrek, Gram Altın vb.).
    /// </summary>
    public sealed class Product : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        /// <summary>Ürün kartının ait olduğu şube (aynı kiracıda şube bazlı katalog).</summary>
        public Guid BranchId { get; set; }
        public string ProductCode { get; set; } = ""; // benzersiz stok kodu / SKU
        public string Name { get; set; } = "";        // görünen ad
        public string? Category { get; set; }         // "Bilezik", "Yüzük", "Kolye"...
        /// <summary>Toptancı hammadde satırı (Stok-Depo): mal tanımı; barkodlu gram eşlemesi için.</summary>
        public string? MalTanim { get; set; }
        /// <summary>Seçilen depo satırındaki tedarikçi unvanı (barkodlu gram hangi satıra yazılacak).</summary>
        public string? DepoTedarikciFirma { get; set; }
        /// <summary>Stok-Depo hammadde satırındaki birim işçilik (has); aynı mal+tedarikçi+ayar için satırları ayırt eder.</summary>
        public decimal? DepoBirimMaliyet { get; set; }
        public string? Karat { get; set; }            // "14K", "22K", "24K" ...
        public decimal? WeightGr { get; set; }        // varsayılan gramaj (opsiyonel)
        public decimal? Cost { get; set; }             // maliyet (DB'de NULL olabilir)
        /// <summary>Tekil ürün barkodlanırken belirlenen satış fiyatı (has gr).</summary>
        public decimal? BelirlenenSatisFiyatiHas { get; set; }

        /// <summary>Tekil: birim satış işçilik (has/gr); ürün ekleme formundaki birim satış işçilik alanı.</summary>
        public decimal? BirimSatisIscilikHas { get; set; }
        public string? Barcode { get; set; }          // barkod (opsiyonel)
        /// <summary>Fiziksel ölçü: yüzük ölçüsü, bilezik çapı vb. (esnek metin). Ziynet ürünlerde genelde kullanılmaz.</summary>
        public string? Olcu { get; set; }

        /// <summary>Tekil = parça bazlı (barkodlu), Ziynet = adetli sarrafiye ürünü. Null ise Tekil kabul edilir.</summary>
        public InventoryType? InventoryType { get; set; }

        /// <summary>Ziynet ürünler için stok adedi. Tekil ürünlerde kullanılmaz. Null ise 0 kabul edilir.</summary>
        public int? StokMiktari { get; set; }

        /// <summary>Ziynet tipi: Çeyrek, Yarım, Tam, Gram Altın vb. Fiyat eşlemesi için kullanılır.</summary>
        public string? ZiynetTipi { get; set; }

        /// <summary>Saat vb. özel ürün işareti. Bu ürünler satışta parça/adet bazında yönetilir.</summary>
        public bool IsSpecialProduct { get; set; }

        /// <summary>Ürün fotoğrafı (opsiyonel, tek görsel). JPEG baytları olarak saklanır.</summary>
        public byte[]? Image { get; set; }
    }
}
