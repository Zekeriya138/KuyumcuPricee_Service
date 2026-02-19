using Kuyumcu.PriceService.Models;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kuyumcu.PriceService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT gerekli
public class StockController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockController(AppDbContext db) => _db = db;

    // GET /api/stock?productCode=&fromQty=&toQty=&page=1&pageSize=50
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? productCode,
        [FromQuery] decimal? fromQty,
        [FromQuery] decimal? toQty,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var q = _db.Stocks.AsNoTracking()
            .Include(s => s.Product)
            .OrderByDescending(s => s.UpdatedAt)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(productCode))
            q = q.Where(s => s.Product.ProductCode == productCode);

        if (fromQty.HasValue) q = q.Where(s => s.Quantity >= fromQty.Value);
        if (toQty.HasValue) q = q.Where(s => s.Quantity <= toQty.Value);

        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new StockDto(
                s.ProductId,
                s.Product.ProductCode,
                s.Product.Name,
                s.Quantity,
                s.UpdatedAt
            ))
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }

    // GET /api/stock/movements?productId=&productCode=&from=&to=&page=1&pageSize=100
    [HttpGet("movements")]
    public async Task<IActionResult> Movements(
        [FromQuery] Guid? productId,
        [FromQuery] string? productCode,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
    {
        var q = _db.StockMovements.AsNoTracking()
            .Include(m => m.Product)
            .OrderByDescending(m => m.Date)
            .AsQueryable();

        if (productId.HasValue) q = q.Where(m => m.ProductId == productId.Value);
        if (!string.IsNullOrWhiteSpace(productCode))
            q = q.Where(m => m.Product.ProductCode == productCode);

        if (from.HasValue) q = q.Where(m => m.Date >= from.Value);
        if (to.HasValue) q = q.Where(m => m.Date < to.Value);

        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(m => new StockMovementDto(
                m.Id,
                m.ProductId,
                m.Product.ProductCode,
                m.Product.Name,
                m.Date,
                m.InQty,
                m.OutQty,
                m.RefKind.ToString(),
                m.RefId,
                m.Note
            ))
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }
}
