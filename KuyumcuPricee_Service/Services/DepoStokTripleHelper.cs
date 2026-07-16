using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

/// <summary>Mal + Tedarikçi + Birim maliyet normalizasyonu (WinForms DepoStokSatirYukleyici ile uyumlu).</summary>
public static class DepoStokTripleHelper
{
    /// <summary>Alış ve depo API ile aynı ayar kodu (PurchasesController.NormalizeAyar ile birebir).</summary>
    public static string NormalizeAyarKarat(string? karat)
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

    public static string NormalizeMal(string? mal) => (mal ?? "").Trim().ToUpperInvariant();

    public static string NormalizeFirma(string? firma) => (firma ?? "").Trim().ToUpperInvariant();

    /// <summary>Türkçe/ASCII farklarında eşleşme için (Ç→C, İ→I vb.).</summary>
    public static string FoldLookupKey(string? value)
    {
        var t = (value ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0) return "";
        return t
            .Replace('Ç', 'C').Replace('Ğ', 'G').Replace('İ', 'I').Replace('I', 'I')
            .Replace('Ö', 'O').Replace('Ş', 'S').Replace('Ü', 'U');
    }

    public static decimal RoundBirimMaliyet(decimal birim) =>
        Math.Round(birim, 6, MidpointRounding.AwayFromZero);

    public static string BirimMaliyetKey(decimal birim) =>
        RoundBirimMaliyet(birim).ToString(CultureInfo.InvariantCulture);

    /// <summary>Alış kalemi → mal tanımı (client MalTanimCozumle ile aynı mantık).</summary>
    public static string MalTanimFromPurchaseItem(PurchaseItem it, string ayarRaw)
    {
        if (!string.IsNullOrWhiteSpace(it.Category))
            return (it.Category ?? "").Trim();
        var pn = it.ProductName ?? "";
        if (pn.Contains('('))
        {
            var start = pn.IndexOf('(');
            var end = pn.IndexOf(')');
            if (start >= 0 && end > start)
                return pn.Substring(start + 1, end - start - 1).Trim();
        }
        var prefix = "Hammadde " + ayarRaw;
        if (pn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return pn.Substring(prefix.Length).Trim().Trim(' ', '(', ')');
        return "";
    }

    public static async Task AddOrIncrementHavuzAsync(
        AppDbContext db,
        Guid tenantId,
        Guid branchId,
        string ayarNormalized,
        string malRaw,
        string partnerName,
        decimal birimMaliyet,
        decimal gram,
        CancellationToken ct)
    {
        if (gram <= 0 || string.IsNullOrEmpty(ayarNormalized)) return;
        var malN = NormalizeMal(malRaw);
        var firmaN = NormalizeFirma(partnerName);
        if (string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN)) return;

        var birim = RoundBirimMaliyet(birimMaliyet);
        var row = db.DepoStokHavuzlar.Local.FirstOrDefault(x =>
            x.TenantId == tenantId &&
            x.BranchId == branchId &&
            x.Ayar == ayarNormalized &&
            x.MalTanimNorm == malN &&
            x.TedarikciFirmaNorm == firmaN &&
            x.BirimMaliyet == birim);

        if (row is null)
        {
            row = await db.DepoStokHavuzlar
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId && x.BranchId == branchId
                && x.Ayar == ayarNormalized && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN
                && x.BirimMaliyet == birim, ct);
        }

