using System;

namespace kuyumcu_domain.Entities
{
    /// <summary>
    /// Ürün kategorisi (Yüzük, Kolye, Bilezik vb.). KategoriKodu ile akıllı ürün kodu üretiminde kullanılır.
    /// </summary>
    public sealed class Category : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        /// <summary>Görünen ad (örn: Yüzük, Kolye)</summary>
        public string Name { get; set; } = "";
        /// <summary>Kod öneki (örn: YZK, KLY) — ürün kodu [KategoriKodu]-[Sıra] formatında üretilir.</summary>
        public string KategoriKodu { get; set; } = "";
    }
}
