
using System.Security.Claims;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_application.Abstractions;
using KUYUMCU.Price_Service.Models; // DTO'lar için

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SalesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStockService _stock;
    public SalesController(AppDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    // DİKKAT: V1'deki tek kalemli Create, List, GetById ve Update metotları,
    // Sale entity'sinden ilgili alanlar kaldırıldığı için İPTAL EDİLMİŞTİR.
    // Çok kalemli işlemler ve raporlama için api/sales/v2 kullanınız.

    // DTO: Barkodla satış
    public sealed record CreateSaleByBarcodeReq(
        Guid BranchId,
        Guid? CustomerId,
        string Barcode,
        decimal? UnitPricePerGram,   // opsiyonel: gram fiyatı
        decimal? FinalPrice          // opsiyonel: toplam fiyat
    );

    [HttpPost("by-barcode")]
    public async Task<IActionResult> CreateByBarcode([FromBody] CreateSaleByBarcodeReq req, CancellationToken ct)
    {
        // 1) Kullanıcı
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        // 2) Barkodlu Parçayı Bul
        if (string.IsNullOrWhiteSpace(req.Barcode))
            return BadRequest(new { error = "Barcode zorunludur." });

        var item = await _db.ProductItems
                            .Include(pi => pi.Product)
                            .FirstOrDefaultAsync(pi => pi.Barcode == req.Barcode, ct);

        if (item is null) return NotFound(new { error = "Barkod bulunamadı." });
        if (!item.IsInStock) return BadRequest(new { error = "Parça stokta değil / zaten satılmış." });
        if (item.BranchId != req.BranchId)
            return BadRequest(new { error = "Parça başka şube stokunda." });

        var product = item.Product;
        if (product is null)
            return BadRequest(new { error = "Parçaya bağlı ürün bulunamadı." });

        // 3) Fiyatı hesapla
        decimal totalPrice;
        decimal unitPrice;

        if (req.FinalPrice.HasValue)
        {
            totalPrice = Math.Round(req.FinalPrice.Value, 2);
            unitPrice = item.Weight > 0 ? Math.Round(totalPrice / item.Weight, 2) : totalPrice;
        }
        else if (req.UnitPricePerGram.HasValue)
        {
            unitPrice = Math.Round(req.UnitPricePerGram.Value, 2);
            totalPrice = Math.Round(unitPrice * item.Weight, 2);
        }
        else
        {
            return BadRequest(new { error = "FinalPrice veya UnitPricePerGram alanlarından en az biri gerekli." });
        }

        var tenantId = item.TenantId; // Tenant ID'yi tekil üründen alıyoruz.

        // 4) Satış + kalem + stok (TRANSACTION)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Satış başlığı (Artık sadece temel alanlar)
            var sale = new Sale
            {
                TenantId = tenantId,
                BranchId = req.BranchId,
                UserId = userId,
                CustomerId = req.CustomerId,
            };
            _db.Sales.Add(sale);
            await _db.SaveChangesAsync(ct); // sale.Id

            // Satış kalemi (1 satır) - ProductItemId eklendi
            var line = new SaleItem
            {
                TenantId = tenantId,
                SaleId = sale.Id,
                LineNo = 1,
                Kind = ItemKind.Product,
                ProductCode = product.ProductCode,
                ProductName = product.Name,
                Karat = item.Karat,
                Category = product.Category,
                Quantity = item.Weight,
                UnitPrice = unitPrice,
                Discount = 0m,
                TaxRate = 0m,
                LineTotal = totalPrice,
                ProductItemId = item.Id // << CRITICAL: Tekil parça bağlantısı
            };
            _db.SaleItems.Add(line);

            // 5) Stok düş (yeni IStockService imzası)
            await _stock.AdjustAsync(
                branchId: sale.BranchId,
                productId: product.Id,
                productItemId: item.Id,                 // << ProductItemId gönderiliyor
                deltaQuantity: -item.Weight,            // çıkış
                refKind: StockRefKind.Sale,
                refId: sale.Id,
                note: $"Sale {sale.Id} - {item.Barcode}",
                ct: ct
            );

            // 6) Parçayı “satıldı” yap
            item.IsInStock = false;
            item.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return CreatedAtAction("GetById", "SalesV2", new { id = sale.Id }, new { sale.Id }); // V2'ye yönlendirme
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // DELETE (soft + stok iadesi) - Birden fazla kalem (SaleItem) yönetimi eklendi
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var s = await _db.Sales.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound();
        if (s.IsDeleted) return NoContent();

        var tenantId = s.TenantId;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            s.IsDeleted = true;
            await _db.SaveChangesAsync(ct);

            // Her bir kalemi stoktan iade et ve ProductItem'ı stoğa geri al
            foreach (var item in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == item.ProductCode, ct);

                if (product is not null && item.Quantity > 0)
                {
                    // Stok iadesi (yeni IStockService imzası)
                    await _stock.AdjustAsync(
                        branchId: s.BranchId,
                        productId: product.Id,
                        productItemId: item.ProductItemId, // << ProductItemId kullanılıyor
                        deltaQuantity: +item.Quantity,    // satış silinince iade
                        refKind: StockRefKind.Sale,
                        refId: s.Id,
                        note: "Sale deleted (item)",
                        ct: ct
                    );

                    // Eğer tekil parça satıldıysa, stoğa geri al
                    if (item.ProductItemId.HasValue)
                    {
                        var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == item.ProductItemId.Value, ct);
                        if (pItem is not null)
                        {
                            pItem.IsInStock = true;
                            pItem.UpdatedAt = DateTime.UtcNow;
                        }
                    }
                }
            }

            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
