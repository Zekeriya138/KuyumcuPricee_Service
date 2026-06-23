using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KUYUMCU.Price_Service.Models;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kuyumcu.PriceService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CategoriesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CategoriesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var list = await _db.Categories
            .AsNoTracking()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Name)
            .Select(x => new CategoryDto(x.Id, x.Name, x.KategoriKodu))
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var c = await _db.Categories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        return c is null ? NotFound() : Ok(new CategoryDto(c.Id, c.Name, c.KategoriKodu));
    }

    [HttpPost]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Create([FromBody] CreateCategoryDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.KategoriKodu))
            return BadRequest(new { error = "Name ve KategoriKodu zorunludur." });

        var code = dto.KategoriKodu.Trim().ToUpperInvariant();
        var exists = await _db.Categories.AnyAsync(x => x.KategoriKodu == code && !x.IsDeleted, ct);
        if (exists)
            return Conflict(new { error = "Bu kategori kodu zaten kayıtlı." });

        var entity = new Category
        {
            Name = dto.Name.Trim(),
            KategoriKodu = code
        };
        _db.Categories.Add(entity);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, new CategoryDto(entity.Id, entity.Name, entity.KategoriKodu));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCategoryDto dto, CancellationToken ct = default)
    {
        var c = await _db.Categories.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (c is null) return NotFound();

        c.Name = string.IsNullOrWhiteSpace(dto.Name) ? c.Name : dto.Name.Trim();
        if (!string.IsNullOrWhiteSpace(dto.KategoriKodu))
            c.KategoriKodu = dto.KategoriKodu.Trim().ToUpperInvariant();
        await _db.SaveChangesAsync(ct);
        return Ok(new CategoryDto(c.Id, c.Name, c.KategoriKodu));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var c = await _db.Categories.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (c is null) return NotFound();
        c.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
