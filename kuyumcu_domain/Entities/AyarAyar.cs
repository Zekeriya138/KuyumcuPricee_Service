using System;

namespace kuyumcu_domain.Entities
{
    /// <summary>
    /// Her ayar (14K, 18K, 22K) için kullanıcının tanımladığı varsayılan değerler.
    /// İşçilik, Milyem (has karşılık oranı), Maliyet hesaplama formülleri.
    /// </summary>
    public class AyarAyar : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }

        /// <summary>14K, 18K, 22K vb.</summary>
        public string Ayar { get; set; } = "";

        /// <summary>Milyem oranı (585, 750, 916 vb.). Has altın karşılığı = gram * (Milyem/1000).</summary>
        public decimal Milyem { get; set; }

        /// <summary>Varsayılan işçilik bedeli (TL).</summary>
        public decimal Iscilik { get; set; }

        /// <summary>Varsayılan gram başı maliyet (TL) - ayarlardan hesaplama için.</summary>
        public decimal VarsayilanMaliyet { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