//// Kuyumcu.PriceService/Controllers/SalesController.cs
//using System.Security.Claims;
//using kuyumcu_domain.Entities;
//using kuyumcu_infrastructure.Persistence;
//using kuyumcu_domain.Enums;             // StockRefKind
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using kuyumcu_application.Abstractions;
//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/[controller]")]
//[Authorize] // JWT zorunlu
//public class SalesController : ControllerBase
//{
//    private readonly AppDbContext _db;
//    private readonly IStockService _stock;
//    public SalesController(AppDbContext db, IStockService stock)
//    {
//        _db = db;
//        _stock = stock;
//    }

//    // CREATE
//    [HttpPost]
//    // CREATE
//    [HttpPost]
//    public async Task<IActionResult> Create([FromBody] CreateSaleDto dto, CancellationToken ct)
//    {
//        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(userIdStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        // Branch kontrolü
//        var branchExists = await _db.Branches.AsNoTracking().AnyAsync(b => b.Id == dto.BranchId, ct);
//        if (!branchExists)
//            return BadRequest(new { error = "Geçersiz BranchId." });

//        // Product kontrolü
//        if (string.IsNullOrWhiteSpace(dto.ProductCode))
//            return BadRequest(new { error = "ProductCode zorunlu." });

//        var product = await _db.Products.AsNoTracking()
//            .FirstOrDefaultAsync(p => p.ProductCode == dto.ProductCode, ct);
//        if (product is null)
//            return BadRequest(new { error = "Geçersiz ProductCode." });

//        // Customer (opsiyonel)
//        if (dto.CustomerId.HasValue)
//        {
//            var custOk = await _db.Customers.AsNoTracking()
//                .AnyAsync(c => c.Id == dto.CustomerId.Value && !c.IsDeleted, ct);
//            if (!custOk)
//                return BadRequest(new { error = "Geçersiz CustomerId." });
//        }

//        var total = dto.TotalPrice ?? (dto.Quantity * dto.UnitPrice);

//        var sale = new Sale
//        {
//            BranchId = dto.BranchId,
//            UserId = userId,
//            CustomerId = dto.CustomerId,
//            ProductCode = dto.ProductCode.Trim(),
//            ProductName = dto.ProductName?.Trim() ?? "",
//            Karat = dto.Karat?.Trim() ?? "",
//            Quantity = dto.Quantity,
//            UnitPrice = dto.UnitPrice,
//            TotalPrice = total
//        };

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            // 1) Sale kaydı
//            _db.Sales.Add(sale);
//            await _db.SaveChangesAsync(ct);

//            // 2) Tek kalemlik satış için otomatik SaleItem (LineNo=1)
//            var item = new SaleItem
//            {
//                SaleId = sale.Id,
//                LineNo = 1,
//                Kind = ItemKind.Unknown,
//                ProductCode = sale.ProductCode,
//                ProductName = sale.ProductName,
//                Karat = sale.Karat,
//                Category = null,
//                Quantity = sale.Quantity,
//                UnitPrice = sale.UnitPrice,
//                Discount = 0m,
//                TaxRate = 0m,
//                LineTotal = sale.TotalPrice
//            };
//            _db.SaleItems.Add(item);
//            await _db.SaveChangesAsync(ct);

