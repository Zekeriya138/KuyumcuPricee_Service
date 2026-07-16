using System;

namespace kuyumcu_domain.Entities
{
    /// <summary>
    /// Hammadde depo stoku (ayar bazında). Kurallar:
    /// Her zaman TotalGram = BarcodedGram + UnbarcodedGram.
    /// Barkodlama: UnbarcodedGram azalır, BarcodedGram artar; TotalGram değişmez.
    /// Satış (ProductItem): BarcodedGram ve TotalGram azalır; UnbarcodedGram değişmez.
    /// </summary>
    public class DepoStok : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid BranchId { get; set; }

        /// <summary>14K, 18K, 22K vb. Ayar kodu.</summary>
        public string Ayar { get; set; } = "";

        /// <summary>Şubedeki toplam metal (barkodlu + barkodsuz). Satışta azalır.</summary>
        public decimal TotalGram { get; set; }

        /// <summary>Vitrinde barkodlu (ProductItem) olarak tutulan gram.</summary>
        public decimal BarcodedGram { get; set; }

        /// <summary>Depoda hâlâ hammadde (barkodsuz) gram.</summary>
        public decimal UnbarcodedGram { get; set; }

        /// <summary>Ortalama alış maliyeti (gram başı TL).</summary>
        public decimal OrtalamaMaliyet { get; set; }

        /// <summary>Son güncelleme.</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Branch Branch { get; set; } = null!;

        /// <summary>Toptancı alışı: toplam ve barkodsuz artar.</summary>
        public void Add(decimal gram, decimal birimMaliyet)
        {
            if (gram <= 0) return;
            var mevcutMal = TotalGram * OrtalamaMaliyet;
            var yeniMal = gram * birimMaliyet;
            TotalGram += gram;
            UnbarcodedGram += gram;
            OrtalamaMaliyet = TotalGram > 0 ? (mevcutMal + yeniMal) / TotalGram : birimMaliyet;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Barkodlama (ADIM 2–3): mevcut <see cref="DepoStok"/> satırında güncelleme; yeni satır eklenmez.
        /// <see cref="TotalGram"/> değişmez. <c>BarcodedGram += gram</c>, ardından <c>UnbarcodedGram = TotalGram - BarcodedGram</c>.
        /// <see cref="BarcodedGram"/> + gram &gt; <see cref="TotalGram"/> ise (ör. 100g stoktan 101g barkod) false döner.
        /// </summary>
        public bool MoveToBarcoded(decimal gram)
        {
            if (gram <= 0) return true;
            if (BarcodedGram + gram > TotalGram + 0.0001m)
                return false;
            BarcodedGram += gram;
            UnbarcodedGram = TotalGram - BarcodedGram;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Barkodlu ürün silme/iptal: barkodlamayı geri alır.
        /// <see cref="TotalGram"/> sabit; barkodlu azalır, barkodsuz artar.
        /// </summary>
        public bool MoveToUnbarcoded(decimal gram)
        {
            if (gram <= 0) return true;
            if (BarcodedGram + 0.0001m < gram)
                return false;
            BarcodedGram -= gram;
            UnbarcodedGram = TotalGram - BarcodedGram;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>Tekil ürün satışı: metal işyerinden çıkar.</summary>
        public bool OnBarcodedProductSold(decimal gram)
        {
            if (gram <= 0) return true;
            if (BarcodedGram < gram) return false;
            BarcodedGram -= gram;
            TotalGram -= gram;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>Satış iptal/iade: barkodlu parça tekrar stokta; depoya metal geri döner.</summary>
        public bool OnBarcodedProductReturned(decimal gram)
        {
            if (gram <= 0) return true;
            BarcodedGram += gram;
            TotalGram += gram;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
            return true;
        }

        /// <summary>Toptancıya hurda ile ödeme: yalnızca barkodsuz hammadde düşer.</summary>
        public bool WithdrawUnbarcoded(decimal gram)
        {
            if (gram <= 0) return true;
            if (UnbarcodedGram < gram) return false;
            UnbarcodedGram -= gram;
            TotalGram -= gram;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
            return true;
        }

        private void EnforceInvariant()
        {
            var t = BarcodedGram + UnbarcodedGram;
            if (Math.Abs(TotalGram - t) > 0.0001m)
                TotalGram = t;
        }
    }
}
