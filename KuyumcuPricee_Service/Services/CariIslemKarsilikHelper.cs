using System.Globalization;
using System.Text.RegularExpressions;
using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

/// <summary>
/// Cari işlemde hedef birim satırında, fiziksel ödeme/tahsil birimini göstermek için not işaretleyici ve çözümleme.
/// </summary>
internal static class CariIslemKarsilikHelper
{
    public const string Prefix = "[CARI_KARSILIK]";

    private static readonly Regex ConversionArrowRegex = new(
        @"(?<srcAmt>[0-9]+(?:[.,][0-9]+)?)\s+(?<srcUnit>[A-Za-zÇçĞğİıÖöŞşÜü%]+)\s*->\s*(?<tgtAmt>[0-9]+(?:[.,][0-9]+)?)\s+(?<tgtUnit>[A-Za-zÇçĞğİıÖöŞşÜü%]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string AppendCrossUnitNote(string? note, decimal srcAmount, string srcUnit, decimal tgtAmount, string tgtUnit)
    {
        var src = NormalizeUnit(srcUnit);
        var tgt = NormalizeUnit(tgtUnit);
        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(tgt))
            return note ?? "";
        if (string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase))
            return note ?? "";

        var marker = BuildMarker(srcAmount, src, tgtAmount, tgt);
        var baseNote = (note ?? "").Trim();
        if (baseNote.Contains(Prefix, StringComparison.OrdinalIgnoreCase))
            return baseNote;
        return string.IsNullOrWhiteSpace(baseNote) ? marker : $"{baseNote} {marker}";
    }

    public static string BuildMarker(decimal srcAmount, string srcUnit, decimal tgtAmount, string tgtUnit)
        => $"{Prefix}SRC={FormatInvariant(srcAmount)}|SRC_UNIT={NormalizeUnit(srcUnit)}|TGT={FormatInvariant(tgtAmount)}|TGT_UNIT={NormalizeUnit(tgtUnit)}";

    public static string ResolveCustomerCounterpart(CustomerTransaction x)
    {
        var rowUnit = NormalizeUnit(x.ItemName);
        if (string.IsNullOrWhiteSpace(rowUnit))
            return "";

        if (TryParseMarker(x.Note, out var srcAmt, out var srcUnit, out var tgtAmt, out var tgtUnit)
            || TryParseConversionNote(x.Note, out srcAmt, out srcUnit, out tgtAmt, out tgtUnit))
        {
            if (UnitsEqual(rowUnit, tgtUnit))
                return FormatCounterpart(srcAmt, srcUnit, tgtAmt, Math.Abs(x.Quantity));
            if (UnitsEqual(rowUnit, srcUnit))
                return FormatCounterpart(tgtAmt, tgtUnit, srcAmt, Math.Abs(x.Quantity));
        }

        if (!UnitsEqual(rowUnit, "TL") && x.TotalPriceTl is > 0m)
            return FormatAmount(Math.Abs(x.TotalPriceTl.Value), "TL");

        return "";
    }

    public static string ResolveSupplierCounterpart(SupplierTransaction x)
    {
        var txType = (x.TxType ?? "").Trim().ToUpperInvariant();
        if (txType is "OPENING_BALANCE" or "BALANCE_CONVERSION" or "ZIYNET")
            return "";

        var srcUnit = NormalizeUnit(x.SourceUnit);
        var tgtUnit = NormalizeUnit(x.TargetUnit);
        if (string.IsNullOrWhiteSpace(srcUnit) || string.IsNullOrWhiteSpace(tgtUnit))
            return "";

        if (TryParseMarker(x.Description, out var srcAmt, out var markerSrcUnit, out _, out _)
            || TryParseConversionNote(x.Description, out srcAmt, out markerSrcUnit, out _, out _))
        {
            if (!string.IsNullOrWhiteSpace(markerSrcUnit))
                srcUnit = markerSrcUnit;
            if (srcAmt > 0m && !UnitsEqual(srcUnit, tgtUnit))
                return FormatAmount(srcAmt, srcUnit);
        }

        if (!x.IsConverted && UnitsEqual(srcUnit, tgtUnit))
            return "";

        if (Math.Abs(x.SourceAmount) > 0m && !UnitsEqual(srcUnit, tgtUnit))
            return FormatAmount(Math.Abs(x.SourceAmount), srcUnit);

        return "";
    }

    private static string FormatCounterpart(decimal totalCounterAmount, string counterUnit, decimal totalRowAmount, decimal rowAmount)
    {
        if (totalCounterAmount <= 0m || string.IsNullOrWhiteSpace(counterUnit))
            return "";

        var ratio = totalRowAmount > 0m ? rowAmount / totalRowAmount : 1m;
        var amount = Math.Abs(totalCounterAmount) * ratio;
        if (amount <= 0m)
            return "";

        return FormatAmount(amount, counterUnit);
    }

    private static bool TryParseMarker(string? note, out decimal srcAmt, out string srcUnit, out decimal tgtAmt, out string tgtUnit)
    {
        srcAmt = 0m;
        tgtAmt = 0m;
        srcUnit = "";
        tgtUnit = "";

        var text = note ?? "";
        var idx = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var segment = text[(idx + Prefix.Length)..].Trim();
        if (segment.StartsWith("|", StringComparison.Ordinal))
            segment = segment[1..];

        foreach (var part in segment.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToUpperInvariant();
            var val = part[(eq + 1)..].Trim();
            switch (key)
            {
                case "SRC":
                    ParseDecimal(val, out srcAmt);
                    break;
                case "SRC_UNIT":
                    srcUnit = NormalizeUnit(val);
                    break;
                case "TGT":
                    ParseDecimal(val, out tgtAmt);
                    break;
                case "TGT_UNIT":
                    tgtUnit = NormalizeUnit(val);
                    break;
            }
        }

        return srcAmt > 0m && tgtAmt > 0m && !string.IsNullOrWhiteSpace(srcUnit) && !string.IsNullOrWhiteSpace(tgtUnit);
    }

    private static bool TryParseConversionNote(string? note, out decimal srcAmt, out string srcUnit, out decimal tgtAmt, out string tgtUnit)
    {
        srcAmt = 0m;
        tgtAmt = 0m;
        srcUnit = "";
        tgtUnit = "";

        var match = ConversionArrowRegex.Match(note ?? "");
        if (!match.Success) return false;

        ParseDecimal(match.Groups["srcAmt"].Value, out srcAmt);
        ParseDecimal(match.Groups["tgtAmt"].Value, out tgtAmt);
        srcUnit = NormalizeUnit(match.Groups["srcUnit"].Value);
        tgtUnit = NormalizeUnit(match.Groups["tgtUnit"].Value);
        return srcAmt > 0m && tgtAmt > 0m && !string.IsNullOrWhiteSpace(srcUnit) && !string.IsNullOrWhiteSpace(tgtUnit);
    }

    private static string FormatAmount(decimal amount, string unit)
    {
        var u = NormalizeUnit(unit);
        var digits = u is "HAS" or "GUMUS" ? 6 : u == "TL" ? 2 : 4;
        return $"{amount.ToString($"N{digits}", CultureInfo.CurrentCulture)} {u}";
    }

    private static string NormalizeUnit(string? unit)
    {
        var u = (unit ?? "").Trim().ToUpperInvariant();
        if (u is "EURO") return "EUR";
        if (u is "GRAM ALTIN" or "GRAM ALTIN(HAS)" or "GRAM ALTIN (HAS)") return "HAS";
        if (u is "GÜMÜŞ" or "SILVER") return "GUMUS";
        return u;
    }

    private static bool UnitsEqual(string a, string b)
        => string.Equals(NormalizeUnit(a), NormalizeUnit(b), StringComparison.OrdinalIgnoreCase);

    private static string FormatInvariant(decimal value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void ParseDecimal(string raw, out decimal value)
    {
        var t = (raw ?? "").Trim();
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return;
        decimal.TryParse(t, NumberStyles.Number, CultureInfo.CurrentCulture, out value);
    }
}
