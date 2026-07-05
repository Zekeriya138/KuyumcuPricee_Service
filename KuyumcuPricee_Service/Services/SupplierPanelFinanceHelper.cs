using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Kasa Tedarikçi Dengesi kartı — tedarikçi detay döviz paneli ve Altın(HAS) Toplam Bakiye ile aynı toplama mantığı.
/// </summary>
public static class SupplierPanelFinanceHelper
{
    public sealed record SupplierZiynetSlice(string Ad, string Tip, decimal Adet);

    /// <summary>Döviz HAS + ziynet HAS — tedarikçi detay OzetHasText toplamı.</summary>
    public static decimal ComputeAltinHasToplamBakiye(decimal balanceHas, IEnumerable<SupplierZiynetSlice> ziynetMoves)
    {
        return balanceHas + SumZiynetNetHas(ziynetMoves);
    }

    public static decimal SumZiynetNetHas(IEnumerable<SupplierZiynetSlice> ziynetMoves)
    {
        return ziynetMoves
            .Where(x => !string.IsNullOrWhiteSpace(x.Ad) && x.Adet != 0m)
            .GroupBy(x => new
            {
                Ad = (x.Ad ?? "").Trim().ToUpperInvariant(),
                Tip = string.IsNullOrWhiteSpace(x.Tip) ? "Yeni" : x.Tip.Trim()
            })
            .Sum(g =>
            {
                var adet = g.Sum(x => x.Adet);
                var first = g.First();
                return adet * CustomerPanelFinanceHelper.ResolveHasGramPerPiece(first.Ad, first.Tip);
            });
    }

    public static SupplierZiynetSlice? TryParseZiynetMove(string? description, decimal targetAmount)
    {
        var desc = (description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(desc)) return null;
        if (!desc.Contains("[ZIYNET]|", StringComparison.OrdinalIgnoreCase)) return null;

        string ad = "";
        string tip = "Yeni";
        decimal adet = 0m;
        foreach (var rawPart in desc.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.StartsWith("AD=", StringComparison.OrdinalIgnoreCase))
                ad = part.Substring(3).Trim();
            else if (part.StartsWith("TIP=", StringComparison.OrdinalIgnoreCase))
                tip = part.Substring(4).Trim();
            else if (part.StartsWith("ADET=", StringComparison.OrdinalIgnoreCase)
                     && decimal.TryParse(part.Substring(5).Trim().Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                adet = parsed;
        }

        if (adet == 0m)
            adet = targetAmount;

        if (string.IsNullOrWhiteSpace(ad) || adet == 0m)
            return null;

        return new SupplierZiynetSlice(ad, string.IsNullOrWhiteSpace(tip) ? "Yeni" : tip, adet);
    }
}
