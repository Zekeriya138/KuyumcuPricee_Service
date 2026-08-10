using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

internal static class SupplierFinanceHelper
{
    public const string RefSettleAlacak = "SETTLE_ALACAK";
    public const string RefSettleBorc = "SETTLE_BORC";

    public static bool IsSettleAlacakOffset(SupplierTransaction x)
        => string.Equals(x.RefType, RefSettleAlacak, StringComparison.OrdinalIgnoreCase);

    public static bool IsSettleBorcOffset(SupplierTransaction x)
        => string.Equals(x.RefType, RefSettleBorc, StringComparison.OrdinalIgnoreCase);

    public static (decimal Borc, decimal Alacak) ComputeDovizGross(IEnumerable<SupplierTransaction> txs, string unit)
    {
        var normalized = NormalizeUnit(unit);
        decimal borc = 0m, alacak = 0m;
        foreach (var x in txs)
        {
            if (string.Equals(x.TxType, "ZIYNET", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(NormalizeUnit(x.TargetUnit), normalized, StringComparison.OrdinalIgnoreCase))
                continue;

            var amt = Math.Abs(x.TargetAmount);
            if (IsSettleBorcOffset(x))
            {
                borc -= amt;
                continue;
            }
            if (IsSettleAlacakOffset(x))
            {
                alacak -= amt;
                continue;
            }

            if (x.TargetAmount > 0m) alacak += x.TargetAmount;
            else if (x.TargetAmount < 0m) borc += Math.Abs(x.TargetAmount);
        }

        return (Math.Max(0m, decimal.Round(borc, 4, MidpointRounding.AwayFromZero)),
                Math.Max(0m, decimal.Round(alacak, 4, MidpointRounding.AwayFromZero)));
    }

    public static (decimal Borc, decimal Alacak) ComputeZiynetGross(IEnumerable<decimal> signedAdets)
    {
        decimal borc = 0m, alacak = 0m;
        foreach (var adet in signedAdets)
        {
            if (adet > 0m) alacak += adet;
            else if (adet < 0m) borc += Math.Abs(adet);
        }

        return (decimal.Round(borc, 3, MidpointRounding.AwayFromZero),
                decimal.Round(alacak, 3, MidpointRounding.AwayFromZero));
    }

    public static (decimal Borc, decimal Alacak) ComputeZiynetGrossFromTransactions(IEnumerable<SupplierTransaction> txs, string ad, string tip)
    {
        var normAd = (ad ?? "").Trim();
        var normTip = string.IsNullOrWhiteSpace(tip) ? "Yeni" : tip.Trim();
        decimal borc = 0m, alacak = 0m;
        foreach (var x in txs.Where(t => string.Equals(t.TxType, "ZIYNET", StringComparison.OrdinalIgnoreCase)))
        {
            var parsed = SupplierPanelFinanceHelper.TryParseZiynetMove(x.Description, x.TargetAmount);
            if (parsed is null) continue;
            if (!string.Equals(parsed.Ad, normAd, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(parsed.Tip, normTip, StringComparison.OrdinalIgnoreCase)) continue;

            var amt = Math.Abs(x.TargetAmount);
            if (IsSettleBorcOffset(x)) { borc -= amt; continue; }
            if (IsSettleAlacakOffset(x)) { alacak -= amt; continue; }

            if (x.TargetAmount > 0m) alacak += x.TargetAmount;
            else if (x.TargetAmount < 0m) borc += Math.Abs(x.TargetAmount);
        }

        return (Math.Max(0m, decimal.Round(borc, 3, MidpointRounding.AwayFromZero)),
                Math.Max(0m, decimal.Round(alacak, 3, MidpointRounding.AwayFromZero)));
    }

    public sealed record ZiynetFinanceRow(string Ad, string Tip, decimal Adet, decimal Borc, decimal Alacak);

    /// <summary>Brüt borç/alacak sütunları — SETTLE_* ref tipleri dahil (tedarikçi detay / işlem ekranı).</summary>
    public static List<ZiynetFinanceRow> BuildZiynetFinanceRows(IEnumerable<SupplierTransaction> txs)
    {
        var ziynetTxs = txs
            .Where(t => string.Equals(t.TxType, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var keys = new Dictionary<string, (string Ad, string Tip)>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in ziynetTxs)
        {
            var parsed = SupplierPanelFinanceHelper.TryParseZiynetMove(x.Description, x.TargetAmount);
            if (parsed is null) continue;
            var tip = string.IsNullOrWhiteSpace(parsed.Tip) ? "Yeni" : parsed.Tip.Trim();
            var key = $"{parsed.Ad.Trim().ToUpperInvariant()}|{tip.ToUpperInvariant()}";
            keys.TryAdd(key, (parsed.Ad.Trim(), tip));
        }

        return keys.Values
            .OrderBy(x => x.Ad, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Tip, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                var (borc, alacak) = ComputeZiynetGrossFromTransactions(ziynetTxs, item.Ad, item.Tip);
                var net = decimal.Round(alacak - borc, 3, MidpointRounding.AwayFromZero);
                return new ZiynetFinanceRow(item.Ad, item.Tip, net, borc, alacak);
            })
            .Where(x => x.Borc != 0m || x.Alacak != 0m)
            .ToList();
    }

    private static string NormalizeUnit(string? raw)
    {
        var u = (raw ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "TL" => "TL",
            "USD" => "USD",
            "EUR" => "EUR",
            "GBP" or "POUND" => "GBP",
            "HAS" or "GOLD" => "HAS",
            "GUMUS" or "GÜMÜŞ" or "SILVER" => "GUMUS",
            _ => "TL"
        };
    }
}
