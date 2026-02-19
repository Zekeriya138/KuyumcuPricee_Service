using System.Security.Claims;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq; // LINQ için eklendi

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/sales/v2")]
[Authorize]
public sealed class SalesV2Controller : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStockService _stock;

    public SalesV2Controller(AppDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    // -------- Tenant helper --------
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
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSaleReqV2 req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // user
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        // branch (tenant ile)
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.BranchId && x.TenantId == tenantId, ct);
        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        // items
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "En az bir satış kalemi zorunludur." });

        // müşteri (opsiyonel) tenant kontrol
        if (req.CustomerId is Guid cid)
        {
            var custOk = await _db.Customers.AsNoTracking()
                .AnyAsync(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
        }

        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            BranchId = branch.Id,
            UserId = userId,
            CustomerId = req.CustomerId,
            Items = new List<SaleItem>()
        };


        int lineNo = 0;
        decimal subtotal = 0m, discTot = 0m, taxTot = 0m, grandTot = 0m;

        foreach (var it in req.Items)
        {
            if (string.IsNullOrWhiteSpace(it.ProductCode))
                return BadRequest(new { error = "ProductCode zorunludur." });

            var product = await _db.Products.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
            if (product is null)
                return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });


            // --- ProductItem / Barcode Kontrolü (Tekil Parça) ---
            if (it.ProductItemId.HasValue)
            {
                var itemId = it.ProductItemId.Value;
                var itemEntity = await _db.ProductItems
                    .FirstOrDefaultAsync(pi => pi.Id == itemId && pi.TenantId == tenantId, ct);

                if (itemEntity is null)
                    return BadRequest(new { error = $"Geçersiz ProductItemId: {itemId}" });

                if (itemEntity.BranchId != branch.Id)
                    return BadRequest(new { error = $"ProductItemId {itemId} bu şubeye ait değil." });

                if (!itemEntity.IsInStock)
                    return BadRequest(new { error = $"ProductItemId {itemId} stokta değil/satılmış." });

                // Tekil parça satılıyorsa, miktar parça ağırlığına eşit olmalıdır.
                if (it.Quantity != itemEntity.Weight)
                    return BadRequest(new { error = $"Tekil parça (ID: {itemId}) satılırken miktar ({it.Quantity}) parça ağırlığına ({itemEntity.Weight}) eşit olmalıdır." });
            }
            // --- ProductItem / Barcode Kontrolü SONU ---

            var lineBase = it.UnitPrice * it.Quantity;
            var afterDisc = lineBase - it.Discount;
            var tax = afterDisc * it.TaxRate;
            var lineTotal = Math.Round(afterDisc + tax, 2);

            subtotal += Math.Round(lineBase, 2);
            discTot += Math.Round(it.Discount, 2);
            taxTot += Math.Round(tax, 2);
            grandTot += lineTotal;

            sale.Items.Add(new SaleItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SaleId = sale.Id,
                LineNo = it.LineNo ?? ++lineNo,
                ProductCode = it.ProductCode.Trim(),
                ProductName = it.ProductName?.Trim() ?? "",
                Karat = it.Karat?.Trim() ?? "",
                Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
                Quantity = it.Quantity,
                UnitPrice = it.UnitPrice,
                Discount = it.Discount,
                TaxRate = it.TaxRate,
                LineTotal = lineTotal,
                ProductItemId = it.ProductItemId // << Bağlantı Eklendi
            });

        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.Sales.Add(sale);
            _db.SaleItems.AddRange(sale.Items); // SaleItem'ları toplu ekleme
            await _db.SaveChangesAsync(ct);

            // STOK ÇIKIŞLARI ve ProductItem güncelleme
            foreach (var si in sale.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
                if (product is null) continue;

                // Stok AdjustAsync çağrısı güncellendi:
                await _stock.AdjustAsync(
                    branchId: sale.BranchId,
                    productId: product.Id,
                    productItemId: si.ProductItemId, // << ProductItemId gönderildi
                    deltaQuantity: -si.Quantity,
                    refKind: StockRefKind.Sale,
                    refId: sale.Id,
                    note: $"Sale {sale.Id} L{si.LineNo}",
                    ct: ct
                );

                // ProductItem'ı satıldı olarak işaretle
                if (si.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == si.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = false;
                        pItem.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
            await tx.CommitAsync(ct);

            CalcTotals(sale, out subtotal, out discTot, out taxTot, out grandTot); // Toplamları hesapla
            return CreatedAtAction(nameof(GetById), new { id = sale.Id }, ToDto(sale, subtotal, discTot, taxTot, grandTot));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw; // CS0161 hatasını çözer.

        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var s = await _db.Sales.AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);

        if (s is null) return NotFound();

        CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
        return Ok(ToDto(s, sub, disc, tax, grand));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleReqV2 req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var s = await _db.Sales.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (s is null) return NotFound();

        var branchOk = await _db.Branches.AsNoTracking()
            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        if (req.CustomerId.HasValue)
        {
            var custOk = await _db.Customers.AsNoTracking()
                .AnyAsync(c => c.Id == req.CustomerId.Value && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Eski kalemleri stoktan geri al ve ProductItem'ları stoğa geri ekle
            foreach (var old in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == old.ProductCode, ct);
                if (product is null) continue;

                // Stok AdjustAsync çağrısı güncellendi:
                await _stock.AdjustAsync(
                    branchId: s.BranchId,
                    productId: product.Id,
                    productItemId: old.ProductItemId, // << ProductItemId gönderildi
                    deltaQuantity: +old.Quantity, // (+) iade
                    refKind: StockRefKind.Sale,
                    refId: s.Id,
                    note: "Sale update (revert old)",
                    ct: ct
                );

                // ProductItem'ı stoğa geri al
                if (old.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == old.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = true;
                        pItem.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            s.BranchId = req.BranchId;
            s.CustomerId = req.CustomerId;

            s.Items.Clear();
            int lineNo = 0;
            // 2. Yeni kalemleri oluştur
            foreach (var it in (req.Items ?? new()))
            {
                if (string.IsNullOrWhiteSpace(it.ProductCode))
                    return BadRequest(new { error = "ProductCode zorunludur." });

                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
                if (product is null)
                    return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });

                // --- ProductItem Kontrolü ---
                if (it.ProductItemId.HasValue)
                {
                    var itemId = it.ProductItemId.Value;
                    var itemEntity = await _db.ProductItems
                        .FirstOrDefaultAsync(pi => pi.Id == itemId && pi.TenantId == tenantId, ct);

                    if (itemEntity is null || itemEntity.BranchId != s.BranchId || !itemEntity.IsInStock)
                        return BadRequest(new { error = $"Geçersiz veya stokta olmayan ProductItemId: {itemId}" });

                    if (it.Quantity != itemEntity.Weight)
                        return BadRequest(new { error = $"Tekil parça (ID: {itemId}) satılırken miktar ({it.Quantity}) parça ağırlığına ({itemEntity.Weight}) eşit olmalıdır." });
                }
                // --- Kontrol Sonu ---


                var lineBase = it.UnitPrice * it.Quantity;
                var afterDisc = lineBase - it.Discount;
                var lineTax = afterDisc * it.TaxRate;
                var lineTotal = Math.Round(afterDisc + lineTax, 2);

                s.Items.Add(new SaleItem
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SaleId = s.Id,
                    LineNo = it.LineNo ?? ++lineNo,
                    ProductCode = it.ProductCode.Trim(),
                    ProductName = it.ProductName?.Trim() ?? "",
                    Karat = it.Karat?.Trim() ?? "",
                    Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
                    Quantity = it.Quantity,
                    UnitPrice = it.UnitPrice,
                    Discount = it.Discount,
                    TaxRate = it.TaxRate,
                    LineTotal = lineTotal,
                    ProductItemId = it.ProductItemId
                });
            }

            await _db.SaveChangesAsync(ct);

            // 3. Yeni kalemleri stoktan düş ve ProductItem'ları satıldı yap
            foreach (var si in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
                if (product is null) continue;

                // Stok AdjustAsync çağrısı güncellendi:
                await _stock.AdjustAsync(
                    branchId: s.BranchId,
                    productId: product.Id,
                    productItemId: si.ProductItemId, // << ProductItemId gönderildi
                    deltaQuantity: -si.Quantity, // (-) çıkış
                    refKind: StockRefKind.Sale,
                    refId: s.Id,
                    note: "Sale update (apply)",
                    ct: ct
                );

                // ProductItem'ı satıldı yap
                if (si.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == si.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = false;
                        pItem.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
            await tx.CommitAsync(ct);

            CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
            return Ok(ToDto(s, sub, disc, tax, grand));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var s = await _db.Sales.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (s is null) return NotFound();
        if (s.IsDeleted) return NoContent();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            s.IsDeleted = true;
            await _db.SaveChangesAsync(ct);

            foreach (var it in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
                if (product is null) continue;

                // Stok AdjustAsync çağrısı güncellendi:
                await _stock.AdjustAsync(
                    branchId: s.BranchId,
                    productId: product.Id,
                    productItemId: it.ProductItemId, // << ProductItemId gönderildi
                    deltaQuantity: +it.Quantity,
                    refKind: StockRefKind.Sale,
                    refId: s.Id,
                    note: "Sale deleted",
                    ct: ct
                );

                // ProductItem'ı stoğa geri al
                if (it.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == it.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = true;
                        pItem.UpdatedAt = DateTime.UtcNow;
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

    // helpers
    private static void CalcTotals(Sale s, out decimal subtotal, out decimal disc, out decimal tax, out decimal grand)
    {
        subtotal = 0m; disc = 0m; tax = 0m; grand = 0m;
        foreach (var i in (s.Items ?? new List<SaleItem>()))
        {
            var lineBase = i.UnitPrice * i.Quantity;
            var afterDisc = lineBase - i.Discount;
            var t = afterDisc * i.TaxRate;
            var lineTot = Math.Round(afterDisc + t, 2);

            subtotal += Math.Round(lineBase, 2);
            disc += Math.Round(i.Discount, 2);
            tax += Math.Round(t, 2);
            grand += lineTot;
        }
    }

    // (DTO tanımı burada bulunmamaktadır, ancak ToDto metodu SaleItemDtoV2'ye bağlıdır)
    // SaleItemDtoV2'nin ProductItemId alanını döndürdüğünden emin olun
    private static SaleDtoV2 ToDto(Sale s, decimal subtotal, decimal disc, decimal tax, decimal grand)
        => new(
            s.Id, s.BranchId, s.UserId, s.CustomerId,
            Subtotal: subtotal,
            DiscountTotal: disc,
            TaxTotal: tax,
            GrandTotal: grand,
            CreatedAt: s.CreatedAt,
            Items: (s.Items ?? new()).Select(i =>
                new SaleItemDtoV2(
                    i.Id, i.SaleId, i.LineNo, i.ProductCode, i.ProductName, i.Karat, i.Category,
                    i.Quantity, i.UnitPrice, i.Discount, i.TaxRate, i.LineTotal,
                    i.ProductItemId // << Güncellendi
                )
            ).ToList()
        );
}
//using System.Security.Claims;
//using kuyumcu_application.Abstractions;
//using kuyumcu_domain.Entities;
//using kuyumcu_domain.Enums;
//using kuyumcu_infrastructure.Persistence;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;

//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/sales/v2")]
//[Authorize]
//public sealed class SalesV2Controller : ControllerBase
//{
//    private readonly AppDbContext _db;
//    private readonly IStockService _stock;

//    public SalesV2Controller(AppDbContext db, IStockService stock)
//    {
//        _db = db;
//        _stock = stock;
//    }

//    // -------- Tenant helper --------
//    private Guid GetTenantId()
//    {
//        var claim = User?.Claims?.FirstOrDefault(c =>
//            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;

//        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
//            return fromJwt;

//        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
//            Guid.TryParse(hdr.ToString(), out var fromHdr))
//            return fromHdr;

//        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
//    }
//    [HttpPost]
//    public async Task<IActionResult> Create([FromBody] CreateSaleReqV2 req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        // user
//        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(uidStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        // branch (tenant ile)
//        var branch = await _db.Branches.AsNoTracking()
//            .FirstOrDefaultAsync(x => x.Id == req.BranchId && x.TenantId == tenantId, ct);
//        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        // items
//        if (req.Items is null || req.Items.Count == 0)
//            return BadRequest(new { error = "En az bir satış kalemi zorunludur." });

//        // müşteri (opsiyonel) tenant kontrol
//        if (req.CustomerId is Guid cid)
//        {
//            var custOk = await _db.Customers.AsNoTracking()
//                .AnyAsync(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted, ct);
//            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
//        }

//        var sale = new Sale
//        {
//            Id = Guid.NewGuid(),
//            TenantId = tenantId,
//            BranchId = branch.Id,
//            UserId = userId,
//            CustomerId = req.CustomerId,
//            Items = new List<SaleItem>()
//        };


//        int lineNo = 0;
//        decimal subtotal = 0m, discTot = 0m, taxTot = 0m, grandTot = 0m;

//        foreach (var it in req.Items)
//        {
//            // ... Product/ProductCode kontrolü ...

//            // --- ProductItem / Barcode Kontrolü (YENİ EKLENDİ) ---
//            if (it.ProductItemId.HasValue)
//            {
//                var itemId = it.ProductItemId.Value;
//                var itemEntity = await _db.ProductItems
//                    .FirstOrDefaultAsync(pi => pi.Id == itemId && pi.TenantId == tenantId, ct);

//                if (itemEntity is null)
//                    return BadRequest(new { error = $"Geçersiz ProductItemId: {itemId}" });

//                if (itemEntity.BranchId != branch.Id)
//                    return BadRequest(new { error = $"ProductItemId {itemId} bu şubeye ait değil." });

//                if (!itemEntity.IsInStock)
//                    return BadRequest(new { error = $"ProductItemId {itemId} stokta değil/satılmış." });

//                // Tekil parça satılıyorsa, miktar parça ağırlığına eşit olmalıdır.
//                if (it.Quantity != itemEntity.Weight)
//                    return BadRequest(new { error = $"Tekil parça (ID: {itemId}) satılırken miktar ({it.Quantity}) parça ağırlığına ({itemEntity.Weight}) eşit olmalıdır." });
//            }
//            // --- ProductItem / Barcode Kontrolü SONU ---

//            // ... (Fiyat hesaplama) ...

//            sale.Items.Add(new SaleItem
//            {
//                // ... (diğer alanlar) ...
//                ProductItemId = it.ProductItemId // << Bağlantı Eklendi
//            });

//        }

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            _db.Sales.Add(sale);
//            _db.SaleItems.AddRange(sale.Items); // SaleItem'ları toplu ekleme
//            await _db.SaveChangesAsync(ct);

//            // STOK ÇIKIŞLARI ve ProductItem güncelleme
//            foreach (var si in sale.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
//                if (product is null) continue;

//                // Stok AdjustAsync çağrısı - ProductItemId gönderiliyor
//                await _stock.AdjustAsync(
//                    branchId: sale.BranchId,
//                    productId: product.Id,
//                    productItemId: si.ProductItemId, // << ProductItemId gönderildi
//                    deltaQuantity: -si.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: sale.Id,
//                    note: $"Sale {sale.Id} L{si.LineNo}",
//                    ct: ct
//                );

//                // ProductItem'ı satıldı olarak işaretle (YENİ EKLENDİ)
//                if (si.ProductItemId.HasValue)
//                {
//                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == si.ProductItemId.Value, ct);
//                    if (pItem is not null)
//                    {
//                        pItem.IsInStock = false;
//                        pItem.UpdatedAt = DateTime.UtcNow;
//                    }
//                }
//            }

//            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
//            await tx.CommitAsync(ct);

//            // ... (Response DTO dönme) ...
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;

//        }
//        return Ok();
//    }
//    //[HttpPost]
//    //public async Task<IActionResult> Create([FromBody] CreateSaleReqV2 req, CancellationToken ct)
//    //{
//    //    var tenantId = GetTenantId();

//    //    // user
//    //    var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//    //    if (!Guid.TryParse(uidStr, out var userId))
//    //        return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//    //    // branch (tenant ile)
//    //    var branch = await _db.Branches.AsNoTracking()
//    //        .FirstOrDefaultAsync(x => x.Id == req.BranchId && x.TenantId == tenantId, ct);
//    //    if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//    //    // items
//    //    if (req.Items is null || req.Items.Count == 0)
//    //        return BadRequest(new { error = "En az bir satış kalemi zorunludur." });

//    //    // müşteri (opsiyonel) tenant kontrol
//    //    if (req.CustomerId is Guid cid)
//    //    {
//    //        var custOk = await _db.Customers.AsNoTracking()
//    //            .AnyAsync(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted, ct);
//    //        if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
//    //    }

//    //    var sale = new Sale
//    //    {
//    //        Id = Guid.NewGuid(),
//    //        TenantId = tenantId,
//    //        BranchId = branch.Id,
//    //        UserId = userId,
//    //        CustomerId = req.CustomerId,
//    //        ProductCode = "",
//    //        ProductName = "",
//    //        Karat = "",
//    //        Quantity = 0,
//    //        UnitPrice = 0,
//    //        TotalPrice = 0,
//    //        Items = new List<SaleItem>()
//    //    };

//    //    int lineNo = 0;
//    //    decimal subtotal = 0m, discTot = 0m, taxTot = 0m, grandTot = 0m;

//    //    foreach (var it in req.Items)
//    //    {
//    //        if (string.IsNullOrWhiteSpace(it.ProductCode))
//    //            return BadRequest(new { error = "ProductCode zorunludur." });

//    //        var product = await _db.Products.AsNoTracking()
//    //            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//    //        if (product is null)
//    //            return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });

//    //        var lineBase = it.UnitPrice * it.Quantity;
//    //        var afterDisc = lineBase - it.Discount;
//    //        var tax = afterDisc * it.TaxRate;
//    //        var lineTotal = Math.Round(afterDisc + tax, 2);

//    //        subtotal += Math.Round(lineBase, 2);
//    //        discTot += Math.Round(it.Discount, 2);
//    //        taxTot += Math.Round(tax, 2);
//    //        grandTot += lineTotal;

//    //        sale.Items.Add(new SaleItem
//    //        {
//    //            Id = Guid.NewGuid(),
//    //            TenantId = tenantId,
//    //            SaleId = sale.Id,
//    //            LineNo = it.LineNo ?? ++lineNo,
//    //            ProductCode = it.ProductCode.Trim(),
//    //            ProductName = it.ProductName?.Trim() ?? "",
//    //            Karat = it.Karat?.Trim() ?? "",
//    //            Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
//    //            Quantity = it.Quantity,
//    //            UnitPrice = it.UnitPrice,
//    //            Discount = it.Discount,
//    //            TaxRate = it.TaxRate,
//    //            LineTotal = lineTotal
//    //        });
//    //    }

//    //    await using var tx = await _db.Database.BeginTransactionAsync(ct);
//    //    try
//    //    {
//    //        _db.Sales.Add(sale);
//    //        await _db.SaveChangesAsync(ct);

//    //        // STOK ÇIKIŞLARI
//    //        foreach (var si in sale.Items)
//    //        {
//    //            var product = await _db.Products.AsNoTracking()
//    //                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
//    //            if (product is null) continue;

//    //            await _stock.AdjustAsync(
//    //                branchId: sale.BranchId,
//    //                productId: product.Id,
//    //                deltaQuantity: -si.Quantity,
//    //                refKind: StockRefKind.Sale,
//    //                refId: sale.Id,
//    //                note: $"Sale {sale.Id} L{si.LineNo}",
//    //                ct: ct
//    //            );
//    //        }

//    //        await tx.CommitAsync(ct);

//    //        return CreatedAtAction(nameof(GetById), new { id = sale.Id }, ToDto(sale, subtotal, discTot, taxTot, grandTot));
//    //    }
//    //    catch
//    //    {
//    //        await tx.RollbackAsync(ct);
//    //        throw;
//    //    }
//    //}

//    [HttpGet("{id:guid}")]
//    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var s = await _db.Sales.AsNoTracking()
//            .Include(x => x.Items)
//            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);

//        if (s is null) return NotFound();

//        CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
//        return Ok(ToDto(s, sub, disc, tax, grand));
//    }

//    [HttpPut("{id:guid}")]
//    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleReqV2 req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var s = await _db.Sales.Include(x => x.Items)
//            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (s is null) return NotFound();

//        var branchOk = await _db.Branches.AsNoTracking()
//            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        if (req.CustomerId.HasValue)
//        {
//            var custOk = await _db.Customers.AsNoTracking()
//                .AnyAsync(c => c.Id == req.CustomerId.Value && c.TenantId == tenantId && !c.IsDeleted, ct);
//            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
//        }

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            // eski kalemleri stoktan geri al
//            foreach (var old in s.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == old.ProductCode, ct);
//                if (product is null) continue;

//                await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +old.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale update (revert old)",
//                    ct: ct
//                );
//            }

//            s.BranchId = req.BranchId;
//            s.CustomerId = req.CustomerId;

//            s.Items.Clear();
//            int lineNo = 0;
//            foreach (var it in (req.Items ?? new()))
//            {
//                if (string.IsNullOrWhiteSpace(it.ProductCode))
//                    return BadRequest(new { error = "ProductCode zorunludur." });

//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//                if (product is null)
//                    return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });

//                var lineBase = it.UnitPrice * it.Quantity;
//                var afterDisc = lineBase - it.Discount;
//                var lineTax = afterDisc * it.TaxRate;
//                var lineTotal = Math.Round(afterDisc + lineTax, 2);

//                s.Items.Add(new SaleItem
//                {
//                    Id = Guid.NewGuid(),
//                    TenantId = tenantId,
//                    SaleId = s.Id,
//                    LineNo = it.LineNo ?? ++lineNo,
//                    ProductCode = it.ProductCode.Trim(),
//                    ProductName = it.ProductName?.Trim() ?? "",
//                    Karat = it.Karat?.Trim() ?? "",
//                    Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
//                    Quantity = it.Quantity,
//                    UnitPrice = it.UnitPrice,
//                    Discount = it.Discount,
//                    TaxRate = it.TaxRate,
//                    LineTotal = lineTotal
//                });
//            }

//            await _db.SaveChangesAsync(ct);

//            // yeni kalemleri stoktan düş
//            foreach (var si in s.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
//                if (product is null) continue;

//                await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: -si.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale update (apply)",
//                    ct: ct
//                );
//            }

//            await tx.CommitAsync(ct);

//            CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
//            return Ok(ToDto(s, sub, disc, tax, grand));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }
//    [HttpDelete("{id:guid}")]
//    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var s = await _db.Sales.Include(x => x.Items)
//            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (s is null) return NotFound();
//        if (s.IsDeleted) return NoContent();

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            s.IsDeleted = true;
//            await _db.SaveChangesAsync(ct);

//            foreach (var it in s.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//                if (product is null) continue;
//                           await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    productItemId: it.ProductItemId, // << ProductItemId gönderildi
//                    deltaQuantity: +it.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale deleted",
//                    ct: ct
//                );

//                // ProductItem'ı stoğa geri al (YENİ EKLENDİ)
//                if (it.ProductItemId.HasValue)
//                {
//                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == it.ProductItemId.Value, ct);
//                    if (pItem is not null)
//                    {
//                        pItem.IsInStock = true;
//                        pItem.UpdatedAt = DateTime.UtcNow;
//                    }
//                }
//            }

//            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
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
//    //    var tenantId = GetTenantId();

//    //    var s = await _db.Sales.Include(x => x.Items)
//    //        .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//    //    if (s is null) return NotFound();
//    //    if (s.IsDeleted) return NoContent();

//    //    await using var tx = await _db.Database.BeginTransactionAsync(ct);
//    //    try
//    //    {
//    //        s.IsDeleted = true;
//    //        await _db.SaveChangesAsync(ct);

//    //        foreach (var it in s.Items)
//    //        {
//    //            var product = await _db.Products.AsNoTracking()
//    //                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//    //            if (product is null) continue;

//    //            await _stock.AdjustAsync(
//    //                branchId: s.BranchId,
//    //                productId: product.Id,
//    //                deltaQuantity: +it.Quantity,
//    //                refKind: StockRefKind.Sale,
//    //                refId: s.Id,
//    //                note: "Sale deleted",
//    //                ct: ct
//    //            );
//    //        }

//    //        await tx.CommitAsync(ct);
//    //        return NoContent();
//    //    }
//    //    catch
//    //    {
//    //        await tx.RollbackAsync(ct);
//    //        throw;
//    //    }
//    //}

//    // helpers
//    private static void CalcTotals(Sale s, out decimal subtotal, out decimal disc, out decimal tax, out decimal grand)
//    {
//        subtotal = 0m; disc = 0m; tax = 0m; grand = 0m;
//        foreach (var i in (s.Items ?? new List<SaleItem>()))
//        {
//            var lineBase = i.UnitPrice * i.Quantity;
//            var afterDisc = lineBase - i.Discount;
//            var t = afterDisc * i.TaxRate;
//            var lineTot = Math.Round(afterDisc + t, 2);

//            subtotal += Math.Round(lineBase, 2);
//            disc += Math.Round(i.Discount, 2);
//            tax += Math.Round(t, 2);
//            grand += lineTot;
//        }
//    }

//    private static SaleDtoV2 ToDto(Sale s, decimal subtotal, decimal disc, decimal tax, decimal grand)
//        => new(
//            s.Id, s.BranchId, s.UserId, s.CustomerId,
//            Subtotal: subtotal,
//            DiscountTotal: disc,
//            TaxTotal: tax,
//            GrandTotal: grand,
//            CreatedAt: s.CreatedAt,
//            Items: (s.Items ?? new()).Select(i =>
//                new SaleItemDtoV2(
//                    i.Id, i.SaleId, i.LineNo, i.ProductCode, i.ProductName, i.Karat, i.Category,
//                    i.Quantity, i.UnitPrice, i.Discount, i.TaxRate, i.LineTotal,
//                    /* ProductItemId */ null
//                )
//            ).ToList()
//        );
//}