//            // 3) Stok çıkışı
//            await _stock.AdjustAsync(
//                branchId: sale.BranchId,
//                productId: product.Id,
//                deltaQuantity: -sale.Quantity,       // satış: çıkış
//                refKind: StockRefKind.Sale,
//                refId: sale.Id,
//                note: $"Sale {sale.Id}",
//                ct: ct
//            );

//            await tx.CommitAsync(ct);
//            return CreatedAtAction(nameof(GetById), new { id = sale.Id }, new { sale.Id });
//        }
//        catch (DbUpdateException ex)
//        {
//            await tx.RollbackAsync(ct);
//            return Problem(title: "Satış kaydedilemedi",
//                           detail: ex.InnerException?.Message ?? ex.Message,
//                           statusCode: StatusCodes.Status400BadRequest);
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }

//    //// CREATE
//    //[HttpPost]
//    //public async Task<IActionResult> Create([FromBody] CreateSaleDto dto, CancellationToken ct)
//    //{
//    //    var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//    //    if (!Guid.TryParse(userIdStr, out var userId))
//    //        return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı" });

//    //    // Ürünü koda göre bul (stok için lazım)
//    //    var product = await _db.Products.AsNoTracking()
//    //        .FirstOrDefaultAsync(p => p.ProductCode == dto.ProductCode, ct);
//    //    if (product is null)
//    //        return BadRequest(new { error = "Geçersiz ProductCode" });

//    //    var total = dto.TotalPrice ?? (dto.Quantity * dto.UnitPrice);

//    //    var sale = new Sale
//    //    {
//    //        BranchId = dto.BranchId,
//    //        UserId = userId,
//    //        CustomerId = dto.CustomerId,
//    //        ProductCode = dto.ProductCode?.Trim() ?? "",
//    //        ProductName = dto.ProductName?.Trim() ?? "",
//    //        Karat = dto.Karat?.Trim() ?? "",
//    //        Quantity = dto.Quantity,
//    //        UnitPrice = dto.UnitPrice,
//    //        TotalPrice = total
//    //    };

//    //    await using var tx = await _db.Database.BeginTransactionAsync(ct);
//    //    try
//    //    {
//    //        _db.Sales.Add(sale);
//    //        await _db.SaveChangesAsync(ct); // sale.Id üretildi

//    //        // stok çıkışı (−)
//    //        await _stock.AdjustAsync(
//    //            branchId: sale.BranchId,
//    //            productId: product.Id,
//    //            deltaQuantity: -sale.Quantity,    // satış: çıkış
//    //            refKind: StockRefKind.Sale,
//    //            refId: sale.Id,
//    //            note: $"Sale {sale.Id}",
//    //            ct: ct
//    //        );

//    //        await tx.CommitAsync(ct);
//    //        return CreatedAtAction(nameof(GetById), new { id = sale.Id }, new { sale.Id });
//    //    }
//    //    catch
//    //    {
//    //        await tx.RollbackAsync(ct);
//    //        throw;
//    //    }
//    //}


//    //[HttpPost]
//    //public async Task<IActionResult> Create([FromBody] CreateSaleDto dto, CancellationToken ct)
//    //{
//    //    var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//    //    if (!Guid.TryParse(userIdStr, out var userId))
//    //        return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//    //    var total = dto.TotalPrice ?? (dto.Quantity * dto.UnitPrice);

//    //    var sale = new Sale
//    //    {
//    //        BranchId = dto.BranchId,
//    //        UserId = userId,
//    //        CustomerId = dto.CustomerId,
//    //        ProductCode = dto.ProductCode?.Trim() ?? "",
//    //        ProductName = dto.ProductName?.Trim() ?? "",
//    //        Karat = dto.Karat?.Trim() ?? "",
//    //        Quantity = dto.Quantity,
//    //        UnitPrice = dto.UnitPrice,
//    //        TotalPrice = total
//    //    };

//    //    _db.Sales.Add(sale);
//    //    await _db.SaveChangesAsync(ct);
//    //    await _db.AdjustAsync(product.Id, -dto.Quantity, "Sale", sale.Id, $"Sale {sale.Id}", ct);

//    //    return CreatedAtAction(nameof(GetById), new { id = sale.Id }, ToDto(sale));
//    //}

