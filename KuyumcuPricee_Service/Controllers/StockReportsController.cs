using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_infrastructure.Persistence;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/reports/stock")]
[Authorize]
public class StockReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockReportsController(AppDbContext db) => _db = db;

    /// <summary>
    /// Anlık stok özeti.
    /// - branchId verilirse: stok, StockMovements üzerinden (In-Out toplamı) sadece o şube için hesaplanır.
    /// - branchId verilmezse: stok, Stocks (global) tablosundan döner.
    /// İsteğe bağlı filtreler: productCode, category, karat, minQty, maxQty
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] Guid? branchId,
        [FromQuery] string? productCode,
        [FromQuery] string? category,
        [FromQuery] string? karat,
        [FromQuery] decimal? minQty,
        [FromQuery] decimal? maxQty,
        CancellationToken ct = default)
    {
        // Temel ürün sorgusu (filtreler)
        var products = _db.Products.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(productCode))
            products = products.Where(p => p.ProductCode == productCode);

        if (!string.IsNullOrWhiteSpace(category))
            products = products.Where(p => p.Category == category);

        if (!string.IsNullOrWhiteSpace(karat))
            products = products.Where(p => p.Karat == karat);

        if (branchId.HasValue && branchId.Value != Guid.Empty)
            products = products.Where(p => p.BranchId == branchId.Value);

        if (branchId.HasValue)
        {
            // ŞUBEYE GÖRE: hareketlerden stok miktarını hesapla
            // qty = SUM(InQty - OutQty) filtre: BranchId
            var q = from p in products
                    join mv in _db.StockMovements.AsNoTracking()
                        .Where(m => m.BranchId == branchId.Value)
                        on p.Id equals mv.ProductId into gmv
                    select new
                    {
                        p.Id,
                        p.ProductCode,
                        p.Name,
                        p.Category,
                        p.Karat,
                        Quantity = gmv.Sum(m => m.InQty - m.OutQty),
                        LastMovementAt = gmv.Max(m => (DateTime?)m.Date)
                    };

            if (minQty.HasValue) q = q.Where(x => x.Quantity >= minQty.Value);
            if (maxQty.HasValue) q = q.Where(x => x.Quantity <= maxQty.Value);

            var list = await q
                .OrderBy(x => x.ProductCode)
                .ToListAsync(ct);

            return Ok(new
            {
                BranchId = branchId,
                Count = list.Count,
                Items = list
            });
        }
        else
        {
            // GLOBAL: Stocks tablosundan oku (tek satır/ürün)
            var q = from p in products
                    join s in _db.Stocks.AsNoTracking()
                        on p.Id equals s.ProductId into gs
                    from s in gs.DefaultIfEmpty()
                        // last movement'ı bilgi amaçlı ekleyelim
                    join mv in _db.StockMovements.AsNoTracking()
                        .GroupBy(m => m.ProductId)
                        .Select(g => new { ProductId = g.Key, Last = g.Max(x => x.Date) })
                        on p.Id equals mv.ProductId into gmv
                    from mvLast in gmv.DefaultIfEmpty()
                    select new
                    {
                        p.Id,
                        p.ProductCode,
                        p.Name,
                        p.Category,
                        p.Karat,
                        Quantity = s != null ? s.Quantity : 0m,
                        LastMovementAt = mvLast != null ? (DateTime?)mvLast.Last : null
                    };

            if (minQty.HasValue) q = q.Where(x => x.Quantity >= minQty.Value);
            if (maxQty.HasValue) q = q.Where(x => x.Quantity <= maxQty.Value);

            var list = await q
                .OrderBy(x => x.ProductCode)
                .ToListAsync(ct);

            return Ok(new
            {
                Count = list.Count,
                Items = list
            });
        }
    }

    /// <summary>
    /// Stok hareket defteri (sayfalı).
    /// Filtreler: branchId (zorunlu değil), productCode, from, to, refKind, direction
    /// </summary>
    [HttpGet("movements")]
    public async Task<IActionResult> Movements(
        [FromQuery] Guid? branchId,
        [FromQuery] string? productCode,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? refKind,     // "Sale" | "Purchase" | "Manual" vb. (enum adları)
        [FromQuery] string? direction,   // "In" | "Out"
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 50;

        var q = _db.StockMovements
            .AsNoTracking()
            .OrderByDescending(m => m.Date)
            .AsQueryable();

        if (branchId.HasValue)
            q = q.Where(m => m.BranchId == branchId.Value);

        if (!string.IsNullOrWhiteSpace(productCode))
        {
            // ürün kodundan ProductId bul
            var pQ = _db.Products.AsNoTracking().Where(p => p.ProductCode == productCode);
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                pQ = pQ.Where(p => p.BranchId == branchId.Value);
            var pids = await pQ.Select(p => p.Id).ToListAsync(ct);

            q = q.Where(m => pids.Contains(m.ProductId));
        }

        if (from.HasValue) q = q.Where(m => m.Date >= from.Value);
        if (to.HasValue) q = q.Where(m => m.Date < to.Value);

        if (!string.IsNullOrWhiteSpace(refKind))
            q = q.Where(m => m.RefKind.ToString() == refKind);

        if (!string.IsNullOrWhiteSpace(direction))
            q = q.Where(m => m.Direction.ToString() == direction);

        var total = await q.CountAsync(ct);

        var rows = await q
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                m.Id,
                m.Date,
                m.BranchId,
                m.ProductId,
                m.ProductItemId,
                m.Direction,
                m.RefKind,
                m.RefType,
                m.RefId,
                m.InQty,
                m.OutQty,
                m.BeforeQty,
                m.AfterQty,
                m.ProductCode,
                m.Karat,
                m.Category,
                m.Reason,
                m.Note
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items = rows });
    }

    /// <summary>
    /// Barkodlu ürün listesi (ProductItems).
    /// Filtreler: branchId, productCode, barcode, serial, inStock
    /// </summary>
    [HttpGet("items")]
    public async Task<IActionResult> Items(
        [FromQuery] Guid? branchId,
        [FromQuery] string? productCode,
        [FromQuery] string? barcode,
        [FromQuery] string? serial,
        [FromQuery] bool? inStock,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 50;

        var q = _db.ProductItems
            .AsNoTracking()
            .Include(pi => pi.Product)
            .AsQueryable();

        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
        if (!string.IsNullOrWhiteSpace(productCode))
        {
            q = q.Where(x => x.Product.ProductCode == productCode);
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                q = q.Where(x => x.Product.BranchId == branchId.Value);
        }
        if (!string.IsNullOrWhiteSpace(barcode))
            q = q.Where(x => x.Barcode == barcode);
        if (!string.IsNullOrWhiteSpace(serial))
            q = q.Where(x => x.Serial == serial);
        if (inStock.HasValue)
            q = q.Where(x => x.IsInStock == inStock.Value);

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderBy(x => x.Product.ProductCode)
            .ThenBy(x => x.Barcode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.ProductId,
                x.BranchId,
                x.Barcode,
                x.Serial,
                x.Karat,
                Weight = x.Weight,
                x.IsInStock,
                Product = new
                {
                    x.Product.ProductCode,
                    x.Product.Name,
                    x.Product.Category
                }
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items = rows });
    }
}
