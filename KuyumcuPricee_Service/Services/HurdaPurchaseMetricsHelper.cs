using System.Globalization;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

/// <summary>api/scrap/hurda-metrics yanıtı — ScrapController ile aynı şema.</summary>
public sealed class HurdaPurchaseLineMetricDto
{
    public Guid PurchaseItemId { get; set; }
    public decimal OriginalGram { get; set; }
    public decimal BarkodluGram { get; set; }
    public decimal BarkodsuzGram { get; set; }
}

/// <summary>
/// StokDepo hurda sekmesi (müşteri alış + hurda-metrics) ile Kasadaki Hurda HAS toplamını aynı kaynaktan üretir.
/// </summary>
public static class HurdaPurchaseMetricsHelper
{
    /// <summary>ScrapController.ComputeHurdaPurchaseLineMetricsAsync ile birebir.</summary>
    public static async Task<List<HurdaPurchaseLineMetricDto>> ComputeHurdaPurchaseLineMetricsAsync(
        AppDbContext db,
        Guid tid,
        Guid branchId,
        List<Guid> idList,
        CancellationToken ct)
    {
        if (branchId == Guid.Empty || idList.Count == 0)
            return new List<HurdaPurchaseLineMetricDto>();

        var distinctIds = idList.Distinct().ToList();
        var tenantItems = new List<PurchaseItem>();
        foreach (var id in distinctIds)
        {
            var pi = await db.PurchaseItems.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && !x.IsDeleted, ct);
            if (pi is not null) tenantItems.Add(pi);
        }

        if (tenantItems.Count == 0)
            return new List<HurdaPurchaseLineMetricDto>();

        var branchPurchaseSet = new HashSet<Guid>();
        foreach (var pid in tenantItems.Select(x => x.PurchaseId).Distinct())
        {
            var inBranch = await db.Purchases.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(p => p.Id == pid && p.TenantId == tid && !p.IsDeleted && p.BranchId == branchId, ct);
            if (inBranch) branchPurchaseSet.Add(pid);
        }

        var branchItems = tenantItems.Where(pi => branchPurchaseSet.Contains(pi.PurchaseId)).ToList();
        var allowedIds = branchItems.Select(pi => pi.Id).ToList();

        var acc = new Dictionary<Guid, (decimal All, decimal InStock)>();
        foreach (var lineId in allowedIds)
        {
            var rows = await db.ProductItems.AsNoTracking().IgnoreQueryFilters()
                .Where(pi => pi.SourcePurchaseItemId == lineId && pi.TenantId == tid && !pi.IsDeleted)
                .Select(pi => new { pi.Weight, pi.IsInStock })
                .ToListAsync(ct);
            if (rows.Count == 0) continue;
            var allSum = rows.Sum(r => r.Weight);
            var inStockSum = rows.Where(r => r.IsInStock).Sum(r => r.Weight);
            acc[lineId] = (allSum, inStockSum);
        }

        var result = new List<HurdaPurchaseLineMetricDto>();
        foreach (var pi in branchItems)
        {
            var orig = Math.Round(pi.Quantity, 4, MidpointRounding.AwayFromZero);
            var allW = 0m;
            var inStockW = 0m;
            if (acc.TryGetValue(pi.Id, out var t))
            {
                allW = Math.Round(t.All, 4, MidpointRounding.AwayFromZero);
                inStockW = Math.Round(t.InStock, 4, MidpointRounding.AwayFromZero);
            }

            var barkodsuzVal = Math.Round(orig - allW, 4, MidpointRounding.AwayFromZero);
            result.Add(new HurdaPurchaseLineMetricDto
            {
                PurchaseItemId = pi.Id,
                OriginalGram = orig,
                BarkodluGram = Math.Max(0, inStockW),
                BarkodsuzGram = Math.Max(0, barkodsuzVal),
            });
        }