//    // LIST (filtre + sayfalama)
//    [HttpGet]
//    public async Task<IActionResult> List(
//        [FromQuery] Guid? branchId,
//        [FromQuery] Guid? customerId,
//        [FromQuery] DateTime? from,
//        [FromQuery] DateTime? to,
//        [FromQuery] int page = 1,
//        [FromQuery] int pageSize = 20,
//        CancellationToken ct = default)
//    {
//        if (page <= 0) page = 1;
//        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

//        var q = _db.Sales
//            .AsNoTracking()
//            .Where(x => !x.IsDeleted)
//            .OrderByDescending(x => x.CreatedAt)
//            .AsQueryable();

//        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
//        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
//        if (from.HasValue) q = q.Where(x => x.CreatedAt >= from.Value);
//        if (to.HasValue) q = q.Where(x => x.CreatedAt < to.Value);

//        var total = await q.CountAsync(ct);
//        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

//        return Ok(new
//        {
//            total,
//            page,
//            pageSize,
//            items = items.Select(ToDto)
//        });
//    }

//    // GET BY ID
//    [HttpGet("{id:guid}")]
//    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
//    {
//        var s = await _db.Sales.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
//        return s is null ? NotFound() : Ok(ToDto(s));
//    }

//    // UPDATE
//    [HttpPut("{id:guid}")]
//    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleDto dto, CancellationToken ct)
//    {
//        var s = await _db.Sales.FirstOrDefaultAsync(x => x.Id == id, ct);
//        if (s is null) return NotFound();

//        if (!string.Equals(s.ProductCode, dto.ProductCode, StringComparison.OrdinalIgnoreCase))
//            return BadRequest(new { error = "ProductCode değişikliği şu an desteklenmiyor." });

//        // Branch / Customer doğrulamaları
//        var branchOk = await _db.Branches.AsNoTracking().AnyAsync(b => b.Id == dto.BranchId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId." });

//        if (dto.CustomerId.HasValue)
//        {
//            var custOk = await _db.Customers.AsNoTracking()
//                .AnyAsync(c => c.Id == dto.CustomerId.Value && !c.IsDeleted, ct);
//            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId." });
//        }

//        var oldQty = s.Quantity;

//        s.BranchId = dto.BranchId;
//        s.CustomerId = dto.CustomerId;
//        s.ProductName = dto.ProductName?.Trim() ?? "";
//        s.Karat = dto.Karat?.Trim() ?? "";
//        s.Quantity = dto.Quantity;
//        s.UnitPrice = dto.UnitPrice;
//        s.TotalPrice = dto.TotalPrice ?? (dto.Quantity * dto.UnitPrice);

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            await _db.SaveChangesAsync(ct);

//            // İlgili tek kalem SaleItem'ı da güncelle (LineNo=1 varsayımı)
//            var item = await _db.SaleItems.FirstOrDefaultAsync(i => i.SaleId == s.Id && i.LineNo == 1, ct);
//            if (item is not null)
//            {
//                item.ProductName = s.ProductName;
//                item.Karat = s.Karat;
//                item.Quantity = s.Quantity;
//                item.UnitPrice = s.UnitPrice;
//                item.LineTotal = s.TotalPrice;
//                await _db.SaveChangesAsync(ct);
//            }

//            // Stok delta uygula
//            var delta = s.Quantity - oldQty; // + arttı -> ekstra çıkış, - azaldı -> iade
//            if (delta != 0)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.ProductCode == s.ProductCode, ct);
//                if (product is not null)
//                {
//                    await _stock.AdjustAsync(
//                        branchId: s.BranchId,
//                        productId: product.Id,
//                        deltaQuantity: -delta,
//                        refKind: StockRefKind.Sale,
//                        refId: s.Id,
//                        note: "Sale update",
//                        ct: ct
//                    );
//                }
//            }

//            await tx.CommitAsync(ct);
//            return Ok(ToDto(s));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }


//    //// UPDATE
//    //[HttpPut("{id:guid}")]
//    //public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleDto dto, CancellationToken ct)
//    //{
//    //    var s = await _db.Sales.FirstOrDefaultAsync(x => x.Id == id, ct);
//    //    if (s is null) return NotFound();

//    //    if (!string.Equals(s.ProductCode, dto.ProductCode, StringComparison.OrdinalIgnoreCase))
//    //        return BadRequest(new { error = "ProductCode değişikliği şu an desteklenmiyor." });

//    //    var oldQty = s.Quantity;

