using System.Globalization;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

internal static class DepoStokGramHelper
{
    public static string NormalizeAyar(string? karat)
    {
        if (string.IsNullOrWhiteSpace(karat)) return "";
        var k = karat.Trim().ToUpperInvariant().Replace("K", "").Replace("AYAR", "").Replace("(", "").Replace(")", "").Replace(" ", "").Trim();
        if (decimal.TryParse(k, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v >= 14 && v <= 24)
            return $"{(int)v}K";
        if (karat.Contains("14") || karat.Contains("585")) return "14K";
        if (karat.Contains("18") || karat.Contains("750")) return "18K";
        if (karat.Contains("22") || karat.Contains("916")) return "22K";
        return karat.Trim().Length > 0 ? karat.Trim() : "";
    }

    public static async Task<(bool ok, string? error)> TryApplyBarcodedProductSoldAsync(
        AppDbContext db, Guid tenantId, Guid branchId, ProductItem pItem, CancellationToken ct)
    {
        // Hurda alış satırından vitrine üretilen tekil: gramaj ScrapStock / hurda defterinde düşüldü;
        // depo (toptancı hammadde) BarcodedGram havuzuna hiç girmedi. Satışta depodan tekrar düşmek hata verirdi.
        if (pItem.SourcePurchaseItemId.HasValue)
            return (true, null);

        var ayar = NormalizeAyar(pItem.Karat);
        if (string.IsNullOrEmpty(ayar) || pItem.Weight <= 0) return (true, null);
        var depo = await db.DepoStoklar.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.BranchId == branchId && x.Ayar == ayar && !x.IsDeleted, ct);
        if (depo is null)
            return (false, $"Depo kaydı bulunamadı (ayar {ayar}).");
        if (!depo.OnBarcodedProductSold(pItem.Weight))
            return (false, $"Depoda yeterli barkodlu gram yok (ayar {ayar}).");

        var (havuzOk, havuzErr) = await TryApplyTripleHavuzSoldAsync(db, tenantId, branchId, pItem, ayar, ct);
        if (!havuzOk) return (false, havuzErr);
        return (true, null);
    }

    public static async Task<(bool ok, string? error)> TryUndoBarcodedProductSoldAsync(
        AppDbContext db, Guid tenantId, Guid branchId, ProductItem pItem, CancellationToken ct)
    {
        if (pItem.SourcePurchaseItemId.HasValue)
            return (true, null);

        var ayar = NormalizeAyar(pItem.Karat);
        if (string.IsNullOrEmpty(ayar) || pItem.Weight <= 0) return (true, null);
        var depo = await db.DepoStoklar.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.BranchId == branchId && x.Ayar == ayar && !x.IsDeleted, ct);
        if (depo is null) return (true, null);
        if (!depo.OnBarcodedProductReturned(pItem.Weight))
            return (false, $"Depo iadesi yapılamadı (ayar {ayar}).");

        var (havuzOk, havuzErr) = await TryUndoTripleHavuzSoldAsync(db, tenantId, branchId, pItem, ayar, ct);
        if (!havuzOk) return (false, havuzErr);
        return (true, null);
    }

    private static async Task<(bool ok, string? error)> TryApplyTripleHavuzSoldAsync(
        AppDbContext db, Guid tenantId, Guid branchId, ProductItem pItem, string ayar, CancellationToken ct)
    {
        var product = await db.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == pItem.ProductId && x.TenantId == tenantId, ct);
        if (product is null) return (true, null);

        var malN = DepoStokTripleHelper.NormalizeMal(product.MalTanim);
        var firmaN = DepoStokTripleHelper.NormalizeFirma(product.DepoTedarikciFirma);

        // Önce tam üçlü eşleşme ile düşmeyi dene.
        if (!string.IsNullOrWhiteSpace(malN) && !string.IsNullOrWhiteSpace(firmaN) && product.DepoBirimMaliyet.HasValue)
        {
            var birim = DepoStokTripleHelper.RoundBirimMaliyet(product.DepoBirimMaliyet.Value);
            var havuz = await db.DepoStokHavuzlar.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                !x.IsDeleted &&
                x.Ayar == ayar &&
                x.MalTanimNorm == malN &&
                x.TedarikciFirmaNorm == firmaN &&
                x.BirimMaliyet == birim, ct);
            if (havuz != null)
            {
                if (!havuz.OnBarcodedProductSold(pItem.Weight))
                    return (false, $"Depo havuz barkodlu gram yetersiz (ayar:{ayar}, mal:{malN}).");
                return (true, null);
            }
        }

        // Fallback: Eski/eksik kartlarda üçlü alanlar yoksa ayar bazında barkodlu havuzdan düş.
        var rows = await db.DepoStokHavuzlar
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.Ayar == ayar)
            .OrderByDescending(x => x.BarcodedGram)
            .ToListAsync(ct);
        var remaining = pItem.Weight;
        foreach (var row in rows)
        {
            if (remaining <= 0.0001m) break;
            var take = Math.Min(row.BarcodedGram, remaining);
            if (take <= 0) continue;
            if (!row.OnBarcodedProductSold(take))
                return (false, $"Depo havuz barkodlu gram düşümü başarısız (ayar:{ayar}).");
            remaining -= take;
        }
        if (remaining > 0.0001m)
            return (false, $"Depo havuz barkodlu gram yetersiz (ayar:{ayar}, kalan:{remaining:0.###}g).");
        return (true, null);
    }

    private static async Task<(bool ok, string? error)> TryUndoTripleHavuzSoldAsync(
        AppDbContext db, Guid tenantId, Guid branchId, ProductItem pItem, string ayar, CancellationToken ct)
    {
        var product = await db.Products
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == pItem.ProductId && x.TenantId == tenantId, ct);
        if (product is null) return (true, null);

        var malN = DepoStokTripleHelper.NormalizeMal(product.MalTanim);
        var firmaN = DepoStokTripleHelper.NormalizeFirma(product.DepoTedarikciFirma);
        if (string.IsNullOrWhiteSpace(malN) || string.IsNullOrWhiteSpace(firmaN) || !product.DepoBirimMaliyet.HasValue)
            return (true, null);
        var birim = DepoStokTripleHelper.RoundBirimMaliyet(product.DepoBirimMaliyet.Value);

        var havuz = await db.DepoStokHavuzlar.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId &&
            x.BranchId == branchId &&
            !x.IsDeleted &&
            x.Ayar == ayar &&
            x.MalTanimNorm == malN &&
            x.TedarikciFirmaNorm == firmaN &&
            x.BirimMaliyet == birim, ct);
        if (havuz is null) return (true, null);
        if (!havuz.OnBarcodedProductReturned(pItem.Weight))
            return (false, $"Depo havuz iade güncellemesi yapılamadı (ayar:{ayar}, mal:{malN}).");
        return (true, null);
    }
}