        return result;
    }

    /// <summary>
    /// Kasa ScrapHas: StokDepoViewModel.YukleHurdaStokAsync ile aynı — hurda sekmesindeki tüm satırların
    /// metrik sonrası ToplamHasKarsiligi toplamı. (Gümüş satırı varsa grid ile aynı şekilde dahildir.)
    /// </summary>
    public static async Task<(decimal HurdaHasToplam, decimal GumusHurdaGram)> ComputeHurdaStokForCashboxAsync(
        AppDbContext db,
        Guid tenantId,
        Guid? branchId,
        CancellationToken ct)
    {
        var branchIds = new List<Guid>();
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            branchIds.Add(branchId.Value);
        else
        {
            branchIds = await db.Branches.AsNoTracking()
                .Where(b => b.TenantId == tenantId && !b.IsDeleted)
                .Select(b => b.Id)
                .ToListAsync(ct);
        }

        decimal hurdaHas = 0m;
        decimal gumusGram = 0m;

        foreach (var bid in branchIds)
        {
            var raw = await (
                from pi in db.PurchaseItems.AsNoTracking()
                join p in db.Purchases.AsNoTracking() on pi.PurchaseId equals p.Id
                where pi.TenantId == tenantId && !pi.IsDeleted && !p.IsDeleted && p.TenantId == tenantId
                      && p.PurchaseType == PurchaseType.Musteri && p.BranchId == bid
                select pi).ToListAsync(ct);

            var hurdaLines = raw.Where(IsHurdaPurchaseItemEntity).ToList();
            if (hurdaLines.Count == 0) continue;

            var ids = hurdaLines.Select(x => x.Id).Distinct().ToList();
            var metrics = await ComputeHurdaPurchaseLineMetricsAsync(db, tenantId, bid, ids, ct);
            var dict = metrics.ToDictionary(m => m.PurchaseItemId, m => m);

            foreach (var pi in hurdaLines)
            {
                var gram = pi.Quantity;
                var milyem = ResolveMilyemFromKaratOrAyarText(pi.Karat);
                var toplamHas = Math.Round(gram * milyem, 3, MidpointRounding.AwayFromZero);
                if (!dict.TryGetValue(pi.Id, out var m))
                {
                    hurdaHas += toplamHas;
                    if (IsSilverHurdaPurchaseItemEntity(pi))
                        gumusGram += gram;
                    continue;
                }

                var yeniToplam = Math.Round(m.BarkodluGram + m.BarkodsuzGram, 4, MidpointRounding.AwayFromZero);
                var origRef = m.OriginalGram;
                if (origRef > 0.0001m)
                {
                    var oran = Math.Min(1m, Math.Max(0m, yeniToplam / origRef));
                    toplamHas = Math.Round(toplamHas * oran, 3, MidpointRounding.AwayFromZero);
                }
                hurdaHas += toplamHas;
                if (IsSilverHurdaPurchaseItemEntity(pi))
                    gumusGram += yeniToplam;
            }
        }

        return (hurdaHas, gumusGram);
    }

    private static bool IsHurdaPurchaseItemEntity(PurchaseItem it)
    {
        if (it.Kind == ItemKind.Scrap) return true;
        if (it.OdenecekToplamHas.HasValue && it.OdenecekToplamHas.Value > 0) return true;

        var karatRaw = (it.Karat ?? "").Trim();
        if (decimal.TryParse(karatRaw.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var karatAsDecimal) &&
            karatAsDecimal > 0m && karatAsDecimal <= 1m)
            return true;

        var ad = (it.ProductName ?? "").Trim();
        return ad.StartsWith("Hurda", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSilverHurdaPurchaseItemEntity(PurchaseItem it)
    {
        if (it.Kind == ItemKind.Silver) return true;
        var ad = (it.ProductName ?? "").Trim();
        if (IsSilverText(ad)) return true;
        var kategori = (it.Category ?? "").Trim();
        return IsSilverText(kategori) || IsSilverText(it.Karat);
    }

    private static bool IsSilverText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var n = text.Trim()
            .ToLowerInvariant()
            .Replace('ı', 'i')
            .Replace('İ', 'i')
            .Replace('ş', 's')
            .Replace('ğ', 'g')
            .Replace('ü', 'u')
            .Replace('ö', 'o')
            .Replace('ç', 'c');
        return n.Contains("gumus") || n.Contains("silver");
    }

    /// <summary>StokDepoViewModel.ResolveMilyemFromKaratOrAyarText ile aynı.</summary>
    private static decimal ResolveMilyemFromKaratOrAyarText(string? raw)
    {
        var txt = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt))
            return 585m / 1000m;

        if (decimal.TryParse(txt.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
        {
            if (d > 1m) d /= 1000m;
            if (d > 0m && d <= 1m) return d;
        }

        return ResolveMilyemFromAyarText(txt);
    }

    private static string NormalizeAyarKey(string? ayarRaw)
    {
        var a = (ayarRaw ?? "").Trim().ToUpperInvariant();
        if (a.Contains("14") || a.Contains("585")) return "14K";
        if (a.Contains("18") || a.Contains("750")) return "18K";
        if (a.Contains("22") || a.Contains("916")) return "22K";
        if (a.Contains("24") || a.Contains("999") || a.Contains("1000")) return "24K";
        return string.IsNullOrWhiteSpace(a) ? "DİĞER" : a;
    }

    private static decimal ResolveMilyemFromAyarText(string? ayarRaw)
    {
        var a = NormalizeAyarKey(ayarRaw);
        return a switch
        {
            "14K" => 585m / 1000m,
            "18K" => 750m / 1000m,
            "22K" => 916m / 1000m,
            "24K" => 1m,
            _ => 916m / 1000m
        };
    }
}
