using System.Globalization;
using System.Text;
using System.Text.Json;
using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

public sealed class EInvoiceProfileSettings
{
    public decimal SpecialMatrahCraftedVatRatePercent { get; set; } = 20m;
    public decimal SpecialMatrahZiynetVatRatePercent { get; set; } = 20m;
    public decimal SalesInvoiceVatRatePercent { get; set; } = 20m;

    public bool AutoDraftEnabled { get; set; } = true;
    public string AutoDraftMatchMode { get; set; } = "ANY"; // ANY | ALL
    public List<string> AutoDraftAllowedPaymentMethods { get; set; } = new();
    public decimal? AutoDraftMinTotal { get; set; }
    public decimal? AutoDraftMaxTotal { get; set; }
    public List<WorkmanshipRuleSetting> WorkmanshipRules { get; set; } = new();
}

public sealed class WorkmanshipRuleSetting
{
    public string ProductType { get; set; } = EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted;
    public string? Karat { get; set; }
    public decimal MinTotal { get; set; }
    public decimal MaxTotal { get; set; }
    public decimal Percentage { get; set; }
}

public static class EInvoiceProfileSettingsCodec
{
    private const string Prefix = "v1";
    public const string WorkmanshipProductTypeCrafted = "Iscilikli";
    public const string WorkmanshipProductTypeZiynet = "Ziynet";
    public static readonly HashSet<string> AllowedKaratValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "24K", "22K", "18K", "14K", "8K"
    };
    public static readonly Dictionary<string, string> ZiynetProductMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GRAMALTIN"] = "Gram Altın(külçe)",
        ["HASALTIN"] = "Has altın",
        ["22AYARGR"] = "22 ayar (gr)",
        ["CEYREK"] = "Çeyrek",
        ["YARIM"] = "Yarım",
        ["TAM"] = "Tam",
        ["ATA"] = "Ata",
        ["ATA5"] = "Ata5",
        ["GREMSE"] = "Gremse"
    };

    public static EInvoiceProfileSettings Decode(string? encoded)
    {
        var settings = new EInvoiceProfileSettings();
        if (string.IsNullOrWhiteSpace(encoded))
            return settings;

        try
        {
            var parts = encoded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase))
                return settings;

            foreach (var segment in parts.Skip(1))
            {
                var idx = segment.IndexOf('=');
                if (idx <= 0 || idx >= segment.Length - 1) continue;
                var key = segment[..idx].Trim().ToLowerInvariant();
                var value = segment[(idx + 1)..].Trim();
                switch (key)
                {
                    case "sw":
                        settings.SpecialMatrahCraftedVatRatePercent = ParseDecimalOrDefault(value, 20m);
                        break;
                    case "zw":
                        settings.SpecialMatrahZiynetVatRatePercent = ParseDecimalOrDefault(value, 20m);
                        break;
                    case "sa":
                        settings.SalesInvoiceVatRatePercent = ParseDecimalOrDefault(value, 20m);
                        break;
                    case "ae":
                        settings.AutoDraftEnabled = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "mm":
                        settings.AutoDraftMatchMode = NormalizeMatchMode(value);
                        break;
                    case "pm":
                        settings.AutoDraftAllowedPaymentMethods = value
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(NormalizePaymentMethod)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        break;
                    case "mn":
                        settings.AutoDraftMinTotal = TryParseNullableDecimal(value);
                        break;
                    case "mx":
                        settings.AutoDraftMaxTotal = TryParseNullableDecimal(value);
                        break;
                    case "wr":
                        settings.WorkmanshipRules = DecodeWorkmanshipRules(value);
                        break;
                }
            }
        }
        catch
        {
            return new EInvoiceProfileSettings();
        }

        settings.SpecialMatrahCraftedVatRatePercent = NormalizeVatPercent(settings.SpecialMatrahCraftedVatRatePercent);
        settings.SpecialMatrahZiynetVatRatePercent = NormalizeVatPercent(settings.SpecialMatrahZiynetVatRatePercent);
        settings.SalesInvoiceVatRatePercent = NormalizeVatPercent(settings.SalesInvoiceVatRatePercent);
        settings.AutoDraftMatchMode = NormalizeMatchMode(settings.AutoDraftMatchMode);
        settings.WorkmanshipRules = NormalizeWorkmanshipRules(settings.WorkmanshipRules);
        return settings;
    }

    public static string Encode(EInvoiceProfileSettings settings)
    {
        var normalizedPayments = (settings.AutoDraftAllowedPaymentMethods ?? [])
            .Select(NormalizePaymentMethod)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var minText = settings.AutoDraftMinTotal.HasValue
            ? settings.AutoDraftMinTotal.Value.ToString("0.##", CultureInfo.InvariantCulture)
            : string.Empty;
        var maxText = settings.AutoDraftMaxTotal.HasValue
            ? settings.AutoDraftMaxTotal.Value.ToString("0.##", CultureInfo.InvariantCulture)
            : string.Empty;

        return string.Join(";",
            Prefix,
            $"sw={NormalizeVatPercent(settings.SpecialMatrahCraftedVatRatePercent).ToString("0.##", CultureInfo.InvariantCulture)}",
            $"zw={NormalizeVatPercent(settings.SpecialMatrahZiynetVatRatePercent).ToString("0.##", CultureInfo.InvariantCulture)}",
            $"sa={NormalizeVatPercent(settings.SalesInvoiceVatRatePercent).ToString("0.##", CultureInfo.InvariantCulture)}",
            $"ae={(settings.AutoDraftEnabled ? "1" : "0")}",
            $"mm={NormalizeMatchMode(settings.AutoDraftMatchMode)}",
            $"pm={string.Join(",", normalizedPayments)}",
            $"mn={minText}",
            $"mx={maxText}",
            $"wr={EncodeWorkmanshipRules(settings.WorkmanshipRules)}");
    }

    public static List<WorkmanshipRuleSetting> NormalizeWorkmanshipRules(IEnumerable<WorkmanshipRuleSetting>? rules)
    {
        var list = (rules ?? [])
            .Select(x => new WorkmanshipRuleSetting
            {
                ProductType = NormalizeWorkmanshipProductType(x.ProductType),
                Karat = NormalizeWorkmanshipSelector(x.ProductType, x.Karat),
                MinTotal = Math.Max(0m, Math.Round(x.MinTotal, 2, MidpointRounding.AwayFromZero)),
                MaxTotal = Math.Max(0m, Math.Round(x.MaxTotal, 2, MidpointRounding.AwayFromZero)),
                Percentage = NormalizeVatPercent(x.Percentage)
            })
            .Where(x => x.MaxTotal > 0m && x.MaxTotal >= x.MinTotal)
            .OrderBy(x => x.ProductType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.MinTotal)
            .ThenBy(x => x.MaxTotal)
            .ToList();

        return list;
    }

    public static WorkmanshipRuleSetting? ResolveWorkmanshipRule(
        IReadOnlyCollection<WorkmanshipRuleSetting>? rules,
        string? productType,
        string? selector,
        decimal comparisonValue)
    {
        if (rules is null || rules.Count == 0 || comparisonValue <= 0m)
            return null;

        var normalizedType = NormalizeWorkmanshipProductType(productType);
        var normalizedSelector = NormalizeWorkmanshipSelector(normalizedType, selector);
        var value = Math.Round(comparisonValue, 2, MidpointRounding.AwayFromZero);
        return rules
            .Where(x => string.Equals(NormalizeWorkmanshipProductType(x.ProductType), normalizedType, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(x.Karat) ||
                        string.Equals(NormalizeWorkmanshipSelector(normalizedType, x.Karat), normalizedSelector, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(x => value >= x.MinTotal && value <= x.MaxTotal);
    }

    public static string NormalizeWorkmanshipProductType(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        return text switch
        {
            "ZIYNET" or "ZIYNET/SARRAFIYE" => WorkmanshipProductTypeZiynet,
            _ => WorkmanshipProductTypeCrafted
        };
    }

    public static string? NormalizeWorkmanshipSelector(string? productType, string? value)
    {
        var type = NormalizeWorkmanshipProductType(productType);
        if (string.Equals(type, WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase))
            return NormalizeWorkmanshipZiynetProduct(value);
        return NormalizeWorkmanshipKarat(value);
    }

    public static string? NormalizeWorkmanshipKarat(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (AllowedKaratValues.Contains(text))
            return text;

        // "22 Ayar", "22AYAR", "22", "22K GOLD", "22K916" gibi varyasyonları normalize et.
        var compact = new string(text.Where(ch => !char.IsWhiteSpace(ch) && ch is not '-' and not '_' and not '/').ToArray());
        if (compact.StartsWith("24")) return "24K";
        if (compact.StartsWith("22")) return "22K";
        if (compact.StartsWith("18")) return "18K";
        if (compact.StartsWith("14")) return "14K";
        if (compact.StartsWith("8")) return "8K";

        if (text.Contains("24K") || text.Contains("24 AYAR")) return "24K";
        if (text.Contains("22K") || text.Contains("22 AYAR")) return "22K";
        if (text.Contains("18K") || text.Contains("18 AYAR")) return "18K";
        if (text.Contains("14K") || text.Contains("14 AYAR")) return "14K";
        if (text.Contains("8K") || text.Contains("8 AYAR")) return "8K";

        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("24")) return "24K";
        if (digits.StartsWith("22")) return "22K";
        if (digits.StartsWith("18")) return "18K";
        if (digits.StartsWith("14")) return "14K";
        if (digits.StartsWith("8")) return "8K";

        return digits switch
        {
            "24" => "24K",
            "22" => "22K",
            "18" => "18K",
            "14" => "14K",
            "8" => "8K",
            _ => null
        };
    }

    public static string? NormalizeWorkmanshipZiynetProduct(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (ZiynetProductMap.ContainsKey(text))
            return text;

        text = text
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .Replace("(", string.Empty)
            .Replace(")", string.Empty);

        return text switch
        {
            "GRAMALTIN" or "GRAMALTINKULCE" or "GRAMALTINKÜLÇE" => "GRAMALTIN",
            "HASALTIN" or "HAS" => "HASALTIN",
            "22AYARGR" or "22AYAR" => "22AYARGR",
            "CEYREK" or "ÇEYREK" => "CEYREK",
            "YARIM" => "YARIM",
            "TAM" => "TAM",
            "ATA" => "ATA",
            "ATA5" or "ATABESLI" or "ATABEŞLİ" => "ATA5",
            "GREMSE" => "GREMSE",
            _ => null
        };
    }

    public static string ToWorkmanshipSelectorDisplay(string? productType, string? selector)
    {
        var type = NormalizeWorkmanshipProductType(productType);
        if (!string.Equals(type, WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase))
            return NormalizeWorkmanshipKarat(selector) ?? "22K";

        var key = NormalizeWorkmanshipZiynetProduct(selector) ?? "CEYREK";
        return ZiynetProductMap.TryGetValue(key, out var display) ? display : key;
    }

    public static bool ShouldCreateAutoDraft(
        EInvoiceProfile? profile,
        IReadOnlyCollection<string> salePaymentMethods,
        decimal saleGrandTotal)
    {
        var settings = Decode(profile?.IntegratorCompanyCode);
        if (!settings.AutoDraftEnabled)
            return false;

        if (settings.AutoDraftMinTotal.HasValue && saleGrandTotal < settings.AutoDraftMinTotal.Value)
            return false;
        if (settings.AutoDraftMaxTotal.HasValue && settings.AutoDraftMaxTotal.Value > 0m && saleGrandTotal > settings.AutoDraftMaxTotal.Value)
            return false;

        if (settings.AutoDraftAllowedPaymentMethods.Count == 0)
            return true;

        var selected = new HashSet<string>(
            settings.AutoDraftAllowedPaymentMethods.Select(NormalizePaymentMethod),
            StringComparer.OrdinalIgnoreCase);
        var sale = salePaymentMethods.Select(NormalizePaymentMethod).ToList();
        if (sale.Count == 0) return false;

        return NormalizeMatchMode(settings.AutoDraftMatchMode) switch
        {
            "ALL" => sale.All(selected.Contains),
            _ => sale.Any(selected.Contains)
        };
    }

    public static string NormalizePaymentMethod(string? value)
    {
        var method = (value ?? string.Empty).Trim().ToUpperInvariant();
        return method switch
        {
            "NAKIT" => "Nakit",
            "KART" or "KREDI KARTI" or "KREDİ KARTI" => "Kart",
            "IBAN" or "HAVALE" or "EFT" => "IBAN",
            "VERESIYE" or "VERESİYE" => "Veresiye",
            "TAKAS" => "Takas",
            "USD" => "USD",
            "EURO" or "EUR" => "Euro",
            "GBP" or "STERLIN" or "STERLİN" => "GBP",
            "TEDARIKCIVERESIYE" or "TEDARIKCI_VERESIYE" or "TEDARIKÇIVERESIYE" => "TedarikciVeresiye",
            _ => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()
        };
    }

    public static decimal NormalizeVatPercent(decimal value)
    {
        if (value < 0m) return 0m;
        if (value > 100m) return 100m;
        return Math.Round(value, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal VatPercentToRatio(decimal value)
        => NormalizeVatPercent(value) / 100m;

    private static string NormalizeMatchMode(string? value)
    {
        var mode = (value ?? string.Empty).Trim().ToUpperInvariant();
        return mode switch
        {
            "ALL" or "TUMU" or "TÜMÜ" => "ALL",
            _ => "ANY"
        };
    }

    private static decimal ParseDecimalOrDefault(string? value, decimal fallback)
        => TryParseNullableDecimal(value) ?? fallback;

    private static string EncodeWorkmanshipRules(IEnumerable<WorkmanshipRuleSetting>? rules)
    {
        var normalized = NormalizeWorkmanshipRules(rules);
        if (normalized.Count == 0)
            return string.Empty;
        var compact = normalized
            .Select(x =>
            {
                var normalizedType = NormalizeWorkmanshipProductType(x.ProductType);
                var pt = string.Equals(normalizedType, WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase) ? "Z" : "C";
                var selector = NormalizeWorkmanshipSelector(normalizedType, x.Karat)
                               ?? (string.Equals(normalizedType, WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase) ? "CEYREK" : "22K");
                var min = x.MinTotal.ToString("0.##", CultureInfo.InvariantCulture);
                var max = x.MaxTotal.ToString("0.##", CultureInfo.InvariantCulture);
                var pct = x.Percentage.ToString("0.##", CultureInfo.InvariantCulture);
                return $"{pt}|{selector}|{min}|{max}|{pct}";
            });
        return string.Join("^", compact);
    }

    private static List<WorkmanshipRuleSetting> DecodeWorkmanshipRules(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            return [];

        var compactParsed = DecodeWorkmanshipRulesCompact(encoded);
        if (compactParsed.Count > 0)
            return NormalizeWorkmanshipRules(compactParsed);

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parsed = JsonSerializer.Deserialize<List<WorkmanshipRuleSetting>>(json);
            return NormalizeWorkmanshipRules(parsed ?? []);
        }
        catch
        {
            return [];
        }
    }

    private static List<WorkmanshipRuleSetting> DecodeWorkmanshipRulesCompact(string encoded)
    {
        var list = new List<WorkmanshipRuleSetting>();
        var rules = encoded.Split('^', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rule in rules)
        {
            var parts = rule.Split('|', StringSplitOptions.None);
            if (parts.Length != 5)
                return [];
            var pt = (parts[0] ?? string.Empty).Trim().ToUpperInvariant();
            var productType = pt == "Z" ? WorkmanshipProductTypeZiynet : WorkmanshipProductTypeCrafted;
            var selector = NormalizeWorkmanshipSelector(productType, parts[1])
                           ?? (string.Equals(productType, WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase) ? "CEYREK" : "22K");
            if (!decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var min))
                return [];
            if (!decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var max))
                return [];
            if (!decimal.TryParse(parts[4], NumberStyles.Number, CultureInfo.InvariantCulture, out var pct))
                return [];

            list.Add(new WorkmanshipRuleSetting
            {
                ProductType = productType,
                Karat = selector,
                MinTotal = min,
                MaxTotal = max,
                Percentage = pct
            });
        }

        return list;
    }

    private static decimal? TryParseNullableDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var inv))
            return inv;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("tr-TR"), out var tr))
            return tr;
        return null;
    }
}
