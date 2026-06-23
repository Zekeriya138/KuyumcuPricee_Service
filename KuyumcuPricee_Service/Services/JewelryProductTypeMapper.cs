using kuyumcu_domain.Entities;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Services;

public interface IJewelryProductTypeMapper
{
    string Resolve(ItemKind kind, string category, string karat, string? productName);
}

public sealed class JewelryProductTypeMapper : IJewelryProductTypeMapper
{
    private readonly IReadOnlyList<ProductTypeMapRow> _rows;

    public JewelryProductTypeMapper(IConfiguration cfg)
    {
        _rows = ProductTypeMapRow.FromConfiguration(cfg);
    }

    public string Resolve(ItemKind kind, string category, string karat, string? productName)
    {
        var normalizedCategory = (category ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedKarat = (karat ?? string.Empty).Trim().ToUpperInvariant();
        var normalizedName = (productName ?? string.Empty).Trim().ToUpperInvariant();

        foreach (var row in _rows)
        {
            if (row.RequiredKind.HasValue && row.RequiredKind.Value != kind)
                continue;

            if (row.CategoryKeywords.Count > 0 &&
                !row.CategoryKeywords.Any(k => normalizedCategory.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (row.KaratKeywords.Count > 0 &&
                !row.KaratKeywords.Any(k => normalizedKarat.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (row.NameKeywords.Count > 0 &&
                !row.NameKeywords.Any(k => normalizedName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            return row.ProductType;
        }

        return "DİĞER";
    }

    private sealed record ProductTypeMapRow(
        string ProductType,
        ItemKind? RequiredKind,
        IReadOnlyList<string> CategoryKeywords,
        IReadOnlyList<string> KaratKeywords,
        IReadOnlyList<string> NameKeywords)
    {
        public static IReadOnlyList<ProductTypeMapRow> FromConfiguration(IConfiguration cfg)
        {
            var section = cfg.GetSection("EInvoice:ProductTypeMappings");
            var rows = section.GetChildren().Select(c => new ProductTypeMapRow(
                ProductType: c.GetValue<string>("ProductType") ?? "DİĞER",
                RequiredKind: ParseItemKind(c.GetValue<string>("Kind")),
                CategoryKeywords: (c.GetSection("CategoryKeywords").Get<string[]>() ?? Array.Empty<string>())
                    .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToList(),
                KaratKeywords: (c.GetSection("KaratKeywords").Get<string[]>() ?? Array.Empty<string>())
                    .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToList(),
                NameKeywords: (c.GetSection("NameKeywords").Get<string[]>() ?? Array.Empty<string>())
                    .Select(x => (x ?? string.Empty).Trim().ToUpperInvariant()).Where(x => x.Length > 0).ToList()))
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductType))
                .ToList();

            if (rows.Count > 0)
                return rows;

            return
            [
                new("DARPHANE ÜRÜNLERİ", null, ["DARPHANE"], [], []),
                new("ZİYNET", ItemKind.Ziynet, ["ZİYNET","ZIYNET","ÇEYREK","YARIM","TAM","ATA"], [], []),
                new("PIRLANTA", null, ["PIRLANTA","ELMAS","TAŞ","TAS"], [], ["PIRLANTA","ELMAS"]),
                new("GÜMÜŞ", ItemKind.Silver, ["GÜMÜŞ","GUMUS"], [], []),
                new("HAS ALTIN", ItemKind.GramGold, ["HAS"], ["HAS","24"], ["HAS"]),
                new("22 AYAR", null, [], ["22"], []),
                new("18 AYAR", null, [], ["18"], []),
                new("14 AYAR", null, [], ["14"], []),
                new("8 AYAR", null, [], ["8"], [])
            ];
        }

        private static ItemKind? ParseItemKind(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return Enum.TryParse<ItemKind>(value, ignoreCase: true, out var kind) ? kind : null;
        }
    }
}