//    //    await using var tx = await _db.Database.BeginTransactionAsync(ct);
//    //    try
//    //    {
//    //        // yeni değerler
//    //        s.BranchId = dto.BranchId;
//    //        s.CustomerId = dto.CustomerId;
//    //        s.ProductName = dto.ProductName?.Trim() ?? "";
//    //        s.Karat = dto.Karat?.Trim() ?? "";
//    //        s.Quantity = dto.Quantity;
//    //        s.UnitPrice = dto.UnitPrice;
//    //        s.TotalPrice = dto.TotalPrice ?? (dto.Quantity * dto.UnitPrice);

//    //        await _db.SaveChangesAsync(ct);

//    //        // stok farkı uygula
//    //        var delta = s.Quantity - oldQty; // + arttı, - azaldı
//    //        if (delta != 0)
//    //        {
//    //            var product = await _db.Products.AsNoTracking()
//    //                .FirstOrDefaultAsync(p => p.ProductCode == s.ProductCode, ct);
//    //            if (product is not null)
//    //            {
//    //                await _stock.AdjustAsync(
//    //                    branchId: s.BranchId,
//    //                    productId: product.Id,
//    //                    deltaQuantity: -delta,        // delta>0 => ekstra çıkış
//    //                    refKind: StockRefKind.Sale,
//    //                    refId: s.Id,
//    //                    note: "Sale update",
//    //                    ct: ct
//    //                );
//    //            }
//    //        }

//    //        await tx.CommitAsync(ct);
//    //        return Ok(ToDto(s));
//    //    }
//    //    catch
//    //    {
//    //        await tx.RollbackAsync(ct);
//    //        throw;
//    //    }
//    //}


//    //[HttpPut("{id:guid}")]
//    //public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleDto dto, CancellationToken ct)
//    //{
//    //    var oldQty = s.Quantity;
//    //    var s = await _db.Sales.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
//    //    if (s is null) return NotFound();

//    //    s.BranchId = dto.BranchId;
//    //    s.CustomerId = dto.CustomerId;
//    //    s.ProductCode = dto.ProductCode?.Trim() ?? "";
//    //    s.ProductName = dto.ProductName?.Trim() ?? "";
//    //    s.Karat = dto.Karat?.Trim() ?? "";
//    //    s.Quantity = dto.Quantity;
//    //    s.UnitPrice = dto.UnitPrice;
//    //    s.TotalPrice = dto.TotalPrice ?? (dto.Quantity * dto.UnitPrice);

//    //    await _db.SaveChangesAsync(ct);
//    //    var delta = s.Quantity - oldQty; // arttıysa +, azaldıysa -
//    //    if (delta != 0)
//    //        await _stock.AdjustAsync(product.Id, -delta, "SaleUpdate", s.Id, null, ct);
//    //    return Ok(ToDto(s));

//    //}

//    // DELETE (soft)
//    // DELETE (soft + stok iadesi)
//    [HttpDelete("{id:guid}")]
//    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    {
//        var s = await _db.Sales.FirstOrDefaultAsync(x => x.Id == id, ct);
//        if (s is null) return NotFound();
//        if (s.IsDeleted) return NoContent();

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            s.IsDeleted = true;
//            await _db.SaveChangesAsync(ct);

//            var product = await _db.Products.AsNoTracking()
//                .FirstOrDefaultAsync(p => p.ProductCode == s.ProductCode, ct);

//            if (product is not null && s.Quantity > 0)
//            {
//                await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +s.Quantity,    // satış silinince iade
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale deleted",
//                    ct: ct
//                );
//            }

//            await tx.CommitAsync(ct);
//            return NoContent();
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }



//    //[HttpDelete("{id:guid}")]
//    //public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    //{
//    //    var s = await _db.Sales.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
//    //    if (s is null) return NotFound();

//    //    s.IsDeleted = true; // soft delete
//    //    await _db.SaveChangesAsync(ct);
//    //    var product = await _db.Products.FirstOrDefaultAsync(p => p.ProductCode == s.ProductCode, ct);
//    //    if (product != null)
//    //        await _stock.AdjustAsync(product.Id, +s.Quantity, "SaleDelete", s.Id, null, ct);

//    //    return NoContent();
//    //}

//    private static SaleDto ToDto(Sale s) =>
//        new(
//            s.Id,
//            s.BranchId,
//            s.UserId,
//            s.CustomerId,
//            s.ProductCode,
//            s.ProductName,
//            s.Karat,
//            s.Quantity,
//            s.UnitPrice,
//            s.TotalPrice,
//            s.CreatedAt
//        );

