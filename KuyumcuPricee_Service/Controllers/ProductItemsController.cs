using kuyumcu_infrastructure.Persistence;
using kuyumcu_domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_infrastructure.Tenancy;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ProductItemsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public ProductItemsController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // ============ CREATE ============
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductItemDto dto, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId missing (JWT veya X-Tenant-Id)." });

        // Branch kontrol (tenant scoped)
        var branchOk = await _db.Branches.AsNoTracking()
            .AnyAsync(b => b.Id == dto.BranchId && b.TenantId == tenantId, ct);
        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId." });

        if (string.IsNullOrWhiteSpace(dto.ProductCode))
            return BadRequest(new { error = "ProductCode zorunlu." });

        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProductCode == dto.ProductCode && p.TenantId == tenantId && p.BranchId == dto.BranchId, ct);

        if (product is null)
            return BadRequest(new { error = "Geçersiz ProductCode." });

        // Barkod boşsa üret
        var barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? GenerateBarcode() : dto.Barcode.Trim();
        var serial = string.IsNullOrWhiteSpace(dto.Serial) ? null : dto.Serial.Trim();

        var entity = new ProductItem
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            BranchId = dto.BranchId,
            ProductId = product.Id,

            Barcode = barcode!,
            Serial = serial ?? "",
            Karat = dto.Karat?.Trim() ?? "",
            Weight = dto.Weight,

            Cost = dto.Cost,

            IsInStock = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            _db.ProductItems.Add(entity);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { error = "Veritabanı hatası: " + msg });
        }

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, ToDto(entity, product));
    }

    // ============ LIST (PagedResult döner) ============
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] string? productCode,
        [FromQuery] string? barcode,
        [FromQuery] string? serial,
        [FromQuery] string? karat,
        [FromQuery] bool? inStock,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId missing (JWT veya X-Tenant-Id)." });

        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 50;

        var q = _db.ProductItems.AsNoTracking()
            .Include(x => x.Product)
            .Where(x => x.TenantId == tenantId)
            .AsQueryable();

        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
        if (!string.IsNullOrWhiteSpace(productCode))
            q = q.Where(x => x.Product.ProductCode == productCode);
        if (!string.IsNullOrWhiteSpace(barcode))
            q = q.Where(x => x.Barcode == barcode);
        if (!string.IsNullOrWhiteSpace(serial))
            q = q.Where(x => x.Serial == serial);
        if (!string.IsNullOrWhiteSpace(karat))
            q = q.Where(x => x.Karat == karat);
        if (inStock.HasValue)
            q = q.Where(x => x.IsInStock == inStock.Value);

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ProductItemDto(
                x.Id,
                x.BranchId,
                x.ProductId,
                x.Product.ProductCode,
                x.Product.Name,
                x.Product.Category,
                string.IsNullOrWhiteSpace(x.Product.Olcu) ? null : x.Product.Olcu,
                string.IsNullOrWhiteSpace(x.Serial) ? null : x.Serial,
                string.IsNullOrWhiteSpace(x.Barcode) ? null : x.Barcode,
                x.Karat,
                x.Weight,
                x.Cost,
                x.IsInStock,
                x.CreatedAt,
                x.UpdatedAt,
                string.IsNullOrWhiteSpace(x.Product.MalTanim) ? null : x.Product.MalTanim.Trim(),
                x.Product.BelirlenenSatisFiyatiHas,
                x.Product.BirimSatisIscilikHas
            ))
            .ToListAsync(ct);

        return Ok(new PagedResult<ProductItemDto>
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = items
        });
    }

    // ============ GET BY ID ============
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId missing (JWT veya X-Tenant-Id)." });

        var x = await _db.ProductItems.AsNoTracking()
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);

        if (x is null) return NotFound();

        return Ok(ToDto(x, x.Product));
    }

    // ============ GET BY BARCODE ============
    [HttpGet("by-barcode/{code}")]
    public async Task<IActionResult> GetByBarcode(string code, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId missing (JWT veya X-Tenant-Id)." });

        if (string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "Barcode boş olamaz." });

        var x = await _db.ProductItems.AsNoTracking()
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Barcode == code && p.TenantId == tenantId, ct);

        if (x is null) return NotFound();

        return Ok(ToDto(x, x.Product));
    }

    // ============ UPDATE ============
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductItemDto dto, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId missing (JWT veya X-Tenant-Id)." });

        var x = await _db.ProductItems
            .Include(p => p.Product)
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);

        if (x is null) return NotFound();

        var branchOk = await _db.Branches.AsNoTracking()
            .AnyAsync(b => b.Id == dto.BranchId && b.TenantId == tenantId, ct);

        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId." });

        x.BranchId = dto.BranchId;
        x.Karat = dto.Karat?.Trim() ?? "";
        x.Weight = dto.Weight;

        x.Cost = dto.Cost; // ✅

        x.IsInStock = dto.IsInStock;
        x.Serial = string.IsNullOrWhiteSpace(dto.Serial) ? "" : dto.Serial.Trim();
        x.Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? x.Barcode : dto.Barcode.Trim();
        x.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(x, x.Product));
    }

    // ============ DELETE ============
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "TenantId missing (JWT veya X-Tenant-Id)." });

        var x = await _db.ProductItems.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenantId, ct);
        if (x is null) return NotFound();

        var softProp = typeof(ProductItem).GetProperty("IsDeleted");
        if (softProp != null)
            softProp.SetValue(x, true);
        else
            _db.ProductItems.Remove(x);

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ============ Helpers ============
    private static string GenerateBarcode()
    {
        var ts = DateTime.UtcNow.ToString("yyMMdd");
        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("=", "").Replace("+", "").Replace("/", "")
            .ToUpperInvariant();
        return $"PI-{ts}-{rnd[..6]}";
    }

    private static ProductItemDto ToDto(ProductItem x, Product product) =>
        new(
            x.Id,
            x.BranchId,
            x.ProductId,
            product.ProductCode,
            product.Name,
            product.Category,
            string.IsNullOrWhiteSpace(product.Olcu) ? null : product.Olcu,
            string.IsNullOrWhiteSpace(x.Serial) ? null : x.Serial,
            string.IsNullOrWhiteSpace(x.Barcode) ? null : x.Barcode,
            x.Karat,
            x.Weight,
            x.Cost,
            x.IsInStock,
            x.CreatedAt,
            x.UpdatedAt,
            string.IsNullOrWhiteSpace(product.MalTanim) ? null : product.MalTanim.Trim(),
            product.BelirlenenSatisFiyatiHas,
            product.BirimSatisIscilikHas
        );
}

