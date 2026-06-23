using System.Globalization;

namespace kuyumcu_domain.Helpers;

/// <summary>Has altın: saf altın gram = ağırlık × (ayar / 24).</summary>
public static class ScrapGoldMath
{
    /// <summary>"22K", "18K" vb. normalize edilmiş ayardan 24 üzerinden oran (örn. 22K → 22/24).</summary>
    public static decimal PurityRatioFromNormalizedKarat(string normalizedKarat)
    {
        if (!TryParseKaratNumber(normalizedKarat, out var k))
            return 0m;
        return k / 24m;
    }

    public static bool TryParseKaratNumber(string? karatToken, out decimal kOf24)
    {
        kOf24 = 0m;
        if (string.IsNullOrWhiteSpace(karatToken)) return false;
        var s = karatToken.Trim().ToUpperInvariant()
            .Replace("K", "", StringComparison.Ordinal)
            .Replace("AYAR", "", StringComparison.Ordinal)
            .Replace("(", "", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal)
            .Trim();
        if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return false;
        if (v < 8m || v > 24m) return false;
        kOf24 = v;
        return true;
    }

    public static decimal PureGoldGrams(decimal weightGram, string normalizedKarat)
        => Math.Round(weightGram * PurityRatioFromNormalizedKarat(normalizedKarat), 4);
}
