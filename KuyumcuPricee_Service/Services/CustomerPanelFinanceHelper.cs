using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Kasa Müşteri Dengesi kartı — müşteri detay döviz paneli ve Altın(HAS) Toplam Bakiye ile aynı toplama mantığı.
/// </summary>
public static class CustomerPanelFinanceHelper
{
    public sealed record CustomerTxSlice(
        string GroupCode,
        string ItemName,
        string? ItemType,
        decimal Quantity,
        int Direction,
        decimal? HasEquivalent,
        string? CariDurum);

    private static readonly Dictionary<string, decimal> HasGramPerPieceByFamily =
        new(StringComparer.Ordinal)
        {
            ["KULCE"] = 0.995m,
            ["22AYAR"] = 0.916m,
            ["CEYREK"] = 1.605m,
            ["YARIM"] = 3.210m,
            ["TAM"] = 6.420m,
            ["ATA"] = 6.610m,
            ["ATA5"] = 33.05m,
            ["GREMSE"] = 16.05m,
        };

    private static readonly Dictionary<string, (string Family, bool IsEski)> RateCodeToFamily =
        new(StringComparer.Ordinal)
        {
            ["CEYREK_YENI"] = ("CEYREK", false),
            ["CEYREK_ESKI"] = ("CEYREK", true),
            ["YARIM_YENI"] = ("YARIM", false),
            ["YARIM_ESKI"] = ("YARIM", true),
            ["TAM_YENI"] = ("TAM", false),
            ["TAM_ESKI"] = ("TAM", true),
            ["ATA_YENI"] = ("ATA", false),
            ["ATA_ESKI"] = ("ATA", true),
            ["ATA5_YENI"] = ("ATA5", false),
            ["ATA5_ESKI"] = ("ATA5", true),
            ["GREMSE_YENI"] = ("GREMSE", false),
            ["GREMSE_ESKI"] = ("GREMSE", true),
            ["KULCE_ALTIN"] = ("KULCE", false),
            ["G22_TRY"] = ("22AYAR", false),
        };

    /// <summary>Döviz HAS + ziynet HAS + işçilikli HAS — müşteri detay OzetHasText toplamı.</summary>
    public static decimal ComputeAltinHasToplamBakiye(decimal balanceHas, IEnumerable<CustomerTxSlice> transactions)
    {
        var ziynetHas = SumZiynetNetHas(transactions);
        var iscilikliHas = SumIscilikliNetHas(transactions);
        return balanceHas + ziynetHas + iscilikliHas;
    }