//// Kuyumcu.PriceService/Controllers/ProductItemsController.cs
//using System.Security.Claims;
//using kuyumcu_infrastructure.Persistence;
//using kuyumcu_domain.Entities;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using System.Linq;

//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/[controller]")]
//[Authorize]
//public sealed class ProductItemsController : ControllerBase
//{
//    private readonly AppDbContext _db;
//    public ProductItemsController(AppDbContext db) => _db = db;

//    // ============ CREATE ============
//    [HttpPost]
//    public async Task<IActionResult> Create([FromBody] CreateProductItemDto dto, CancellationToken ct)
//    {
//        // Branch kontrol
//        var branchOk = await _db.Branches.AsNoTracking().AnyAsync(b => b.Id == dto.BranchId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId." });

//        // Ürün koda göre bulunuyor (mevcut düzen)
//        if (string.IsNullOrWhiteSpace(dto.ProductCode))
//            return BadRequest(new { error = "ProductCode zorunlu." });

//        var product = await _db.Products.AsNoTracking()
//            .FirstOrDefaultAsync(p => p.ProductCode == dto.ProductCode, ct);
//        if (product is null)
//            return BadRequest(new { error = "Geçersiz ProductCode." });

//        // Barkod boşsa üret
//        var barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? GenerateBarcode() : dto.Barcode.Trim();
//        var serial = string.IsNullOrWhiteSpace(dto.Serial) ? null : dto.Serial.Trim();

//        var entity = new ProductItem
//        {
//            Id = Guid.NewGuid(),
//            BranchId = dto.BranchId,
//            ProductId = product.Id,
//            Barcode = barcode!,
//            Serial = serial ?? "",
//            Karat = dto.Karat?.Trim() ?? "",
//            Weight = dto.Weight,
//            IsInStock = true,
//            CreatedAt = DateTime.UtcNow
//        };

//        _db.ProductItems.Add(entity);
//        await _db.SaveChangesAsync(ct);

//        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, ToDto(entity, product.ProductCode, product.Name));
//    }

//    // ============ LIST ============
//    [HttpGet]
//    public async Task<IActionResult> List(
//        [FromQuery] Guid? branchId,
//        [FromQuery] string? productCode,
//        [FromQuery] string? barcode,
//        [FromQuery] string? serial,
//        [FromQuery] string? karat,
//        [FromQuery] bool? inStock,
//        [FromQuery] int page = 1,
//        [FromQuery] int pageSize = 20,
//        CancellationToken ct = default)
//    {
//        if (page <= 0) page = 1;
//        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

//        var q = _db.ProductItems.AsNoTracking().Include(x => x.Product).AsQueryable();

