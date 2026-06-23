using System.Globalization;

namespace kuyumcu_domain.Scrap;

/// <summary>Has altın: Saf altın ağırlığı = Gram × (ayar fineness). Fineness = Karat/24 veya milyem/1000.</summary>
public static class ScrapPureGoldCalculator
{
    /// <summary>"22K", "22", "916" vb. → 22/24</summary>
    public static decimal FinenessFromKarat(string? karatRaw)
    {
        var k = NormalizeKaratKey(karatRaw);
        if (string.IsNullOrEmpty(k)) return 0m;

        if (k.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            var num = k[..^1].Trim();
            if (decimal.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out var kv) && kv > 0 && kv <= 24)
                return kv / 24m;
        }

        if (decimal.TryParse(k, NumberStyles.Any, CultureInfo.InvariantCulture, out var mil) && mil > 0)
        {
            if (mil <= 1.5m) return mil;
            if (mil <= 1000m) return mil / 1000m;
        }

        return 0m;
    }

    public static decimal ComputePureGoldGrams(decimal weightGram, string? karatRaw)
    {
        var f = FinenessFromKarat(karatRaw);
        if (f <= 0 || weightGram <= 0) return 0m;
        return Math.Round(weightGram * f, 4);
    }

    /// <summary>Depo/Ayar ile uyumlu anahtar: 14K, 18K, 22K</summary>
    public static string NormalizeKaratKey(string? karat)
    {
        if (string.IsNullOrWhiteSpace(karat)) return "";
        var s = karat.Trim().ToUpperInvariant().Replace("AYAR", "").Replace("(", "").Replace(")", "").Replace(" ", "");
        if (s.EndsWith("K", StringComparison.Ordinal) && s.Length > 1)
        {
            var num = s[..^1];
            if (decimal.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v >= 8 && v <= 24)
                return $"{(int)v}K";
        }
        if (s.Contains("585") || s.Contains("14")) return "14K";
        if (s.Contains("750") || s.Contains("18")) return "18K";
        if (s.Contains("916") || s.Contains("22")) return "22K";
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var vv) && vv >= 8 && vv <= 24)
            return $"{(int)vv}K";
        return karat.Trim();
    }
}