    public static decimal SumZiynetNetHas(IEnumerable<CustomerTxSlice> transactions)
    {
        return transactions
            .Where(x => string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => new
            {
                ItemName = x.ItemName ?? "",
                Tip = string.IsNullOrWhiteSpace(x.ItemType) ? "Yeni" : x.ItemType!.Trim()
            })
            .Sum(g =>
            {
                var adet = g.Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);
                return adet * ResolveHasGramPerPiece(g.Key.ItemName, g.Key.Tip);
            });
    }

    public static decimal SumIscilikliNetHas(IEnumerable<CustomerTxSlice> transactions)
    {
        decimal total = 0m;
        foreach (var row in transactions.Where(x =>
                     string.Equals(x.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase)))
        {
            var has = Math.Abs(row.HasEquivalent ?? 0m);
            if (has == 0m) continue;
            total += IsBorcluCariDurum(row.CariDurum) ? -has : has;
        }
        return total;
    }

    public static decimal ResolveHasGramPerPiece(string? adRaw, string? tipRaw)
    {
        var (family, _) = ResolveZiynetKey(adRaw, tipRaw);
        return !string.IsNullOrEmpty(family) && HasGramPerPieceByFamily.TryGetValue(family, out var gram)
            ? gram
            : 0m;
    }

    private static bool IsBorcluCariDurum(string? raw)
    {
        var txt = (raw ?? "").Trim();
        return txt.Contains("borclu", StringComparison.OrdinalIgnoreCase)
               || txt.Contains("borçlu", StringComparison.OrdinalIgnoreCase);
    }

    private static string FoldForMatch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var value = raw.Trim().ToUpper(CultureInfo.GetCultureInfo("tr-TR"));
        return value
            .Replace('İ', 'I')
            .Replace('Ş', 'S')
            .Replace('Ğ', 'G')
            .Replace('Ü', 'U')
            .Replace('Ö', 'O')
            .Replace('Ç', 'C');
    }

    private static string NormalizeZiynetTipDisplay(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(t)) return "Yeni";
        if (t.Equals("yeni", StringComparison.OrdinalIgnoreCase)) return "Yeni";
        if (t.Equals("eski", StringComparison.OrdinalIgnoreCase)) return "Eski";
        return t;
    }

    private static bool IsEskiTip(string? raw)
    {
        var folded = FoldForMatch(NormalizeZiynetTipDisplay(raw));
        if (string.IsNullOrEmpty(folded)) return false;
        return folded.Contains("ESK", StringComparison.Ordinal)
               || folded.Contains("OLD", StringComparison.Ordinal);
    }

    private static string NormalizeZiynetAd(string? raw)
    {
        var value = FoldForMatch(raw);
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Contains("KULCE", StringComparison.Ordinal))
            return "GRAM ALTIN(KULCE)";
        if ((value.Contains("22 AYAR", StringComparison.Ordinal) || value.Contains("22AYAR", StringComparison.Ordinal)) &&
            (value.Contains("GR", StringComparison.Ordinal) || value.Contains("GRAM", StringComparison.Ordinal)))
            return "22 AYAR(GR)";
        if (value == "GRAM" || value.Contains("GRAM ALTIN (HAS)", StringComparison.Ordinal) ||
            value.Contains("GRAM ALTIN(HAS)", StringComparison.Ordinal))
            return "GRAM ALTIN(KULCE)";
        return value;
    }

    private static string MapZiynetAliases(string ad)
    {
        if (ad == "TEK" || ad.StartsWith("TEK_", StringComparison.Ordinal))
            ad = "TAM" + ad[3..];
        if (ad.Contains("GREMESE", StringComparison.Ordinal))
            ad = ad.Replace("GREMESE", "GREMSE", StringComparison.Ordinal);
        return ad;
    }

    private static void StripEmbeddedTipFromAd(ref string ad, ref bool isEski, ref bool isYeni)
    {
        if (RateCodeToFamily.TryGetValue(ad, out var mapped))
        {
            isEski = mapped.IsEski;
            isYeni = !mapped.IsEski;
            ad = mapped.Family;
            return;
        }

        if (ad.Contains("(ESKI)", StringComparison.Ordinal))
        {
            isEski = true;
            ad = ad.Replace("(ESKI)", "", StringComparison.Ordinal).Trim();
        }
        else if (ad.Contains("(YENI)", StringComparison.Ordinal))
        {
            isYeni = true;
            ad = ad.Replace("(YENI)", "", StringComparison.Ordinal).Trim();
        }

        if (ad.Contains("_ESKI", StringComparison.Ordinal))
        {
            isEski = true;
            ad = ad.Replace("_ESKI", "", StringComparison.Ordinal);
        }
        else if (ad.EndsWith(" ESKI", StringComparison.Ordinal))
        {
            isEski = true;
            ad = ad[..^5].TrimEnd();
        }
        else if (ad.StartsWith("ESKI ", StringComparison.Ordinal))
        {
            isEski = true;
            ad = ad[5..].TrimStart();
        }
        else if (ad.Contains("_YENI", StringComparison.Ordinal))
        {
            isYeni = true;
            ad = ad.Replace("_YENI", "", StringComparison.Ordinal);
        }
        else if (ad.EndsWith(" YENI", StringComparison.Ordinal))
        {
            isYeni = true;
            ad = ad[..^5].TrimEnd();
        }
        else if (ad.StartsWith("YENI ", StringComparison.Ordinal))
        {
            isYeni = true;
            ad = ad[5..].TrimStart();
        }
    }

    private static string BuildRateCode(string ad, bool eski)
    {
        ad = MapZiynetAliases(NormalizeZiynetAd(ad));
        if (string.IsNullOrWhiteSpace(ad)) return "";

        if (ad.Contains("CEYREK", StringComparison.Ordinal)) return eski ? "CEYREK_ESKI" : "CEYREK_YENI";
        if (ad.Contains("YARIM", StringComparison.Ordinal)) return eski ? "YARIM_ESKI" : "YARIM_YENI";
        if (ad.Contains("CUMHUR", StringComparison.Ordinal)) return "ATA_YENI";
        if (ad.Contains("ATA5", StringComparison.Ordinal) || ad.Contains("5LI ATA", StringComparison.Ordinal) ||
            ad.Contains("BESLI", StringComparison.Ordinal))
            return eski ? "ATA5_ESKI" : "ATA5_YENI";
        if (ad.Contains("ATA", StringComparison.Ordinal)) return eski ? "ATA_ESKI" : "ATA_YENI";
        if (ad.Contains("GREMSE", StringComparison.Ordinal)) return eski ? "GREMSE_ESKI" : "GREMSE_YENI";
        if (ad.Contains("TAM", StringComparison.Ordinal)) return eski ? "TAM_ESKI" : "TAM_YENI";
        if (ad.Contains("KULCE", StringComparison.Ordinal)) return "KULCE_ALTIN";
        if (ad.Contains("22 AYAR", StringComparison.Ordinal) || ad.Contains("22AYAR", StringComparison.Ordinal)) return "G22_TRY";
        return "";
    }

    private static (string Family, bool IsEski) ResolveZiynetKey(string? adRaw, string? tipRaw)
    {
        var ad = FoldForMatch(adRaw);
        var tipNorm = NormalizeZiynetTipDisplay(tipRaw);
        var isEski = IsEskiTip(tipNorm);
        var isYeni = FoldForMatch(tipNorm).Contains("YEN", StringComparison.Ordinal) ||
                     FoldForMatch(tipNorm).Contains("NEW", StringComparison.Ordinal);

        StripEmbeddedTipFromAd(ref ad, ref isEski, ref isYeni);
        ad = MapZiynetAliases(NormalizeZiynetAd(ad));

        var rateCode = BuildRateCode(ad, isEski);
        if (!string.IsNullOrEmpty(rateCode) && RateCodeToFamily.TryGetValue(rateCode, out var mapped))
            return mapped;

        return ("", false);
    }
}
