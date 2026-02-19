using System.Security.Claims;
using KUYUMCU.Price_Service.Models;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kuyumcu.PriceService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // listeleme herkese açık olsun isterseniz [AllowAnonymous] ekleyebilirsiniz
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _db;
    public ProductsController(AppDbContext db) => _db = db;

    // LIST + filtre + sayfalama
    [HttpGet]
    public async Task<IActionResult> List(
      [FromQuery] string? q,
      [FromQuery] string? category,
      [FromQuery] string? karat,
      [FromQuery] int page = 1,
      [FromQuery] int pageSize = 20,
      CancellationToken ct = default)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

        var query = _db.Products.AsNoTracking().Where(x => !x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var qTrim = q.Trim();
            query = query.Where(x =>
                x.ProductCode.Contains(qTrim) ||
                x.Name.Contains(qTrim) ||
                (x.Barcode != null && x.Barcode.Contains(qTrim)));
        }

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(x => x.Category == category.Trim());

        if (!string.IsNullOrWhiteSpace(karat))
            query = query.Where(x => x.Karat == karat.Trim());

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            // ÖNEMLİ: IQueryable projeksiyon
            .Select(x => new ProductDto(
                x.Id, x.ProductCode, x.Name, x.Category, x.Karat, x.WeightGr, x.Barcode, x.CreatedAt
            ))
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items });
    }


    // GET by id
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        return p is null ? NotFound() : Ok(ToDto(p));
    }

    // CREATE (Yalnızca Owner/Admin)
    [HttpPost]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Create([FromBody] CreateProductDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.ProductCode) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { error = "ProductCode ve Name zorunludur." });

        var exists = await _db.Products.AnyAsync(x => x.ProductCode == dto.ProductCode && !x.IsDeleted, ct);
        if (exists)
            return Conflict(new { error = "Aynı ProductCode zaten kayıtlı." });

        var entity = new Product
        {
            ProductCode = dto.ProductCode.Trim(),
            Name = dto.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category!.Trim(),
            Karat = string.IsNullOrWhiteSpace(dto.Karat) ? null : dto.Karat!.Trim(),
            WeightGr = dto.WeightGr,
            Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? null : dto.Barcode!.Trim(),
        };

        _db.Products.Add(entity);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, ToDto(entity));
    }

    // UPDATE (Yalnızca Owner/Admin)
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductDto dto, CancellationToken ct)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (p is null) return NotFound();

        p.Name = string.IsNullOrWhiteSpace(dto.Name) ? p.Name : dto.Name.Trim();
        p.Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category!.Trim();
        p.Karat = string.IsNullOrWhiteSpace(dto.Karat) ? null : dto.Karat!.Trim();
        p.WeightGr = dto.WeightGr;
        p.Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? null : dto.Barcode!.Trim();

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(p));
    }

    // DELETE (soft) — Yalnızca Owner/Admin
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (p is null) return NotFound();

        p.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static ProductDto ToDto(Product p) =>
        new(p.Id, p.ProductCode, p.Name, p.Category, p.Karat, p.WeightGr, p.Barcode, p.CreatedAt);
}
