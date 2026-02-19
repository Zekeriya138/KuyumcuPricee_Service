using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT
public class InventoryController : ControllerBase
{
    private readonly AppDbContext _db;
    public InventoryController(AppDbContext db) => _db = db;

    // GET /api/inventory/items/by-barcode/{code}
    [HttpGet("items/by-barcode/{code}")]
    public async Task<IActionResult> GetByBarcode(string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "Barcode boş olamaz." });

        var item = await _db.ProductItems
            .AsNoTracking()
            .Include(x => x.Product)
            .Include(x => x.Branch)
            .FirstOrDefaultAsync(x => x.Barcode == code.Trim(), ct);

        if (item is null) return NotFound();

        return Ok(new
        {
            item.Id,
            item.Barcode,
            item.Serial,
            item.Karat,
            item.Weight,
            item.IsInStock,
            Branch = new { item.Branch.Id, item.Branch.Name },
            Product = new
            {
                item.Product.Id,
                item.Product.ProductCode,
                item.Product.Name,
                item.Product.Category
            }
        });
    }
}
