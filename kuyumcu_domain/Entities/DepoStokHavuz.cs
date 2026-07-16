using System;

namespace kuyumcu_domain.Entities
{
    /// <summary>
    /// Hammadde havuzu: Mal tanımı + Tedarikçi + Birim maliyet + Ayar bazında barkodlu/barkodsuz gram.
    /// <see cref="DepoStok"/> ayar toplamı ile birlikte güncellenir; tutarlılık için her iki tablo da transaction içinde güncellenir.
    /// </summary>
    public class DepoStokHavuz : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid BranchId { get; set; }

        /// <summary>Normalize edilmiş ayar kodu (örn. 22K).</summary>
        public string Ayar { get; set; } = "";

        /// <summary>Mal tanımı — büyük harf trim (eşleştirme anahtarı).</summary>
        public string MalTanimNorm { get; set; } = "";

        /// <summary>Tedarikçi firma — büyük harf trim.</summary>
        public string TedarikciFirmaNorm { get; set; } = "";

        /// <summary>Birim işçilik (has); ürün DepoBirimMaliyet ile aynı.</summary>
        public decimal BirimMaliyet { get; set; }

        public decimal TotalGram { get; set; }
        public decimal BarcodedGram { get; set; }
        public decimal UnbarcodedGram { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Branch Branch { get; set; } = null!;

        public void AddGram(decimal gram)
        {
            if (gram <= 0) return;
            TotalGram += gram;
            UnbarcodedGram += gram;
            EnforceInvariant();
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Üçlü anahtar (Mal+Tedarikçi+Birim) + ayar satırında barkodlama; yeni satır eklenmez.
        /// <see cref="TotalGram"/> sabit; <c>BarcodedGram += gram</c>, <c>UnbarcodedGram = TotalGram - BarcodedGram</c>.
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
        /// <see cref="TotalGram"/> sabit; <c>BarcodedGram -= gram</c>, <c>UnbarcodedGram = TotalGram - BarcodedGram</c>.
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

        /// <summary>Toptancı hurda ödemesi vb.: yalnızca barkodsuz düşer (DepoStok.WithdrawUnbarcoded ile uyumlu).</summary>
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

        /// <summary>Tekil barkodlu ürün satışı: barkodlu ve toplam gram azalır; barkodsuz sabit kalır.</summary>
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

        /// <summary>Satış iadesi/geri alım: barkodlu ve toplam gram artar; barkodsuz sabit kalır.</summary>
        public bool OnBarcodedProductReturned(decimal gram)
        {
            if (gram <= 0) return true;
            BarcodedGram += gram;
            TotalGram += gram;
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
