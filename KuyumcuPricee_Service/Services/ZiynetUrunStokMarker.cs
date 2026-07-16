using System.Globalization;

namespace KUYUMCU.Price_Service.Services;

internal static class ZiynetUrunStokMarker
{
    public const string Prefix = "[ZIYNET_URUN_STOK]";

    public sealed record Item(string Ad, string Tip, decimal Adet);

    public static string AppendDescription(string? description, IEnumerable<Item>? items)
    {
        var list = (items ?? Enumerable.Empty<Item>()).Where(x => x.Adet > 0m && !string.IsNullOrWhiteSpace(x.Ad)).ToList();
        if (list.Count == 0) return (description ?? "").Trim();

        var marker = BuildMarker(list);
        var baseDesc = (description ?? "").Trim();
        if (baseDesc.Contains(Prefix, StringComparison.OrdinalIgnoreCase))
            return baseDesc;
        return string.IsNullOrEmpty(baseDesc) ? marker : $"{baseDesc}\n{marker}";
    }

    public static string BuildMarker(IEnumerable<Item> items)
    {
        var payload = string.Join(";", items.Select(x =>
            $"{x.Ad.Trim()}|{(string.IsNullOrWhiteSpace(x.Tip) ? "Yeni" : x.Tip.Trim())}|{x.Adet.ToString("0.###", CultureInfo.InvariantCulture)}"));
        return $"{Prefix}{payload}";
    }

    public static List<Item> Parse(string? text)
    {
        var result = new List<Item>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var idx = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return result;

        var payload = text[(idx + Prefix.Length)..].Trim();
        var end = payload.IndexOf('\n');
        if (end >= 0) payload = payload[..end].Trim();

        foreach (var part in payload.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var seg = part.Split('|');
            if (seg.Length < 3) continue;
            if (!decimal.TryParse(seg[2].Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var adet) || adet <= 0m)
                continue;
            result.Add(new Item(
                seg[0].Trim(),
                string.IsNullOrWhiteSpace(seg[1]) ? "Yeni" : seg[1].Trim(),
                adet));
        }
        return result;
    }

    public static List<Item> FromReqItems(IEnumerable<ZiynetUrunStokReq>? items)
        => (items ?? Enumerable.Empty<ZiynetUrunStokReq>())
            .Where(x => x.Adet > 0m && !string.IsNullOrWhiteSpace(x.Ad))
            .Select(x => new Item(x.Ad.Trim(), string.IsNullOrWhiteSpace(x.Tip) ? "Yeni" : x.Tip.Trim(), x.Adet))
            .ToList();

    public static List<Item> MergeDistinct(IEnumerable<Item> items)
        => items
            .GroupBy(x => (
                Ad: x.Ad.Trim().ToUpperInvariant(),
                Tip: (string.IsNullOrWhiteSpace(x.Tip) ? "Yeni" : x.Tip.Trim()).ToUpperInvariant()))
            .Select(g => new Item(g.First().Ad, g.First().Tip, g.Sum(x => x.Adet)))
            .ToList();
}

public sealed record ZiynetUrunStokReq(string Ad, string Tip, decimal Adet);
