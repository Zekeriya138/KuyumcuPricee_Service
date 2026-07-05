using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

internal static class BalanceConversionZiynetHelper
{
    public const string ZiynetPrefix = "Z|";

    internal readonly record struct ConversionUnit(bool IsZiynet, string CurrencyUnit, string ZiynetAd, string ZiynetTip);

    public static bool TryParseUnit(string? raw, out ConversionUnit unit)
    {
        unit = default;
        var s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s)) return false;

        if (s.StartsWith(ZiynetPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = s[ZiynetPrefix.Length..];
            var sep = body.IndexOf('|');
            var ad = sep < 0 ? body.Trim() : body[..sep].Trim();
            var tip = sep < 0 ? "" : body[(sep + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(ad)) return false;
            unit = new ConversionUnit(true, "", ad, tip);
            return true;
        }

        unit = new ConversionUnit(false, NormalizeCurrency(s), "", "");
        return !string.IsNullOrWhiteSpace(unit.CurrencyUnit);
    }

    public static string EncodeZiynet(string ad, string? tip)
        => $"{ZiynetPrefix}{(ad ?? "").Trim()}|{(tip ?? "").Trim()}";

    public static string NormalizeCurrency(string? raw)
    {
        var u = (raw ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "TL" => "TL",
            "USD" => "USD",
            "EUR" => "EUR",
            "GBP" => "GBP",
            "POUND" => "GBP",
            "HAS" => "HAS",
            "GOLD" => "HAS",
            "GUMUS" => "GUMUS",
            "GÜMÜŞ" => "GUMUS",
            _ => u
        };
    }

    public static string ZiynetRateCode(string? adRaw, string? tipRaw)
    {
        var ad = (adRaw ?? "").Trim().ToUpperInvariant();
        var eski = (tipRaw ?? "").Trim().ToUpperInvariant().Contains("ESK", StringComparison.Ordinal);
        if (ad.Contains("ÇEYREK", StringComparison.Ordinal) || ad.Contains("CEYREK", StringComparison.Ordinal)) return eski ? "CEYREK_ESKI" : "CEYREK_YENI";
        if (ad.Contains("YARIM", StringComparison.Ordinal)) return eski ? "YARIM_ESKI" : "YARIM_YENI";
        if (ad.Contains("CUMHUR", StringComparison.Ordinal)) return "ATA_YENI";
        if (ad.Contains("ATA5", StringComparison.Ordinal) || ad.Contains("ATA 5", StringComparison.Ordinal) || ad.Contains("BEŞLI", StringComparison.Ordinal) || ad.Contains("BESLI", StringComparison.Ordinal)) return eski ? "ATA5_ESKI" : "ATA5_YENI";
        if (ad.Contains("ATA", StringComparison.Ordinal)) return eski ? "ATA_ESKI" : "ATA_YENI";
        if (ad.Contains("GREMSE", StringComparison.Ordinal) || ad.Contains("GREMESE", StringComparison.Ordinal)) return eski ? "GREMSE_ESKI" : "GREMSE_YENI";
        if (ad.Contains("TAM", StringComparison.Ordinal)) return eski ? "TAM_ESKI" : "TAM_YENI";
        if (ad.Contains("KÜLÇE", StringComparison.Ordinal) || ad.Contains("KULCE", StringComparison.Ordinal)) return "KULCE_ALTIN";
        if (ad.Contains("22 AYAR", StringComparison.Ordinal) || ad.Contains("22AYAR", StringComparison.Ordinal)) return "G22_TRY";
        if (ad == "HAS" || ad.Contains("HAS ALTIN", StringComparison.Ordinal) || ad.Contains("HASALTIN", StringComparison.Ordinal) || (ad.Contains("GRAM", StringComparison.Ordinal) && ad.Contains("ALTIN", StringComparison.Ordinal)) || ad == "GRAM") return "G24_TRY";
        if (ad.Contains("GÜMÜŞ", StringComparison.Ordinal) || ad.Contains("GUMUS", StringComparison.Ordinal)) return "XAG_GM_TRY";
        return "";
    }

    public static decimal ResolveZiynetTlRate(ExchangeRateService rates, string ad, string? tip, bool useBuyRate)
    {
        var code = ZiynetRateCode(ad, tip);
        if (string.IsNullOrEmpty(code)) return 0m;
        var rate = useBuyRate ? rates.GetQuoteBidByCode(code) : rates.GetQuoteAskByCode(code);
        return rate > 0m ? rate : 0m;
    }

    public static decimal ResolveCurrencyTlRate(ExchangeRateService rates, string unit, bool useBuyRate)
    {
        var map = useBuyRate ? rates.GetUnitToTlBuyRates() : rates.GetUnitToTlSellRates();
        var u = NormalizeCurrency(unit);
        return map.TryGetValue(u, out var r) && r > 0m ? r : 0m;
    }

    public static decimal ResolveUnitTlRate(ExchangeRateService rates, ConversionUnit unit, bool useBuyRate)
        => unit.IsZiynet
            ? ResolveZiynetTlRate(rates, unit.ZiynetAd, unit.ZiynetTip, useBuyRate)
            : ResolveCurrencyTlRate(rates, unit.CurrencyUnit, useBuyRate);

    public static string FormatUnitLabel(ConversionUnit unit)
    {
        if (!unit.IsZiynet) return unit.CurrencyUnit;
        return string.IsNullOrWhiteSpace(unit.ZiynetTip)
            ? unit.ZiynetAd
            : $"{unit.ZiynetAd} ({unit.ZiynetTip})";
    }

    public static bool UnitsEqual(ConversionUnit a, ConversionUnit b)
    {
        if (a.IsZiynet != b.IsZiynet) return false;
        if (a.IsZiynet)
            return string.Equals(a.ZiynetAd, b.ZiynetAd, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ZiynetTip ?? "", b.ZiynetTip ?? "", StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.CurrencyUnit, b.CurrencyUnit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Kaynak alacaklı (+) → kaynak alış, hedef satış; kaynak borçlu (−) → kaynak satış, hedef alış.
    /// Müşteri ve tedarikçi aynı kuralı kullanır.
    /// </summary>
    public static (bool UseBuySrc, bool UseBuyTgt) ResolveRateSides(bool isSupplier, decimal sourceBalance)
    {
        _ = isSupplier;
        return (sourceBalance >= 0m, sourceBalance < 0m);
    }

    public static string BuildConversionNote(
        string? description, decimal srcAmt, ConversionUnit src, decimal tgtAmt, ConversionUnit tgt,
        bool useBuySrc, bool useBuyTgt)
    {
        string kurTip;
        if (useBuySrc == useBuyTgt)
            kurTip = useBuySrc ? "Alış" : "Satış";
        else
            kurTip = $"Kaynak {(useBuySrc ? "Alış" : "Satış")}, Hedef {(useBuyTgt ? "Alış" : "Satış")}";

        var detail = $"{srcAmt.ToString("0.######", CultureInfo.InvariantCulture)} {FormatUnitLabel(src)} -> {tgtAmt.ToString("0.######", CultureInfo.InvariantCulture)} {FormatUnitLabel(tgt)} ({kurTip})";
        return string.IsNullOrWhiteSpace(description) ? detail : $"{description.Trim()} | {detail}";
    }
}
