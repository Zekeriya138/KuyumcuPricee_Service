using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

public static class JewelrySpecialBaseCalculator
{
    public static bool TryBuild(
        decimal urunGram,
        string? karatText,
        decimal altinBedeliMatrah,
        decimal birimMaliyetMilyem,
        decimal birimSatisIscilikMilyem,
        decimal genelToplamKdvDahil,
        decimal kdvOrani,
        out JewelrySpecialBaseResult result)
    {
        result = default;

        var qty = urunGram <= 0 ? 0m : urunGram;
        var milyem = MilyemFromKarat(karatText);
        if (qty <= 0m || milyem <= 0m || genelToplamKdvDahil <= 0m)
            return false;

        var kdvRate = NormalizeRateRatio(kdvOrani);
        if (kdvRate <= 0m)
            kdvRate = 0.20m;

        var safHasGram = qty * milyem;
        if (safHasGram <= 0m)
            return false;

        var altinBedeli = Round2(Math.Max(0m, altinBedeliMatrah));
        var toplamSatisAgirligiHas = 0m;
        var hasAltinGramFiyat = 0m;
        var maliyet = NormalizeMilyemComponent(birimMaliyetMilyem);
        var iscilik = NormalizeMilyemComponent(birimSatisIscilikMilyem);
        var hasToplamPayda = milyem + maliyet + iscilik;
        var hasIscilikVar = iscilik > 0m;

        // Ana akış: Altın bedeli satış satırındaki matrahtan gelsin (anlık kur + seçilen fiyat tipi).
        if (altinBedeli > 0m && altinBedeli < genelToplamKdvDahil)
        {
            hasAltinGramFiyat = altinBedeli / safHasGram;
            toplamSatisAgirligiHas = qty * milyem;
        }
        else
        {
            // Satış satırındaki altın matrahı toplamla aynıysa (çoğu tek satır satış), seçilen birim fiyatı
            // "ürün gram fiyatı" kabul ederek saf has gram bedelini türet.
            // Böylece altın satırı büyük, işçilik/KDV satırı daha küçük kalır.
            if (altinBedeli >= genelToplamKdvDahil && qty > 0m)
            {
                var secilenBirimFiyat = altinBedeli / qty;
                var safHasAltinBedeli = Round2(safHasGram * secilenBirimFiyat);
                if (safHasAltinBedeli > 0m && safHasAltinBedeli < genelToplamKdvDahil)
                {
                    altinBedeli = safHasAltinBedeli;
                    hasAltinGramFiyat = secilenBirimFiyat;
                    toplamSatisAgirligiHas = qty * milyem;
                }
            }

            if (hasAltinGramFiyat > 0m && toplamSatisAgirligiHas > 0m)
                goto ComputeRemainder;

            // Fallback: Satış satırındaki matrah toplamı tamamen kapatıyorsa işçilik satırı kaybolmasın.
            // Milyem oranından altın payını hesaplayıp kalan kısmı işçilik+KDV olarak ayır.
            toplamSatisAgirligiHas = qty * hasToplamPayda;
            if (toplamSatisAgirligiHas <= 0m)
                return false;

            if (hasIscilikVar && hasToplamPayda > 0m)
            {
                var altinOrani = milyem / hasToplamPayda;
                altinBedeli = Round2(genelToplamKdvDahil * altinOrani);
                if (altinBedeli >= genelToplamKdvDahil)
                    altinBedeli = Round2(genelToplamKdvDahil - 0.01m);
            }
            else
            {
                hasAltinGramFiyat = genelToplamKdvDahil / toplamSatisAgirligiHas;
                altinBedeli = Round2(safHasGram * hasAltinGramFiyat);
            }

            if (hasAltinGramFiyat <= 0m)
                hasAltinGramFiyat = altinBedeli / safHasGram;
        }

    ComputeRemainder:
        var iscilikKdvDahil = Round2(genelToplamKdvDahil - altinBedeli);
        if (iscilikKdvDahil < 0m)
            iscilikKdvDahil = 0m;

        var kdvMatrahi = Round2(iscilikKdvDahil / (1m + kdvRate));
        var hesaplananKdv = Round2(iscilikKdvDahil - kdvMatrahi);

        var altinBirimFiyat = qty <= 0m ? 0m : Round2(altinBedeli / qty);
        var iscilikBirimFiyat = qty <= 0m ? 0m : Round2(kdvMatrahi / qty);

        result = new JewelrySpecialBaseResult(
            Round2(safHasGram),
            Round2(toplamSatisAgirligiHas),
            Round2(hasAltinGramFiyat),
            altinBedeli,
            iscilikKdvDahil,
            kdvMatrahi,
            hesaplananKdv,
            altinBirimFiyat,
            iscilikBirimFiyat,
            kdvRate);

        return true;
    }

