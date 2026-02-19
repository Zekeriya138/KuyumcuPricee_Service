using System.Globalization;
using System.Linq; // LINQ ve Sum için eklendi
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_infrastructure.Persistence;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ReportsController(AppDbContext db) => _db = db;

    // -------- Tenant helper (eklendi) --------
    private Guid GetTenantId()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;

        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;

        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;

        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
    }

    /// <summary>
    /// Satış özeti: toplam miktar (Quantity) ve tutar (LineTotal).
    /// Filtreler: branchId, customerId, from, to (CreatedAt üzerinden).
    /// includeBreakdown=true -> gün bazında grup.
    /// </summary>
    [HttpGet("sales/summary")]
    public async Task<IActionResult> SalesSummary(
        [FromQuery] Guid? branchId,
        [FromQuery] Guid? customerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] bool includeBreakdown = false,
        CancellationToken ct = default)
    {
        // SaleItem'lar üzerinden sorgu (doğru modelleme)
        var salesQuery = _db.Sales.AsNoTracking().Where(s => !s.IsDeleted).AsQueryable();

        if (branchId.HasValue) salesQuery = salesQuery.Where(s => s.BranchId == branchId.Value);
        if (customerId.HasValue) salesQuery = salesQuery.Where(s => s.CustomerId == customerId.Value);
        if (from.HasValue) salesQuery = salesQuery.Where(s => s.CreatedAt >= from.Value);
        if (to.HasValue) salesQuery = salesQuery.Where(s => s.CreatedAt < to.Value);

        var totalQty = await salesQuery.SelectMany(s => s.Items).SumAsync(i => (decimal?)i.Quantity, ct) ?? 0m;
        var totalPrice = await salesQuery.SelectMany(s => s.Items).SumAsync(i => (decimal?)i.LineTotal, ct) ?? 0m;
        var count = await salesQuery.CountAsync(ct);

        object? breakdown = null;
        if (includeBreakdown)
        {
            // Gün bazında kırılım için Sale ve SaleItem'ı dahil et
            breakdown = await salesQuery
                .Include(s => s.Items)
                .Select(s => new
                {
                    Date = s.CreatedAt.Date,
                    Quantity = s.Items.Sum(i => i.Quantity),
                    Amount = s.Items.Sum(i => i.LineTotal)
                })
                .GroupBy(x => x.Date)
                .Select(g => new
                {
                    Date = g.Key,
                    Count = g.Count(),
                    Quantity = g.Sum(x => x.Quantity),
                    Amount = g.Sum(x => x.Amount)
                })
                .OrderBy(x => x.Date)
                .ToListAsync(ct);
        }

        return Ok(new
        {
            Filters = new { branchId, customerId, from, to },
            Count = count,
            TotalQuantity = totalQty,
            TotalAmount = totalPrice,
            Breakdown = breakdown
        });
    }

    /// <summary>
    /// Alış özeti: toplam miktar (kalemlerin Quantity toplamı) ve tutar (GrandTotal).
    /// Filtreler: branchId, customerId, from, to (Purchase.Date üzerinden).
    /// includeBreakdown=true -> gün bazında grup.
    /// </summary>
    [HttpGet("purchases/summary")]
    public async Task<IActionResult> PurchasesSummary(
        [FromQuery] Guid? branchId,
        [FromQuery] Guid? customerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] bool includeBreakdown = false,
        CancellationToken ct = default)
    {
        var p = _db.Purchases.AsNoTracking().AsQueryable();

        // Projende Purchase için soft delete zorunlu değil; varsa saygı gösterelim:
        var softProp = typeof(kuyumcu_domain.Entities.Purchase).GetProperty("IsDeleted");
        if (softProp != null)
            p = p.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

        if (branchId.HasValue) p = p.Where(x => x.BranchId == branchId.Value);
        if (customerId.HasValue) p = p.Where(x => x.CustomerId == customerId.Value);
        if (from.HasValue) p = p.Where(x => x.Date >= from.Value);
        if (to.HasValue) p = p.Where(x => x.Date < to.Value);

        // Tutar için Purchase.GrandTotal; miktar için PurchaseItem.Quantity toplamı
        var totalAmount = await p.SumAsync(x => (decimal?)x.GrandTotal, ct) ?? 0m;

        // PurchaseItem üzerinden Quantity toplamı: (Bu kısım zaten doğruydu, sadece kod akışı düzeltildi)
        var pi = _db.PurchaseItems.AsNoTracking()
            .Where(i => p.Select(pp => pp.Id).Contains(i.PurchaseId)); // Purchase sorgusuna göre filtrele

        var totalQuantity = await pi.SumAsync(i => (decimal?)i.Quantity, ct) ?? 0m;
        var count = await p.CountAsync(ct);

        object? breakdown = null;
        if (includeBreakdown)
        {
            breakdown = await p
                .GroupBy(x => x.Date.Date)
                .Select(g => new {
                    Date = g.Key,
                    Count = g.Count(),
                    Amount = g.Sum(x => x.GrandTotal)
                })
                .OrderBy(x => x.Date)
                .ToListAsync(ct);
        }

        return Ok(new
        {
            Filters = new { branchId, customerId, from, to },
            Count = count,
            TotalQuantity = totalQuantity,
            TotalAmount = totalAmount,
            Breakdown = breakdown
        });
    }
}

