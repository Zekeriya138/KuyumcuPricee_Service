using System;

namespace kuyumcu_domain.Entities
{
    /// <summary>
    /// Mağaza kataloğu ürünü (bilezik, yüzük, kolye, vs.)
    /// Not: Şube bağımsız tutuluyor; şube-bazlı adet/gram takibini bir sonraki adımda "Stock" ile yapacağız.
    /// </summary>
    public sealed class Product : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public string ProductCode { get; set; } = ""; // benzersiz stok kodu / SKU
        public string Name { get; set; } = "";        // görünen ad
        public string? Category { get; set; }         // "Bilezik", "Yüzük", "Kolye"...
        public string? Karat { get; set; }            // "14K", "22K", "24K" ...
        public decimal? WeightGr { get; set; }        // varsayılan gramaj (opsiyonel)
        public string? Barcode { get; set; }          // barkod (opsiyonel)
    }
}