//    // KUYUMCU.Price_Service/Controllers/SalesController.cs (içine ek)
//    // DTO: Barkodla satış
//    public sealed record CreateSaleByBarcodeReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        string Barcode,
//        decimal? UnitPricePerGram,   // opsiyonel: gram fiyatı
//        decimal? FinalPrice          // opsiyonel: toplam fiyat
//    );

//    [HttpPost("by-barcode")]
//    public async Task<IActionResult> CreateByBarcode([FromBody] CreateSaleByBarcodeReq req, CancellationToken ct)
//    {
//        // 1) Kullanıcı
//        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(userIdStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        // 2) Branch doğrulama
//        var branchOk = await _db.Branches.AsNoTracking().AnyAsync(b => b.Id == req.BranchId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId." });

//        // 3) Ürünü barkoda göre bul
//        if (string.IsNullOrWhiteSpace(req.Barcode))
//            return BadRequest(new { error = "Barcode zorunludur." });

//        var item = await _db.ProductItems
//                            .Include(pi => pi.Product)
//                            .FirstOrDefaultAsync(pi => pi.Barcode == req.Barcode, ct);

//        if (item is null) return NotFound(new { error = "Barkod bulunamadı." });
//        if (!item.IsInStock) return BadRequest(new { error = "Parça stokta değil / zaten satılmış." });
//        if (item.BranchId != req.BranchId)
//            return BadRequest(new { error = "Parça başka şube stokunda." });

//        var product = item.Product; // null olmamalı, config’te required
//        if (product is null)
//            return BadRequest(new { error = "Parçaya bağlı ürün bulunamadı." });

//        // 4) Fiyatı hesapla
//        // - Eğer FinalPrice verilmişse onu kullan
//        // - Değilse UnitPricePerGram * item.Weight
//        decimal totalPrice;
//        decimal unitPrice;

//        if (req.FinalPrice.HasValue)
//        {
//            totalPrice = Math.Round(req.FinalPrice.Value, 2);
//            unitPrice = item.Weight > 0 ? Math.Round(totalPrice / item.Weight, 2) : totalPrice;
//        }
//        else if (req.UnitPricePerGram.HasValue)
//        {
//            unitPrice = Math.Round(req.UnitPricePerGram.Value, 2);
//            totalPrice = Math.Round(unitPrice * item.Weight, 2);
//        }
//        else
//        {
//            return BadRequest(new { error = "FinalPrice veya UnitPricePerGram alanlarından en az biri gerekli." });
//        }

//        // 5) Satış + kalem + stok (TRANSACTION)
//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            // Satış başlığı
//            var sale = new Sale
//            {
//                BranchId = req.BranchId,
//                UserId = userId,
//                CustomerId = req.CustomerId,
//                ProductCode = product.ProductCode,
//                ProductName = product.Name,
//                Karat = item.Karat,
//                Quantity = item.Weight,     // tekil parça ağırlığı
//                UnitPrice = unitPrice,       // gram fiyatı
//                TotalPrice = totalPrice
//            };
//            _db.Sales.Add(sale);
//            await _db.SaveChangesAsync(ct); // sale.Id

//            // Satış kalemi (1 satır)
//            var line = new SaleItem
//            {
//                SaleId = sale.Id,
//                LineNo = 1,
//                Kind = ItemKind.Product,          // ihtiyacına göre
//                ProductCode = product.ProductCode,
//                ProductName = product.Name,
//                Karat = item.Karat,
//                Category = product.Category,
//                Quantity = item.Weight,               // gram/adet
//                UnitPrice = unitPrice,
//                Discount = 0m,
//                TaxRate = 0m,
//                LineTotal = totalPrice
//            };
//            _db.SaleItems.Add(line);

//            // Stok düş (parçaya bağlayarak)
//            await _stock.AdjustAsync(
//                branchId: sale.BranchId,
//                productId: product.Id,
//                productItemId: item.Id,                 // << hareketi parçaya bağla
//                deltaQuantity: -item.Weight,            // çıkış
//                refKind: StockRefKind.Sale,
//                refId: sale.Id,
//                note: $"Sale {sale.Id} - {item.Barcode}",
//                ct: ct
//            );

//            // Parçayı “satıldı” yap
//            item.IsInStock = false;
//            item.UpdatedAt = DateTime.UtcNow;
//            await _db.SaveChangesAsync(ct);

//            await tx.CommitAsync(ct);
//            return CreatedAtAction(nameof(GetById), new { id = sale.Id }, new { sale.Id });
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }

//}