    public static decimal MilyemFromKarat(string? karatText)
    {
        var raw = (karatText ?? string.Empty).Trim().ToUpperInvariant();
        if (raw.Contains("916")) return 0.916m;
        if (raw.Contains("22")) return 0.916m;
        if (raw.Contains("999")) return 1.000m;
        if (raw.Contains("24")) return 1.000m;
        if (raw.Contains("750")) return 0.750m;
        if (raw.Contains("18")) return 0.750m;
        if (raw.Contains("585")) return 0.585m;
        if (raw.Contains("14")) return 0.585m;
        if (raw.Contains("333")) return 0.333m;
        if (raw.Contains("8")) return 0.333m;

        // "0.916" gibi doğrudan milyem girişi de desteklensin.
        var normalized = raw.Replace(",", ".", StringComparison.Ordinal);
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0m && parsed <= 1.5m)
            return parsed;

        return 0m;
    }

    public static string BuildGoldLineName(string? karatText)
    {
        var raw = (karatText ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(raw))
            return "Altın";
        if (raw.Contains("24") || raw.Contains("999")) return "24K Has Altın";
        if (raw.Contains("22") || raw.Contains("916")) return "22K Altın";
        if (raw.Contains("18") || raw.Contains("750")) return "18K Altın";
        if (raw.Contains("14") || raw.Contains("585")) return "14K Altın";
        if (raw.Contains("8") || raw.Contains("333")) return "8K Altın";
        return $"{raw} Altın";
    }

    public static string BuildWorkmanshipLineName(string? karatText)
        => $"{BuildGoldLineName(karatText)} Takı İşçiliği";

    public static string BuildWorkmanshipCodeSuffix(string? karatText)
    {
        var raw = (karatText ?? string.Empty).Trim().ToUpperInvariant();
        if (raw.Contains("24") || raw.Contains("999")) return "ISCILIK-24K";
        if (raw.Contains("22") || raw.Contains("916")) return "ISCILIK-22K";
        if (raw.Contains("18") || raw.Contains("750")) return "ISCILIK-18K";
        if (raw.Contains("14") || raw.Contains("585")) return "ISCILIK-14K";
        if (raw.Contains("8") || raw.Contains("333")) return "ISCILIK-8K";
        return "ISCILIK";
    }

    private static decimal NormalizeRateRatio(decimal value)
    {
        if (value <= 0m) return 0m;
        if (value > 1m) return value / 100m;
        return value;
    }

    private static decimal NormalizeMilyemComponent(decimal value)
    {
        if (value <= 0m) return 0m;
        // Milyem/has bileşenleri per-gram oran olmalı (genelde 0-1 arası).
        // Daha büyük değerler yanlış alan eşlemesi olabilir, dengeyi bozmaması için yok say.
        if (value > 1m) return 0m;
        return value;
    }

    private static decimal Round2(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public readonly record struct JewelrySpecialBaseResult(
    decimal SafHasGram,
    decimal ToplamSatisAgirligiHas,
    decimal HasAltinGramFiyat,
    decimal AltinBedeli,
    decimal IscilikKdvDahil,
    decimal KdvMatrahi,
    decimal HesaplananKdv,
    decimal AltinBirimFiyat,
    decimal IscilikBirimFiyat,
    decimal KdvRateRatio);
