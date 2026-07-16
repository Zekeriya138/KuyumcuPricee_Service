using System.Globalization;
using kuyumcu_domain.Entities;
using KUYUMCU.Price_Service.Services;

namespace KUYUMCU.Price_Service.Services;

internal sealed class CariSonMiktarSnapshot
{
    public string Birim { get; init; } = "";
    public string SonMiktar { get; init; } = "";
}

/// <summary>Son işlemler listesinde işlem sonrası birim bakiyesi (son miktar).</summary>
internal static class CariRecentTxBalanceHelper
{
    public static Dictionary<Guid, CariSonMiktarSnapshot> BuildCustomerIndex(IEnumerable<CustomerTransaction> transactions)
    {
        var result = new Dictionary<Guid, CariSonMiktarSnapshot>();
        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var x in OrderCustomer(transactions))
        {
            if (!TryResolveCustomerBalanceLeg(x, out var unitKey, out var unitLabel, out var delta))
                continue;

            balances.TryGetValue(unitKey, out var cur);
            var next = cur + delta;
            balances[unitKey] = next;
            result[x.Id] = new CariSonMiktarSnapshot
            {
                Birim = unitLabel,
                SonMiktar = FormatBalance(unitLabel, next)
            };
        }

        return result;
    }

    public static Dictionary<Guid, CariSonMiktarSnapshot> BuildSupplierIndex(IEnumerable<SupplierTransaction> transactions)
    {
        var result = new Dictionary<Guid, CariSonMiktarSnapshot>();
        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var x in OrderSupplier(transactions))
        {
            if (!TryResolveSupplierBalanceLeg(x, out var unitKey, out var unitLabel, out var delta))
                continue;

            balances.TryGetValue(unitKey, out var cur);
            var next = cur + delta;
            balances[unitKey] = next;
            result[x.Id] = new CariSonMiktarSnapshot
            {
                Birim = unitLabel,
                SonMiktar = FormatBalance(unitLabel, next)
            };
        }

        return result;
    }

    private static IEnumerable<CustomerTransaction> OrderCustomer(IEnumerable<CustomerTransaction> transactions)
        => transactions
            .Where(x => !x.IsReversed && !x.IsDeleted)
            .OrderBy(x => x.TxDate)
            .ThenBy(x => x.CreatedAt);

    private static IEnumerable<SupplierTransaction> OrderSupplier(IEnumerable<SupplierTransaction> transactions)
        => transactions
            .Where(x => !x.IsReversed && !x.IsDeleted)
            .OrderBy(x => x.TxDate)
            .ThenBy(x => x.CreatedAt);

    private static bool TryResolveCustomerBalanceLeg(
        CustomerTransaction x,
        out string unitKey,
        out string unitLabel,
        out decimal delta)
    {
        unitKey = "";
        unitLabel = "";
        delta = 0m;

        var group = (x.GroupCode ?? "").Trim().ToUpperInvariant();
        if (group == "AUDIT")
        {
            if (!string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!TryParseZiynetDusumKey(x, out unitKey, out unitLabel))
                return false;

            delta = ComputeCustomerNetDelta(x);
            return true;
        }

        if (group == "DOVIZ")
        {
            var unit = (x.ItemName ?? "TL").Trim().ToUpperInvariant();
            unitKey = $"DOVIZ:{unit}";
            unitLabel = unit;
            delta = ComputeCustomerNetDelta(x);
            return true;
        }

        if (group == "ZIYNET")
        {
            var ad = CustomerFinanceHelper.NormalizeZiynetItemName(x.ItemName);
            if (string.Equals(ad, "RESTORE", StringComparison.OrdinalIgnoreCase))
                return false;

            if (CustomerFinanceHelper.IsHasAltinZiynetAd(ad))
            {
                unitKey = "DOVIZ:HAS";
                unitLabel = "HAS";
            }
            else
            {
                var tip = CustomerFinanceHelper.NormalizeZiynetTipGroupingKey(ad, x.ItemType);
                unitKey = $"ZIYNET:{ad}|{tip}";
                unitLabel = $"{ToDisplayZiynetAd(ad)}/{tip}";
            }

            delta = ComputeCustomerNetDelta(x);
            return true;
        }

        if (group == "ISCILIKLI")
        {
            var name = (x.ItemName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return false;

            unitKey = $"ISCILIKLI:{name}";
            unitLabel = name;
            var qty = Math.Abs(x.Gram ?? x.Quantity);
            delta = x.Direction >= 0 ? qty : -qty;
            return true;
        }

        return false;
    }

    private static bool TryResolveSupplierBalanceLeg(
        SupplierTransaction x,
        out string unitKey,
        out string unitLabel,
        out decimal delta)
    {
        unitKey = "";
        unitLabel = "";
        delta = 0m;

        var txType = (x.TxType ?? "").Trim().ToUpperInvariant();
        if (txType == "ZIYNET")
        {
            var move = TryParseSupplierZiynetMove(x);
            if (move is null)
                return false;

            var adKey = (move.Ad ?? "").Trim().ToUpperInvariant();
            var tipKey = string.IsNullOrWhiteSpace(move.Tip) ? "YENI" : move.Tip.Trim().ToUpperInvariant();
            unitKey = $"ZIYNET:{adKey}|{tipKey}";
            unitLabel = $"{move.Ad}/{move.Tip}";
            delta = move.Adet;
            return true;
        }

        var tgt = NormalizeUnit(x.TargetUnit);
        unitKey = $"DOVIZ:{tgt}";
        unitLabel = tgt;
        delta = x.TargetAmount;
        return true;
    }

    private static decimal ComputeCustomerNetDelta(CustomerTransaction x)
    {
        var qty = Math.Abs(x.Quantity);
        if (CustomerFinanceHelper.IsSettleAlacakOffset(x))
            return -qty;
        if (CustomerFinanceHelper.IsSettleBorcOffset(x))
            return qty;
        return x.Direction >= 0 ? qty : -qty;
    }

    private static bool TryParseZiynetDusumKey(CustomerTransaction x, out string unitKey, out string unitLabel)
    {
        unitKey = "";
        unitLabel = "";

        var raw = (x.ItemType ?? "").Trim();
        if (raw.Contains('|', StringComparison.Ordinal))
        {
            var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                var ad = CustomerFinanceHelper.NormalizeZiynetItemName(parts[0]);
                var tip = CustomerFinanceHelper.NormalizeZiynetTipGroupingKey(ad, parts[1]);
                unitKey = $"ZIYNET:{ad}|{tip}";
                unitLabel = $"{ToDisplayZiynetAd(ad)}/{tip}";
                return true;
            }
        }

        var note = x.Note ?? "";
        var marker = "Ziynet düşüm:";
        var idx = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        var tail = note[(idx + marker.Length)..].Trim();
        var pipeIdx = tail.IndexOf('|');
        var detail = pipeIdx >= 0 ? tail[..pipeIdx].Trim() : tail;
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        string adRaw;
        string tipRaw = "Yeni";
        var openParen = detail.IndexOf('(');
        if (openParen > 0 && detail.Contains(')'))
        {
            adRaw = detail[..openParen].Trim();
            tipRaw = detail[(openParen + 1)..].Replace(")", "").Trim();
        }
        else
        {
            adRaw = detail;
        }

        var adNorm = CustomerFinanceHelper.NormalizeZiynetItemName(adRaw);
        var tipNorm = CustomerFinanceHelper.NormalizeZiynetTipGroupingKey(adNorm, tipRaw);
        unitKey = $"ZIYNET:{adNorm}|{tipNorm}";
        unitLabel = $"{ToDisplayZiynetAd(adNorm)}/{tipNorm}";
        return true;
    }

    private sealed record SupplierZiynetMove(string Ad, string Tip, decimal Adet);

    private static SupplierZiynetMove? TryParseSupplierZiynetMove(SupplierTransaction tx)
    {
        var desc = (tx.Description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(desc) || !desc.Contains("[ZIYNET]|", StringComparison.OrdinalIgnoreCase))
            return null;

        string ad = "";
        string tip = "Yeni";
        decimal adet = 0m;
        var parts = desc.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.StartsWith("AD=", StringComparison.OrdinalIgnoreCase))
                ad = part[3..].Trim();
            else if (part.StartsWith("TIP=", StringComparison.OrdinalIgnoreCase))
                tip = part[4..].Trim();
            else if (part.StartsWith("ADET=", StringComparison.OrdinalIgnoreCase))
                decimal.TryParse(part[5..].Trim().Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out adet);
        }

        if (adet == 0m)
            adet = tx.TargetAmount;

        if (string.IsNullOrWhiteSpace(ad))
            return null;

        return new SupplierZiynetMove(ad, tip, adet);
    }

    private static string FormatBalance(string unitLabel, decimal value)
    {
        var upper = (unitLabel ?? "").Trim().ToUpperInvariant();
        if (upper is "TL" or "TRY")
            return $"{value.ToString("N2", CultureInfo.InvariantCulture)} TL";
        if (upper is "USD" or "EUR" or "GBP")
            return $"{value.ToString("N4", CultureInfo.InvariantCulture)} {upper}";
        if (upper == "HAS")
            return $"{value.ToString("N4", CultureInfo.InvariantCulture)} HAS";
        if (upper == "GUMUS" || upper.Contains("GUMUS", StringComparison.Ordinal))
            return $"{value.ToString("N4", CultureInfo.InvariantCulture)} GUMUS";
        if (upper.Contains('/'))
            return $"{value.ToString("N3", CultureInfo.InvariantCulture)} adet";
        return $"{value.ToString("N3", CultureInfo.InvariantCulture)} gr";
    }

    private static string ToDisplayZiynetAd(string normalizedAd)
    {
        var ad = (normalizedAd ?? "").Trim();
        if (ad.Contains("GRAM ALTIN", StringComparison.OrdinalIgnoreCase))
            return "Gram Altın(Külçe)";
        if (ad.Contains("22 AYAR", StringComparison.OrdinalIgnoreCase))
            return "22 Ayar (gr)";
        if (string.IsNullOrWhiteSpace(ad))
            return ad;
        return char.ToUpper(ad[0], CultureInfo.GetCultureInfo("tr-TR")) + ad[1..].ToLower(CultureInfo.GetCultureInfo("tr-TR"));
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
