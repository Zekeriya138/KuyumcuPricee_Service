using kuyumcu_domain.Entities;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Services;

public interface IJewelryTaxCalculator
{
    JewelryTaxResult Calculate(JewelryTaxContext context);
}

public sealed record JewelryTaxContext(
    ItemKind Kind,
    string ProductType,
    string Category,
    string Karat,
    bool IsStoneProduct,
    decimal LineBaseAmount,
    decimal DeclaredTaxRate,
    decimal WorkmanshipHasAmount,
    string? ProductName);

public sealed record JewelryTaxResult(
    decimal AppliedTaxRate,
    decimal WithholdingRate,
    string? ExemptionCode,
    string? ExemptionReason);

public sealed class JewelryTaxCalculator : IJewelryTaxCalculator
{
    private readonly IConfiguration _cfg;

    public JewelryTaxCalculator(IConfiguration cfg)
    {
        _cfg = cfg;
    }

    public JewelryTaxResult Calculate(JewelryTaxContext context)
    {
        var o = TaxRulesOptions.FromConfiguration(_cfg);
        var normalizedCategory = (context.Category ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedKarat = (context.Karat ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedProductType = (context.ProductType ?? string.Empty).Trim().ToUpperInvariant();
        var name = (context.ProductName ?? string.Empty).Trim().ToUpperInvariant();
        var declaredRate = NormalizeRate(context.DeclaredTaxRate);

        var isHasGold = context.Kind == ItemKind.GramGold ||
                        normalizedKarat.Contains("HAS", StringComparison.OrdinalIgnoreCase) ||
                        normalizedCategory.Contains("HAS", StringComparison.OrdinalIgnoreCase);
        var isSilver = context.Kind == ItemKind.Silver ||
                       normalizedCategory.Contains("GUMUS", StringComparison.OrdinalIgnoreCase) ||
                       normalizedCategory.Contains("GÜMÜŞ", StringComparison.OrdinalIgnoreCase);
        var isStone = context.IsStoneProduct ||
                      normalizedCategory.Contains("PIRLANTA", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("PIRLANTA", StringComparison.OrdinalIgnoreCase);
        var isWithholding = o.EnableWithholding &&
                            o.WithholdingCategoryKeywords.Any(k => normalizedCategory.Contains(k, StringComparison.OrdinalIgnoreCase));

        var appliedRate = declaredRate > 0
            ? declaredRate
            : ResolveMappedRate(normalizedProductType, normalizedKarat, o);
        string? exemptionCode = null;
        string? exemptionReason = null;

        if (isHasGold)
        {
            appliedRate = 0m;
            exemptionCode = o.HasGoldExemptionCode;
            exemptionReason = o.HasGoldExemptionReason;

            if (o.ApplyWorkmanshipVatOnHasGold && context.WorkmanshipHasAmount > 0)
            {
                appliedRate = o.WorkmanshipVatRate;
                exemptionCode = null;
                exemptionReason = null;
            }
        }
        else if (isSilver)
        {
            appliedRate = o.SilverVatRate;
        }
        else if (isStone)
        {
            appliedRate = o.StoneVatRate;
        }

        var withholdingRate = isWithholding
            ? o.WithholdingRates.TryGetValue(normalizedProductType, out var mappedWithholding) ? mappedWithholding : o.WithholdingRate
            : 0m;
        return new JewelryTaxResult(
            AppliedTaxRate: appliedRate,
            WithholdingRate: withholdingRate,
            ExemptionCode: exemptionCode,
            ExemptionReason: exemptionReason);
    }

    private static decimal ResolveMappedRate(string productType, string karat, TaxRulesOptions o)
    {
        if (o.ProductTypeVatRates.TryGetValue(productType, out var byProductType))
            return byProductType;

        var karatKey = NormalizeKaratKey(karat);
        if (o.KaratVatRates.TryGetValue(karatKey, out var byKarat))
            return byKarat;

        return o.DefaultVatRate;
    }

    private static string NormalizeKaratKey(string? karat)
    {
        var s = (karat ?? string.Empty).ToUpperInvariant();
        if (s.Contains("22")) return "22";
        if (s.Contains("18")) return "18";
        if (s.Contains("14")) return "14";
        if (s.Contains("8")) return "8";
        if (s.Contains("24") || s.Contains("HAS")) return "24";
        return s;
    }

    private static decimal NormalizeRate(decimal value)
    {
        if (value <= 0) return 0m;
        return value > 1m ? value / 100m : value;
    }

    private sealed record TaxRulesOptions(
        decimal DefaultVatRate,
        decimal SilverVatRate,
        decimal StoneVatRate,
        decimal WorkmanshipVatRate,
        bool ApplyWorkmanshipVatOnHasGold,
        bool EnableWithholding,
        decimal WithholdingRate,
        IReadOnlyDictionary<string, decimal> ProductTypeVatRates,
        IReadOnlyDictionary<string, decimal> KaratVatRates,
        IReadOnlyDictionary<string, decimal> WithholdingRates,
        string HasGoldExemptionCode,
        string HasGoldExemptionReason,
        IReadOnlyList<string> WithholdingCategoryKeywords)
    {
        public static TaxRulesOptions FromConfiguration(IConfiguration cfg)
        {
            var s = cfg.GetSection("EInvoice:TaxRules");
            var keywords = s.GetSection("WithholdingCategoryKeywords").Get<string[]>() ?? ["TEVKIFAT"];
            var productTypeVatRates = ReadRateMap(s.GetSection("ProductTypeVatRates"));
            var karatVatRates = ReadRateMap(s.GetSection("KaratVatRates"));
            var withholdingRates = ReadRateMap(s.GetSection("WithholdingRates"));

            return new TaxRulesOptions(
                DefaultVatRate: NormalizeRate(s.GetValue<decimal?>("DefaultVatRate") ?? 0.20m),
                SilverVatRate: NormalizeRate(s.GetValue<decimal?>("SilverVatRate") ?? 0.20m),
                StoneVatRate: NormalizeRate(s.GetValue<decimal?>("StoneVatRate") ?? 0.20m),
                WorkmanshipVatRate: NormalizeRate(s.GetValue<decimal?>("WorkmanshipVatRate") ?? 0.20m),
                ApplyWorkmanshipVatOnHasGold: s.GetValue<bool?>("ApplyWorkmanshipVatOnHasGold") ?? false,
                EnableWithholding: s.GetValue<bool?>("EnableWithholding") ?? false,
                WithholdingRate: NormalizeRate(s.GetValue<decimal?>("WithholdingRate") ?? 0m),
                ProductTypeVatRates: productTypeVatRates,
                KaratVatRates: karatVatRates,
                WithholdingRates: withholdingRates,
                HasGoldExemptionCode: s.GetValue<string?>("HasGoldExemptionCode") ?? "351",
                HasGoldExemptionReason: s.GetValue<string?>("HasGoldExemptionReason") ?? "Has altın teslimi KDV istisnası",
                WithholdingCategoryKeywords: keywords.Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToList());
        }

        private static IReadOnlyDictionary<string, decimal> ReadRateMap(IConfigurationSection section)
        {
            var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in section.GetChildren())
            {
                if (string.IsNullOrWhiteSpace(child.Key)) continue;
                if (!decimal.TryParse(child.Value, out var raw)) continue;
                map[child.Key.Trim().ToUpperInvariant()] = NormalizeRate(raw);
            }
            return map;
        }
    }
}