        if (row is null)
        {
            row = new DepoStokHavuz
            {
                TenantId = tenantId,
                BranchId = branchId,
                Ayar = ayarNormalized,
                MalTanimNorm = malN,
                TedarikciFirmaNorm = firmaN,
                BirimMaliyet = birim,
                TotalGram = 0,
                BarcodedGram = 0,
                UnbarcodedGram = 0
            };
            db.DepoStokHavuzlar.Add(row);
        }
        else if (row.IsDeleted)
        {
            // Unique index IsDeleted alanını içermediği için soft-deleted satırı canlandırıp yeniden kullan.
            row.IsDeleted = false;
            row.UpdatedAt = DateTime.UtcNow;
        }
        row.AddGram(gram);
    }

    /// <summary>
    /// Havuz satırı yoksa (eski veri / migration sonrası), toptancı alışları + ürün kartlarından bu üçlü için tek satır üretir.
    /// RebuildHavuz ile aynı matematik; yalnızca eksik satır için.
    /// </summary>
    public static async Task TryEnsureHavuzRowFromPurchasesAsync(
        AppDbContext db,
        Guid tenantId,
        Guid branchId,
        string ayarNormalized,
        string malN,
        string firmaN,
        decimal birim,
        CancellationToken ct,
        bool saveImmediately = true)
    {
        if (string.IsNullOrEmpty(ayarNormalized) || string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN))
            return;

        var exists = await db.DepoStokHavuzlar.AnyAsync(x =>
            x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted
            && x.Ayar == ayarNormalized && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN
            && x.BirimMaliyet == birim, ct);
        if (exists) return;

        decimal purchaseGram = 0;
        var purchases = await db.Purchases
            .Include(p => p.Items)
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.PurchaseType == PurchaseType.Toptanci && p.BranchId == branchId)
            .ToListAsync(ct);

        foreach (var p in purchases)
        {
            foreach (var it in p.Items ?? Array.Empty<PurchaseItem>())
            {
                var ayar = NormalizeAyarKarat(it.Karat);
                if (string.IsNullOrEmpty(ayar) || it.Quantity <= 0) continue;
                var mal = NormalizeMal(MalTanimFromPurchaseItem(it, it.Karat ?? ""));
                var fn = NormalizeFirma(p.PartnerName);
                var bm = RoundBirimMaliyet(it.BirimIscilikHas ?? 0);
                if (ayar == ayarNormalized && mal == malN && fn == firmaN && bm == birim)
                    purchaseGram += it.Quantity;
            }
        }

        if (purchaseGram <= 0.0001m) return;

        decimal barcodedGram = 0;
        var products = await db.Products
            .Where(p => p.TenantId == tenantId && !p.IsDeleted && !string.IsNullOrWhiteSpace(p.MalTanim))
            .ToListAsync(ct);

        foreach (var pr in products)
        {
            var ayar = NormalizeAyarKarat(pr.Karat);
            if (ayar != ayarNormalized) continue;
            var m = NormalizeMal(pr.MalTanim);
            var f = NormalizeFirma(pr.DepoTedarikciFirma);
            var bm = RoundBirimMaliyet(pr.DepoBirimMaliyet ?? 0);
            if (m != malN || f != firmaN || bm != birim) continue;
            if (!pr.DepoBirimMaliyet.HasValue) continue;

            decimal gramMove;
            if ((pr.InventoryType ?? InventoryType.Tekil) == InventoryType.Ziynet)
            {
                var w = pr.WeightGr ?? 0m;
                var st = pr.StokMiktari ?? 0;
                gramMove = w * st;
            }
            else
                gramMove = pr.WeightGr ?? 0m;

            if (gramMove > 0) barcodedGram += gramMove;
        }

        var barcoded = Math.Min(barcodedGram, purchaseGram);
        var unbarcoded = Math.Max(0, purchaseGram - barcoded);

        var row = new DepoStokHavuz
        {
            TenantId = tenantId,
            BranchId = branchId,
            Ayar = ayarNormalized,
            MalTanimNorm = malN,
            TedarikciFirmaNorm = firmaN,
            BirimMaliyet = birim,
            TotalGram = purchaseGram,
            BarcodedGram = barcoded,
            UnbarcodedGram = unbarcoded,
            UpdatedAt = DateTime.UtcNow
        };
        db.DepoStokHavuzlar.Add(row);
        if (saveImmediately)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>Mal + tedarikçi + birim + ayar için havuz satırı; tam eşleşme yoksa yakın birim/folded anahtar ile dener.</summary>
    public static async Task<DepoStokHavuz?> FindHavuzRowAsync(
        AppDbContext db,
        Guid tenantId,
        Guid branchId,
        string ayarRaw,
        string malRaw,
        string firmaRaw,
        decimal birimRaw,
        CancellationToken ct,
        bool tracked = false)
    {
        var ay = NormalizeAyarKarat(ayarRaw);
        var malN = NormalizeMal(malRaw);
        var firmaN = NormalizeFirma(firmaRaw);
        var birim = RoundBirimMaliyet(birimRaw);
        if (string.IsNullOrEmpty(ay) || string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN))
            return null;

        IQueryable<DepoStokHavuz> query = tracked
            ? db.DepoStokHavuzlar.Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted)
            : db.DepoStokHavuzlar.AsNoTracking().Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted);

        var exact = await query.FirstOrDefaultAsync(x =>
            x.Ayar == ay && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN && x.BirimMaliyet == birim, ct);
        if (exact != null) return exact;

        var candidates = await query
            .Where(x => x.Ayar == ay && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN)
            .ToListAsync(ct);
        if (candidates.Count > 0)
        {
            return candidates
                .OrderBy(x => Math.Abs(x.BirimMaliyet - birim))
                .ThenByDescending(x => x.UnbarcodedGram)
                .FirstOrDefault();
        }

        var malFold = FoldLookupKey(malN);
        var firmaFold = FoldLookupKey(firmaN);
        if (string.IsNullOrEmpty(malFold) || string.IsNullOrEmpty(firmaFold))
            return null;

        var folded = await query
            .Where(x => x.Ayar == ay)
            .ToListAsync(ct);
        return folded
            .Where(x => FoldLookupKey(x.MalTanimNorm) == malFold && FoldLookupKey(x.TedarikciFirmaNorm) == firmaFold)
            .OrderBy(x => Math.Abs(x.BirimMaliyet - birim))
            .ThenByDescending(x => x.UnbarcodedGram)
            .FirstOrDefault();
    }

    /// <summary>Hurda ödemesi: ayar bazlı barkodsuz düşümü havuz satırlarına oransal yayılır.</summary>
    public static async Task WithdrawUnbarcodedProportionalAsync(
        AppDbContext db, Guid tenantId, Guid branchId, string ayarNormalized, decimal gw, CancellationToken ct)
    {
        if (gw <= 0) return;
        var rows = await db.DepoStokHavuzlar
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.Ayar == ayarNormalized)
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        var totalUnb = rows.Sum(x => x.UnbarcodedGram);
        if (totalUnb <= 0.0001m) return;
        var remaining = gw;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            decimal share;
            if (i == rows.Count - 1)
                share = Math.Min(row.UnbarcodedGram, remaining);
            else
            {
                share = Math.Round(gw * (row.UnbarcodedGram / totalUnb), 4, MidpointRounding.AwayFromZero);
                share = Math.Min(share, row.UnbarcodedGram);
                share = Math.Min(share, remaining);
            }
            if (share > 0)
                row.WithdrawUnbarcoded(share);
            remaining -= share;
            if (remaining <= 0.0001m) break;
        }
    }
}
