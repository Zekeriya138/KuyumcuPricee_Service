using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

internal static class SupplierFinanceHelper
{
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

            var amt = x.TargetAmount;
            if (amt > 0m) alacak += amt;
            else if (amt < 0m) borc += Math.Abs(amt);
        }

        return (decimal.Round(borc, 4, MidpointRounding.AwayFromZero),
                decimal.Round(alacak, 4, MidpointRounding.AwayFromZero));
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