//using System.Globalization;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using kuyumcu_infrastructure.Persistence;

//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/[controller]")]
//[Authorize]
//public class ReportsController : ControllerBase
//{
//    private readonly AppDbContext _db;
//    public ReportsController(AppDbContext db) => _db = db;

//    /// <summary>
//    /// Satış özeti: toplam miktar (Quantity) ve tutar (TotalPrice).
//    /// Filtreler: branchId, customerId, from, to (CreatedAt üzerinden).
//    /// includeBreakdown=true -> gün bazında grup.
//    /// </summary>
//    [HttpGet("sales/summary")]
//    public async Task<IActionResult> SalesSummary(
//        [FromQuery] Guid? branchId,
//        [FromQuery] Guid? customerId,
//        [FromQuery] DateTime? from,
//        [FromQuery] DateTime? to,
//        [FromQuery] bool includeBreakdown = false,
//        CancellationToken ct = default)
//    {
//        var q = _db.Sales.AsNoTracking().Where(s => !s.IsDeleted).AsQueryable();

//        if (branchId.HasValue) q = q.Where(s => s.BranchId == branchId.Value);
//        if (customerId.HasValue) q = q.Where(s => s.CustomerId == customerId.Value);
//        if (from.HasValue) q = q.Where(s => s.CreatedAt >= from.Value);
//        if (to.HasValue) q = q.Where(s => s.CreatedAt < to.Value);

//        var totalQty = await q.SumAsync(s => (decimal?)s.Quantity, ct) ?? 0m;
//        var totalPrice = await q.SumAsync(s => (decimal?)s.TotalPrice, ct) ?? 0m;
//        var count = await q.CountAsync(ct);

//        object? breakdown = null;
//        if (includeBreakdown)
//        {
//            breakdown = await q
//                .GroupBy(s => s.CreatedAt.Date)
//                .Select(g => new {
//                    Date = g.Key,
//                    Count = g.Count(),
//                    Quantity = g.Sum(x => x.Quantity),
//                    Amount = g.Sum(x => x.TotalPrice)
//                })
//                .OrderBy(x => x.Date)
//                .ToListAsync(ct);
//        }

//        return Ok(new
//        {
//            Filters = new { branchId, customerId, from, to },
//            Count = count,
//            TotalQuantity = totalQty,
//            TotalAmount = totalPrice,
//            Breakdown = breakdown
//        });
//    }

//    /// <summary>
//    /// Alış özeti: toplam miktar (kalemlerin Quantity toplamı) ve tutar (GrandTotal).
//    /// Filtreler: branchId, customerId, from, to (Purchase.Date üzerinden).
//    /// includeBreakdown=true -> gün bazında grup.
//    /// </summary>
//    [HttpGet("purchases/summary")]
//    public async Task<IActionResult> PurchasesSummary(
//        [FromQuery] Guid? branchId,
//        [FromQuery] Guid? customerId,
//        [FromQuery] DateTime? from,
//        [FromQuery] DateTime? to,
//        [FromQuery] bool includeBreakdown = false,
//        CancellationToken ct = default)
//    {
//        var p = _db.Purchases.AsNoTracking().AsQueryable();

//        // Projende Purchase için soft delete zorunlu değil; varsa saygı gösterelim:
//        var softProp = typeof(kuyumcu_domain.Entities.Purchase).GetProperty("IsDeleted");
//        if (softProp != null)
//            p = p.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

//        if (branchId.HasValue) p = p.Where(x => x.BranchId == branchId.Value);
//        if (customerId.HasValue) p = p.Where(x => x.CustomerId == customerId.Value);
//        if (from.HasValue) p = p.Where(x => x.Date >= from.Value);
//        if (to.HasValue) p = p.Where(x => x.Date < to.Value);

//        // Tutar için Purchase.GrandTotal; miktar için PurchaseItem.Quantity toplamı
//        var totalAmount = await p.SumAsync(x => (decimal?)x.GrandTotal, ct) ?? 0m;

//        // PurchaseItem üzerinden Quantity toplamı:
//        var pi = _db.PurchaseItems.AsNoTracking().AsQueryable();
//        pi = pi.Where(i => _db.Purchases
//            .Where(pp =>
//                (!branchId.HasValue || pp.BranchId == branchId.Value) &&
//                (!customerId.HasValue || pp.CustomerId == customerId.Value) &&
//                (!from.HasValue || pp.Date >= from.Value) &&
//                (!to.HasValue || pp.Date < to.Value))
//            .Select(pp => pp.Id)
//            .Contains(i.PurchaseId));

//        var totalQuantity = await pi.SumAsync(i => (decimal?)i.Quantity, ct) ?? 0m;
//        var count = await p.CountAsync(ct);

//        object? breakdown = null;
//        if (includeBreakdown)
//        {
//            breakdown = await p
//                .GroupBy(x => x.Date.Date)
//                .Select(g => new {
//                    Date = g.Key,
//                    Count = g.Count(),
//                    Amount = g.Sum(x => x.GrandTotal)
//                })
//                .OrderBy(x => x.Date)
//                .ToListAsync(ct);
//        }

//        return Ok(new
//        {
//            Filters = new { branchId, customerId, from, to },
//            Count = count,
//            TotalQuantity = totalQuantity,
//            TotalAmount = totalAmount,
//            Breakdown = breakdown
//        });
//    }
//}