//        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
//        if (!string.IsNullOrWhiteSpace(productCode))
//            q = q.Where(x => x.Product.ProductCode == productCode);
//        if (!string.IsNullOrWhiteSpace(barcode))
//            q = q.Where(x => x.Barcode == barcode);
//        if (!string.IsNullOrWhiteSpace(serial))
//            q = q.Where(x => x.Serial == serial);
//        if (!string.IsNullOrWhiteSpace(karat))
//            q = q.Where(x => x.Karat == karat);
//        if (inStock.HasValue)
//            q = q.Where(x => x.IsInStock == inStock.Value);

//        var total = await q.CountAsync(ct);
//        var items = await q
//            .OrderByDescending(x => x.CreatedAt)
//            .Skip((page - 1) * pageSize)
//            .Take(pageSize)
//            .Select(x => new ProductItemDto(
//                x.Id, x.BranchId, x.ProductId, x.Product.ProductCode, x.Product.Name,
//                x.Serial, x.Barcode, x.Karat, x.Weight, x.IsInStock, x.CreatedAt, x.UpdatedAt
//            ))
//            .ToListAsync(ct);

//        return Ok(new { total, page, pageSize, items });
//    }

//    // ============ GET BY ID ============
//    [HttpGet("{id:guid}")]
//    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
//    {
//        var x = await _db.ProductItems.AsNoTracking()
//            .Include(p => p.Product)
//            .FirstOrDefaultAsync(p => p.Id == id, ct);
//        if (x is null) return NotFound();

//        return Ok(ToDto(x, x.Product.ProductCode, x.Product.Name));
//    }

//    // ============ GET BY BARCODE ============
//    [HttpGet("by-barcode/{code}")]
//    public async Task<IActionResult> GetByBarcode(string code, CancellationToken ct)
//    {
//        if (string.IsNullOrWhiteSpace(code))
//            return BadRequest(new { error = "Barcode boş olamaz." });

//        var x = await _db.ProductItems.AsNoTracking()
//            .Include(p => p.Product)
//            .FirstOrDefaultAsync(p => p.Barcode == code, ct);
//        if (x is null) return NotFound();

//        return Ok(ToDto(x, x.Product.ProductCode, x.Product.Name));
//    }

//    // ============ UPDATE ============
//    [HttpPut("{id:guid}")]
//    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductItemDto dto, CancellationToken ct)
//    {
//        var x = await _db.ProductItems.Include(p => p.Product)
//            .FirstOrDefaultAsync(p => p.Id == id, ct);
//        if (x is null) return NotFound();

//        // Branch doğrula
//        var branchOk = await _db.Branches.AsNoTracking().AnyAsync(b => b.Id == dto.BranchId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId." });

//        x.BranchId = dto.BranchId;
//        x.Karat = dto.Karat?.Trim() ?? "";
//        x.Weight = dto.Weight;
//        x.IsInStock = dto.IsInStock;
//        x.Serial = string.IsNullOrWhiteSpace(dto.Serial) ? "" : dto.Serial.Trim();
//        x.Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? x.Barcode : dto.Barcode.Trim();
//        x.UpdatedAt = DateTime.UtcNow;

//        await _db.SaveChangesAsync(ct);

//        // ürün yeniden dahil ederek DTO dönelim
//        var prod = await _db.Products.AsNoTracking().FirstAsync(p => p.Id == x.ProductId, ct);
//        return Ok(ToDto(x, prod.ProductCode, prod.Name));
//    }

//    // ============ DELETE ============
//    [HttpDelete("{id:guid}")]
//    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    {
//        var x = await _db.ProductItems.FirstOrDefaultAsync(p => p.Id == id, ct);
//        if (x is null) return NotFound();

//        // Eğer Entity base’inde IsDeleted varsa soft delete yap
//        var softProp = typeof(ProductItem).GetProperty("IsDeleted");
//        if (softProp != null)
//        {
//            softProp.SetValue(x, true);
//        }
//        else
//        {
//            _db.ProductItems.Remove(x);
//        }

//        await _db.SaveChangesAsync(ct);
//        return NoContent();
//    }

//    // ============ Helpers ============
//    private static string GenerateBarcode()
//    {
//        // Örn: PI-250907-ABC123
//        var ts = DateTime.UtcNow.ToString("yyMMdd");
//        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
//                    .Replace("=", "").Replace("+", "").Replace("/", "")
//                    .ToUpperInvariant();
//        return $"PI-{ts}-{rnd[..6]}";
//    }

//    private static ProductItemDto ToDto(ProductItem x, string productCode, string productName) =>
//        new(
//            x.Id,
//            x.BranchId,
//            x.ProductId,
//            productCode,
//            productName,
//            string.IsNullOrWhiteSpace(x.Serial) ? null : x.Serial,
//            string.IsNullOrWhiteSpace(x.Barcode) ? null : x.Barcode,
//            x.Karat,
//            x.Weight,
//            x.IsInStock,
//            x.CreatedAt,
//            x.UpdatedAt
//        );
//}
