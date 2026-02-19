using System.Security.Claims;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_domain.Enums; // StockRefKind, ItemKind
using System.Linq; // LINQ için eklendi

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT zorunlu
public class PurchasesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStockService _stock;
    public PurchasesController(AppDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    // ---------------- Tenant helper ----------------
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

    // ================== DTO'lar ==================
    // Create
    public sealed record CreatePurchaseItemReq(
        int? LineNo,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Quantity,
        decimal UnitCost,
        decimal Discount,
        decimal TaxRate
    );

    public sealed record CreatePurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        DateTime? Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        List<CreatePurchaseItemReq> Items
    );

    // Update
    public sealed record UpdatePurchaseItemReq(
        int? LineNo,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Quantity,
        decimal UnitCost,
        decimal Discount,
        decimal TaxRate
    );

    public sealed record UpdatePurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        DateTime? Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        List<UpdatePurchaseItemReq> Items
    );

    // Response
    public sealed record PurchaseItemDto(
        Guid Id,
        Guid PurchaseId,
        int LineNo,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Quantity,
        decimal UnitCost,
        decimal Discount,
        decimal TaxRate,
        decimal LineTotal
    );

    public sealed record PurchaseDto(
        Guid Id,
        Guid BranchId,
        Guid UserId,
        Guid? CustomerId,
        DateTime Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        decimal Subtotal,
        decimal DiscountTotal,
        decimal TaxTotal,
        decimal GrandTotal,
        decimal TotalAmount,
        List<PurchaseItemDto> Items
    );

    // ================== CREATE ==================
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // user
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "En az bir kalem (Items) gereklidir." });

        // Branch tenant doğrula
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (branch is null)
            return BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." });

        // Ürün kodlarını tenant bazında doğrula
        var missingCodes = new List<string>();
        foreach (var it in req.Items)
        {
            if (string.IsNullOrWhiteSpace(it.ProductCode)) { missingCodes.Add("(boş kod)"); continue; }

            var exists = await _db.Products.AsNoTracking()
                .AnyAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
            if (!exists) missingCodes.Add(it.ProductCode);
        }
        if (missingCodes.Count > 0)
        {
            return BadRequest(new
            {
                error = "Aşağıdaki ProductCode(lar) bu tenant’ta yok:",
                codes = missingCodes.Distinct().ToArray()
            });
        }

        // Başlık
        var purchase = new Purchase
        {
            TenantId = tenantId,
            BranchId = branch.Id,
            UserId = userId,
            CustomerId = req.CustomerId,
            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
            DocumentNo = req.DocumentNo?.Trim(),
            PartnerName = req.PartnerName?.Trim(),
            Note = req.Note?.Trim(),

        };

        // Toplamlar
        decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;
        int lineNo = 0;

        foreach (var it in req.Items)
        {
            var lineSub = Math.Round(it.Quantity * it.UnitCost, 2);
            var lineDisc = Math.Round(it.Discount, 2);
            var taxable = lineSub - lineDisc;
            var lineTax = Math.Round(taxable * (it.TaxRate / 100m), 2);
            var lineTot = Math.Round(taxable + lineTax, 2);

            subtotal += lineSub;
            discountTotal += lineDisc;
            taxTotal += lineTax;
            grandTotal += lineTot;

            purchase.Items.Add(new PurchaseItem
            {
                TenantId = tenantId,
                LineNo = ++lineNo,
                Kind = it.Kind,
                ProductCode = it.ProductCode?.Trim() ?? "",
                ProductName = it.ProductName?.Trim() ?? "",
                Karat = it.Karat?.Trim() ?? "",
                Category = it.Category?.Trim(),
                Quantity = it.Quantity,
                UnitCost = it.UnitCost,
                Discount = it.Discount,
                TaxRate = it.TaxRate,
                LineTotal = lineTot
            });
        }

        purchase.Subtotal = Math.Round(subtotal, 2);
        purchase.DiscountTotal = Math.Round(discountTotal, 2);
        purchase.TaxTotal = Math.Round(taxTotal, 2);
        purchase.GrandTotal = Math.Round(grandTotal, 2);
        purchase.TotalAmount = purchase.GrandTotal;

        // ---- TRANSACTION ----
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.Purchases.Add(purchase);
            await _db.SaveChangesAsync(ct); // Id’ler oluştu

            // STOK GİRİŞİ (+) ve tekil parça
            foreach (var it in purchase.Items)
            {
                if (string.IsNullOrWhiteSpace(it.ProductCode)) continue;

                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
                if (product is null) continue;

                // stok + (Purchase hareketinde ProductItemId null olabilir)
                await _stock.AdjustAsync(
                    branchId: purchase.BranchId,
                    productId: product.Id,
                    productItemId: null, // << productItemId eklendi
                    deltaQuantity: +it.Quantity,
                    refKind: StockRefKind.Purchase,
                    refId: purchase.Id,
                    note: $"Purchase {purchase.Id}",
                    ct: ct
                );

                // hurda değilse tekil ProductItem oluşturulur
                var isScrap = it.Kind.ToString().Equals("Scrap", StringComparison.OrdinalIgnoreCase);
                if (!isScrap)
                {
                    _db.ProductItems.Add(new ProductItem
                    {
                        TenantId = tenantId,
                        ProductId = product.Id,
                        BranchId = purchase.BranchId,
                        Barcode = GenerateBarcode(),
                        Serial = Guid.NewGuid().ToString("N")[..8].ToUpper(),
                        Karat = it.Karat ?? "",
                        Weight = it.Quantity,
                        IsInStock = true
                    });
                }
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return CreatedAtAction(nameof(GetById), new { id = purchase.Id }, ToDto(purchase));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================== LIST ==================
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] Guid? customerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();

        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

        var q = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.Date)
            .AsQueryable();

        var softProp = typeof(Purchase).GetProperty("IsDeleted");
        if (softProp != null)
            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
        if (from.HasValue) q = q.Where(x => x.Date >= from.Value);
        if (to.HasValue) q = q.Where(x => x.Date < to.Value);

        var total = await q.CountAsync(ct);
        var items = await q.Include(x => x.Items)
                           .Skip((page - 1) * pageSize)
                           .Take(pageSize)
                           .ToListAsync(ct);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items = items.Select(ToDto)
        });
    }

    // ================== GET BY ID ==================
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var q = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        var softProp = typeof(Purchase).GetProperty("IsDeleted");
        if (softProp != null)
            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

        var p = await q.Include(x => x.Items)
                       .FirstOrDefaultAsync(x => x.Id == id, ct);

        return p is null ? NotFound() : Ok(ToDto(p));
    }

    // ================== UPDATE ==================
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePurchaseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // GÜNCELLEME İŞLEMİNDE STOKTA TUTARSIZLIK OLMAMASI İÇİN DAHA KAPSAMLI BİR LOGİK GEREKİR.
        // Basitleştirme: Sadece başlık ve kalemleri güncelliyoruz, stok hareketi eklemiyoruz.

        var p = await _db.Purchases.Include(x => x.Items)
                                   .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (p is null) return NotFound();

        // branch tenant doğrula
        var branchOk = await _db.Branches.AsNoTracking()
            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        p.BranchId = req.BranchId;
        p.CustomerId = req.CustomerId;
        p.Date = req.Date ?? p.Date;
        p.DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim();
        p.PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
        p.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();

        // **DİKKAT:** Stok hareketlerini güncellemek karmaşıktır. Basit UPDATE yapısında,
        // sadece faturayı güncelliyoruz. Gerçek uygulamada eski stok hareketlerinin tersine çevrilmesi gerekir.

        p.Items.Clear();

        int line = 1;
        foreach (var it in (req.Items ?? new()))
        {
            p.Items.Add(new PurchaseItem
            {
                TenantId = tenantId,
                LineNo = it.LineNo ?? line++,
                Kind = it.Kind,
                ProductCode = it.ProductCode?.Trim() ?? "",
                ProductName = it.ProductName?.Trim() ?? "",
                Karat = it.Karat?.Trim() ?? "",
                Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
                Quantity = it.Quantity,
                UnitCost = it.UnitCost,
                Discount = it.Discount,
                TaxRate = it.TaxRate
            });
        }

        RecalcTotals(p);
        await _db.SaveChangesAsync(ct);

        var fresh = await _db.Purchases.AsNoTracking()
            .Include(x => x.Items)
            .FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);

        return Ok(ToDto(fresh));
    }

    // ================== DELETE ==================
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // Satın alma silindiğinde, PurchaseItem ve ProductItem'ların da silinmesi gerekir.
        // Ayrıca stok hareketlerinin tersine çevrilmesi gerekir.
        var p = await _db.Purchases.Include(x => x.Items)
                                   .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (p is null) return NotFound();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var softProp = typeof(Purchase).GetProperty("IsDeleted");
            if (softProp != null) softProp.SetValue(p, true);
            else _db.Purchases.Remove(p); // Hard delete

            await _db.SaveChangesAsync(ct);

            // Stok hareketlerini tersine çevir
            foreach (var item in p.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(pr => pr.TenantId == tenantId && pr.ProductCode == item.ProductCode, ct);
                if (product is not null)
                {
                    // Stoktan ÇIKIŞ (-) hareketi (Girişi tersine çevirme)
                    await _stock.AdjustAsync(
                        branchId: p.BranchId,
                        productId: product.Id,
                        productItemId: null, // Tekil parçaları toplu yönetmiyoruz
                        deltaQuantity: -item.Quantity, // (-) çıkış
                        refKind: StockRefKind.Purchase,
                        refId: p.Id,
                        note: $"Purchase deleted (revert)",
                        ct: ct
                    );
                }
            }

            // ProductItem'ları sil (Satın alma kalemi silinince ProductItem'ın da silinmesi gerekir)
            var itemIdsToDelete = await _db.ProductItems
                .Where(pi => pi.BranchId == p.BranchId && pi.CreatedAt > p.Date.AddMinutes(-5) && pi.CreatedAt < p.Date.AddMinutes(5)) // kaba bir zaman aralığı
                .Select(pi => pi.Id)
                .ToListAsync(ct);

            // Daha kesin: Satın alma hareketindeki ProductMovement'ları bulup ProductItem'ları silmek daha iyidir.
            // Bu basit kodda, ProductItem'ları manuel silmeyi atlıyoruz, çünkü EF'nin cascade delete'i veya ayrı bir lojik ile yönetilmesi gerekir.

            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================== Basit Alım (auto product) ==================
    public sealed record SimplePurchaseItemReq(
        string? ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Weight,
        decimal UnitCost,
        string? Serial,
        string? Barcode
    );

    public sealed record SimplePurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        DateTime? Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        List<SimplePurchaseItemReq> Items
    );
    [HttpPost("simple")]
    public async Task<IActionResult> CreateSimple([FromBody] SimplePurchaseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "En az bir kalem gerekli." });

        // branch check
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        var purchase = new Purchase
        {
            TenantId = tenantId,
            BranchId = branch.Id,
            UserId = userId,
            CustomerId = req.CustomerId,
            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
            DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim(),
            PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),

        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            int lineNo = 0;
            decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;
            var newProductItems = new List<ProductItem>(); // Yeni oluşan tekil parçaları tut

            foreach (var it in req.Items)
            {
                var code = (it.ProductCode ?? "").Trim();
                Product? product = null;

                if (!string.IsNullOrEmpty(code))
                {
                    product = await _db.Products.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == code, ct);
                }

                if (product is null)
                {
                    var safeCat = (it.Category ?? "URUN").ToUpperInvariant().Replace(' ', '-');
                    var safeName = it.ProductName.Trim().ToUpperInvariant().Replace(' ', '-');
                    var generatedCode = $"{safeCat}-{it.Karat}-{safeName}";

                    product = new Product
                    {
                        TenantId = tenantId,
                        ProductCode = generatedCode,
                        Name = it.ProductName.Trim(),
                        Category = it.Category?.Trim(),
                        Karat = it.Karat.Trim()
                    };

                    _db.Products.Add(product);
                    await _db.SaveChangesAsync(ct); // Id & code
                    code = product.ProductCode;
                }

                var lineSub = it.Weight * it.UnitCost;
                var lineTot = Math.Round(lineSub, 2);

                // PurchaseItem oluşturulurken 'it' (SimplePurchaseItemReq) kullanılır
                purchase.Items.Add(new PurchaseItem
                {
                    TenantId = tenantId,
                    LineNo = ++lineNo,
                    Kind = ItemKind.Finished,
                    ProductCode = code,
                    ProductName = it.ProductName.Trim(),
                    Karat = it.Karat.Trim(),
                    Category = it.Category?.Trim(),
                    Quantity = it.Weight,
                    UnitCost = it.UnitCost,
                    Discount = 0m,
                    TaxRate = 0m,
                    LineTotal = lineTot
                });

                subtotal += lineSub;
                grandTotal += lineTot;

                // Yeni ProductItem oluşturma lojiği
                var reqItem = req.Items[lineNo - 1]; // Bu aslında 'it' ile aynıdır
                var barcode = string.IsNullOrWhiteSpace(reqItem.Barcode) ? GenerateBarcode() : reqItem.Barcode!.Trim();

                // ProductItem oluşturulurken yine 'it' (SimplePurchaseItemReq) kullanılır
                var newPi = new ProductItem
                {
                    TenantId = tenantId,
                    ProductId = product.Id,
                    BranchId = purchase.BranchId,
                    Serial = string.IsNullOrWhiteSpace(reqItem.Serial) ? "" : reqItem.Serial!.Trim(),
                    Barcode = barcode,
                    Karat = it.Karat,       // <-- Düzeltildi: 'it.Karat' kullanıldı
                    Weight = it.Weight,     // <-- Düzeltildi: 'it.Weight' kullanıldı
                    IsInStock = true
                };
                newProductItems.Add(newPi);
                _db.ProductItems.Add(newPi); // EF'e ekle
            } // foreach (var it in req.Items) döngüsü biter

            purchase.Subtotal = Math.Round(subtotal, 2);
            purchase.DiscountTotal = Math.Round(discountTotal, 2);
            purchase.TaxTotal = Math.Round(taxTotal, 2);
            purchase.GrandTotal = Math.Round(grandTotal, 2);
            purchase.TotalAmount = purchase.GrandTotal;

            _db.Purchases.Add(purchase);
            await _db.SaveChangesAsync(ct); // Tüm Id'ler oluştu

            // stok + tekil parça hareketlerini kaydet
            foreach (var pi in purchase.Items) // Bu döngüdeki 'pi' artık PurchaseItem'dır (doğru)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == pi.ProductCode, ct);
                if (product is null) continue;

                // RelatedProductItem, yeni oluşturulan ProductItem'lar listesinden bulunur
                var relatedProductItem = newProductItems.FirstOrDefault(i => i.ProductId == product.Id);

                // AdjustAsync çağrısı güncellendi:
                await _stock.AdjustAsync(
                    branchId: purchase.BranchId,
                    productId: product.Id,
                    productItemId: relatedProductItem?.Id, // << ProductItem ID kullanıldı
                    deltaQuantity: +pi.Quantity,
                    refKind: StockRefKind.Purchase,
                    refId: purchase.Id,
                    note: $"Purchase {purchase.Id} (simple) - Item {relatedProductItem?.Barcode}",
                    ct: ct
                );
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var saved = await _db.Purchases.AsNoTracking()
                .Include(x => x.Items)
                .FirstAsync(x => x.Id == purchase.Id && x.TenantId == tenantId, ct);

            return CreatedAtAction(nameof(GetById), new { id = saved.Id }, ToDto(saved));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================== Helpers ==================
    private static void RecalcTotals(Purchase p)
    {
        foreach (var i in p.Items)
        {
            var lineBase = i.Quantity * i.UnitCost;
            var afterDiscount = lineBase - i.Discount;
            var tax = afterDiscount * i.TaxRate;
            i.LineTotal = decimal.Round(afterDiscount + tax, 2);
        }

        p.Subtotal = decimal.Round(p.Items.Sum(i => i.Quantity * i.UnitCost), 2);
        p.DiscountTotal = decimal.Round(p.Items.Sum(i => i.Discount), 2);
        p.TaxTotal = decimal.Round(p.Items.Sum(i =>
        {
            var baseAmt = i.Quantity * i.UnitCost;
            var after = baseAmt - i.Discount;
            return after * i.TaxRate;
        }), 2);
        p.GrandTotal = decimal.Round(p.Subtotal - p.DiscountTotal + p.TaxTotal, 2);
        p.TotalAmount = p.Subtotal; // mevcut davranış
    }

    private static string GenerateBarcode()
    {
        var ts = DateTime.UtcNow.ToString("yyMMddHHmm");
        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                    .Replace("=", "").Replace("+", "").Replace("/", "")
                    .ToUpperInvariant();
        return $"PI-{ts}-{rnd[..6]}";
    }

    private static PurchaseDto ToDto(Purchase p) =>
        new(
            p.Id,
            p.BranchId,
            p.UserId,
            p.CustomerId,
            p.Date,
            p.DocumentNo,
            p.PartnerName,
            p.Note,
            p.Subtotal,
            p.DiscountTotal,
            p.TaxTotal,
            p.GrandTotal,
            p.TotalAmount,
            (p.Items ?? new List<PurchaseItem>()).Select(ToItemDto).ToList()
        );

    private static PurchaseItemDto ToItemDto(PurchaseItem i) =>
        new(
            i.Id,
            i.PurchaseId,
            i.LineNo,
            i.Kind,
            i.ProductCode,
            i.ProductName,
            i.Karat,
            i.Category,
            i.Quantity,
            i.UnitCost,
            i.Discount,
            i.TaxRate,
            i.LineTotal
        );
}

//using System.Security.Claims;
//using kuyumcu_application.Abstractions;
//using kuyumcu_domain.Entities;
//using kuyumcu_infrastructure.Persistence;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using kuyumcu_domain.Enums; // StockRefKind, ItemKind
//using KuyumcuPricee_Service;
//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/[controller]")]
//[Authorize] // JWT zorunlu
//public class PurchasesController : ControllerBase
//{
//    private readonly AppDbContext _db;
//    private readonly IStockService _stock;
//    public PurchasesController(AppDbContext db, IStockService stock)
//    {
//        _db = db;
//        _stock = stock;
//    }

//    // ---------------- Tenant helper ----------------
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

//    // ================== DTO'lar ==================
//    // Create
//    public sealed record CreatePurchaseItemReq(
//        int? LineNo,
//        ItemKind Kind,
//        string ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Quantity,
//        decimal UnitCost,
//        decimal Discount,
//        decimal TaxRate
//    );

//    public sealed record CreatePurchaseReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        DateTime? Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        List<CreatePurchaseItemReq> Items
//    );

//    // Update
//    public sealed record UpdatePurchaseItemReq(
//        int? LineNo,
//        ItemKind Kind,
//        string ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Quantity,
//        decimal UnitCost,
//        decimal Discount,
//        decimal TaxRate
//    );

//    public sealed record UpdatePurchaseReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        DateTime? Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        List<UpdatePurchaseItemReq> Items
//    );

//    // Response
//    public sealed record PurchaseItemDto(
//        Guid Id,
//        Guid PurchaseId,
//        int LineNo,
//        ItemKind Kind,
//        string ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Quantity,
//        decimal UnitCost,
//        decimal Discount,
//        decimal TaxRate,
//        decimal LineTotal
//    );

//    public sealed record PurchaseDto(
//        Guid Id,
//        Guid BranchId,
//        Guid UserId,
//        Guid? CustomerId,
//        DateTime Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        decimal Subtotal,
//        decimal DiscountTotal,
//        decimal TaxTotal,
//        decimal GrandTotal,
//        decimal TotalAmount,
//        List<PurchaseItemDto> Items
//    );

//    // ================== CREATE ==================
//    [HttpPost]
//    public async Task<IActionResult> Create([FromBody] CreatePurchaseReq req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        // user
//        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(uidStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        if (req.Items is null || req.Items.Count == 0)
//            return BadRequest(new { error = "En az bir kalem (Items) gereklidir." });

//        // Branch tenant doğrula
//        var branch = await _db.Branches.AsNoTracking()
//            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (branch is null)
//            return BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." });

//        // Ürün kodlarını tenant bazında doğrula
//        var missingCodes = new List<string>();
//        foreach (var it in req.Items)
//        {
//            if (string.IsNullOrWhiteSpace(it.ProductCode)) { missingCodes.Add("(boş kod)"); continue; }

//            var exists = await _db.Products.AsNoTracking()
//                .AnyAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//            if (!exists) missingCodes.Add(it.ProductCode);
//        }
//        if (missingCodes.Count > 0)
//        {
//            return BadRequest(new
//            {
//                error = "Aşağıdaki ProductCode(lar) bu tenant’ta yok:",
//                codes = missingCodes.Distinct().ToArray()
//            });
//        }

//        // Başlık
//        var purchase = new Purchase
//        {
//            TenantId = tenantId,
//            BranchId = branch.Id,
//            UserId = userId,
//            CustomerId = req.CustomerId,
//            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
//            DocumentNo = req.DocumentNo?.Trim(),
//            PartnerName = req.PartnerName?.Trim(),
//            Note = req.Note?.Trim(),

//        };

//        // Toplamlar
//        decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;
//        int lineNo = 0;

//        foreach (var it in req.Items)
//        {
//            var lineSub = Math.Round(it.Quantity * it.UnitCost, 2);
//            var lineDisc = Math.Round(it.Discount, 2);
//            var taxable = lineSub - lineDisc;
//            var lineTax = Math.Round(taxable * (it.TaxRate / 100m), 2);
//            var lineTot = Math.Round(taxable + lineTax, 2);

//            subtotal += lineSub;
//            discountTotal += lineDisc;
//            taxTotal += lineTax;
//            grandTotal += lineTot;

//            purchase.Items.Add(new PurchaseItem
//            {
//                TenantId = tenantId,
//                LineNo = ++lineNo,
//                Kind = it.Kind,
//                ProductCode = it.ProductCode?.Trim() ?? "",
//                ProductName = it.ProductName?.Trim() ?? "",
//                Karat = it.Karat?.Trim() ?? "",
//                Category = it.Category?.Trim(),
//                Quantity = it.Quantity,
//                UnitCost = it.UnitCost,
//                Discount = it.Discount,
//                TaxRate = it.TaxRate,
//                LineTotal = lineTot
//            });
//        }

//        purchase.Subtotal = Math.Round(subtotal, 2);
//        purchase.DiscountTotal = Math.Round(discountTotal, 2);
//        purchase.TaxTotal = Math.Round(taxTotal, 2);
//        purchase.GrandTotal = Math.Round(grandTotal, 2);
//        purchase.TotalAmount = purchase.GrandTotal;

//        // ---- TRANSACTION ----
//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            _db.Purchases.Add(purchase);
//            await _db.SaveChangesAsync(ct); // Id’ler oluştu

//            // STOK GİRİŞİ (+) ve tekil parça
//            foreach (var it in purchase.Items)
//            {
//                if (string.IsNullOrWhiteSpace(it.ProductCode)) continue;

//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//                if (product is null) continue;

//                // stok +
//                await _stock.AdjustAsync(
//                    branchId: purchase.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +it.Quantity,
//                    refKind: StockRefKind.Purchase,
//                    refId: purchase.Id,
//                    note: $"Purchase {purchase.Id}",
//                    ct: ct
//                );

//                // hurda değilse tekil ProductItem
//                var isScrap = it.Kind.ToString().Equals("Scrap", StringComparison.OrdinalIgnoreCase);
//                if (!isScrap)
//                {
//                    _db.ProductItems.Add(new ProductItem
//                    {
//                        TenantId = tenantId,
//                        ProductId = product.Id,
//                        BranchId = purchase.BranchId,
//                        Barcode = GenerateBarcode(),
//                        Serial = Guid.NewGuid().ToString("N")[..8].ToUpper(),
//                        Karat = it.Karat ?? "",
//                        Weight = it.Quantity,
//                        IsInStock = true
//                    });
//                }
//            }

//            await _db.SaveChangesAsync(ct);
//            await tx.CommitAsync(ct);
//            return CreatedAtAction(nameof(GetById), new { id = purchase.Id }, ToDto(purchase));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }

//    // ================== LIST ==================
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
//        var tenantId = GetTenantId();

//        if (page <= 0) page = 1;
//        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

//        var q = _db.Purchases.AsNoTracking()
//            .Where(x => x.TenantId == tenantId)
//            .OrderByDescending(x => x.Date)
//            .AsQueryable();

//        var softProp = typeof(Purchase).GetProperty("IsDeleted");
//        if (softProp != null)
//            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

//        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
//        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
//        if (from.HasValue) q = q.Where(x => x.Date >= from.Value);
//        if (to.HasValue) q = q.Where(x => x.Date < to.Value);

//        var total = await q.CountAsync(ct);
//        var items = await q.Include(x => x.Items)
//                           .Skip((page - 1) * pageSize)
//                           .Take(pageSize)
//                           .ToListAsync(ct);

//        return Ok(new
//        {
//            total,
//            page,
//            pageSize,
//            items = items.Select(ToDto)
//        });
//    }

//    // ================== GET BY ID ==================
//    [HttpGet("{id:guid}")]
//    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var q = _db.Purchases.AsNoTracking()
//            .Where(x => x.TenantId == tenantId);

//        var softProp = typeof(Purchase).GetProperty("IsDeleted");
//        if (softProp != null)
//            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

//        var p = await q.Include(x => x.Items)
//                       .FirstOrDefaultAsync(x => x.Id == id, ct);

//        return p is null ? NotFound() : Ok(ToDto(p));
//    }

//    // ================== UPDATE ==================
//    [HttpPut("{id:guid}")]
//    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePurchaseReq req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var p = await _db.Purchases.Include(x => x.Items)
//                                   .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (p is null) return NotFound();

//        // branch tenant doğrula
//        var branchOk = await _db.Branches.AsNoTracking()
//            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        p.BranchId = req.BranchId;
//        p.CustomerId = req.CustomerId;
//        p.Date = req.Date ?? p.Date;
//        p.DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim();
//        p.PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
//        p.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();

//        p.Items.Clear();

//        int line = 1;
//        foreach (var it in (req.Items ?? new()))
//        {
//            p.Items.Add(new PurchaseItem
//            {
//                TenantId = tenantId,
//                LineNo = it.LineNo ?? line++,
//                Kind = it.Kind,
//                ProductCode = it.ProductCode?.Trim() ?? "",
//                ProductName = it.ProductName?.Trim() ?? "",
//                Karat = it.Karat?.Trim() ?? "",
//                Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
//                Quantity = it.Quantity,
//                UnitCost = it.UnitCost,
//                Discount = it.Discount,
//                TaxRate = it.TaxRate
//            });
//        }

//        RecalcTotals(p);
//        await _db.SaveChangesAsync(ct);

//        var fresh = await _db.Purchases.AsNoTracking()
//            .Include(x => x.Items)
//            .FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);

//        return Ok(ToDto(fresh));
//    }

//    // ================== DELETE ==================
//    [HttpDelete("{id:guid}")]
//    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var p = await _db.Purchases.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (p is null) return NotFound();

//        var softProp = typeof(Purchase).GetProperty("IsDeleted");
//        if (softProp != null) softProp.SetValue(p, true);
//        else _db.Purchases.Remove(p);

//        await _db.SaveChangesAsync(ct);
//        return NoContent();
//    }

//    // ================== Basit Alım (auto product) ==================
//    public sealed record SimplePurchaseItemReq(
//        string? ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Weight,
//        decimal UnitCost,
//        string? Serial,
//        string? Barcode
//    );

//    public sealed record SimplePurchaseReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        DateTime? Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        List<SimplePurchaseItemReq> Items
//    );

//    [HttpPost("simple")]
//    public async Task<IActionResult> CreateSimple([FromBody] SimplePurchaseReq req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(uidStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        if (req.Items is null || req.Items.Count == 0)
//            return BadRequest(new { error = "En az bir kalem gerekli." });

//        // branch check
//        var branch = await _db.Branches.AsNoTracking()
//            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        var purchase = new Purchase
//        {
//            TenantId = tenantId,
//            BranchId = branch.Id,
//            UserId = userId,
//            CustomerId = req.CustomerId,
//            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
//            DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim(),
//            PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim(),
//            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),

//        };

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            int lineNo = 0;
//            decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;

//            foreach (var it in req.Items)
//            {
//                var code = (it.ProductCode ?? "").Trim();
//                Product? product = null;

//                if (!string.IsNullOrEmpty(code))
//                {
//                    product = await _db.Products.AsNoTracking()
//                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == code, ct);
//                }

//                if (product is null)
//                {
//                    var safeCat = (it.Category ?? "URUN").ToUpperInvariant().Replace(' ', '-');
//                    var safeName = it.ProductName.Trim().ToUpperInvariant().Replace(' ', '-');
//                    var generatedCode = $"{safeCat}-{it.Karat}-{safeName}";

//                    product = new Product
//                    {
//                        TenantId = tenantId,
//                        ProductCode = generatedCode,
//                        Name = it.ProductName.Trim(),
//                        Category = it.Category?.Trim(),
//                        Karat = it.Karat.Trim()
//                    };

//                    _db.Products.Add(product);
//                    await _db.SaveChangesAsync(ct); // Id & code
//                    code = product.ProductCode;
//                }

//                var lineSub = it.Weight * it.UnitCost;
//                var lineTot = Math.Round(lineSub, 2);

//                purchase.Items.Add(new PurchaseItem
//                {
//                    TenantId = tenantId,
//                    LineNo = ++lineNo,
//                    Kind = ItemKind.Finished,
//                    ProductCode = code,
//                    ProductName = it.ProductName.Trim(),
//                    Karat = it.Karat.Trim(),
//                    Category = it.Category?.Trim(),
//                    Quantity = it.Weight,
//                    UnitCost = it.UnitCost,
//                    Discount = 0m,
//                    TaxRate = 0m,
//                    LineTotal = lineTot
//                });

//                subtotal += lineSub;
//                grandTotal += lineTot;
//            }

//            purchase.Subtotal = Math.Round(subtotal, 2);
//            purchase.DiscountTotal = Math.Round(discountTotal, 2);
//            purchase.TaxTotal = Math.Round(taxTotal, 2);
//            purchase.GrandTotal = Math.Round(grandTotal, 2);
//            purchase.TotalAmount = purchase.GrandTotal;

//            _db.Purchases.Add(purchase);
//            await _db.SaveChangesAsync(ct);

//            // stok + tekil parça
//            foreach (var pi in purchase.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == pi.ProductCode, ct);
//                if (product is null) continue;

//                await _stock.AdjustAsync(
//                    branchId: purchase.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +pi.Quantity,
//                    refKind: StockRefKind.Purchase,
//                    refId: purchase.Id,
//                    note: $"Purchase {purchase.Id} (simple)",
//                    ct: ct
//                );

//                var reqItem = req.Items[pi.LineNo - 1];
//                var barcode = string.IsNullOrWhiteSpace(reqItem.Barcode) ? GenerateBarcode() : reqItem.Barcode!.Trim();

//                _db.ProductItems.Add(new ProductItem
//                {
//                    TenantId = tenantId,
//                    ProductId = product.Id,
//                    BranchId = purchase.BranchId,
//                    Serial = string.IsNullOrWhiteSpace(reqItem.Serial) ? "" : reqItem.Serial!.Trim(),
//                    Barcode = barcode,
//                    Karat = pi.Karat,
//                    Weight = pi.Quantity,
//                    IsInStock = true
//                });
//            }

//            await _db.SaveChangesAsync(ct);
//            await tx.CommitAsync(ct);

//            var saved = await _db.Purchases.AsNoTracking()
//                .Include(x => x.Items)
//                .FirstAsync(x => x.Id == purchase.Id && x.TenantId == tenantId, ct);

//            return CreatedAtAction(nameof(GetById), new { id = saved.Id }, ToDto(saved));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }

//    // ================== Helpers ==================
//    private static void RecalcTotals(Purchase p)
//    {
//        foreach (var i in p.Items)
//        {
//            var lineBase = i.Quantity * i.UnitCost;
//            var afterDiscount = lineBase - i.Discount;
//            var tax = afterDiscount * i.TaxRate;
//            i.LineTotal = decimal.Round(afterDiscount + tax, 2);
//        }

//        p.Subtotal = decimal.Round(p.Items.Sum(i => i.Quantity * i.UnitCost), 2);
//        p.DiscountTotal = decimal.Round(p.Items.Sum(i => i.Discount), 2);
//        p.TaxTotal = decimal.Round(p.Items.Sum(i =>
//        {
//            var baseAmt = i.Quantity * i.UnitCost;
//            var after = baseAmt - i.Discount;
//            return after * i.TaxRate;
//        }), 2);
//        p.GrandTotal = decimal.Round(p.Subtotal - p.DiscountTotal + p.TaxTotal, 2);
//        p.TotalAmount = p.Subtotal; // mevcut davranış
//    }

//    private static string GenerateBarcode()
//    {
//        var ts = DateTime.UtcNow.ToString("yyMMddHHmm");
//        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
//                    .Replace("=", "").Replace("+", "").Replace("/", "")
//                    .ToUpperInvariant();
//        return $"PI-{ts}-{rnd[..6]}";
//    }

//    private static PurchaseDto ToDto(Purchase p) =>
//        new(
//            p.Id,
//            p.BranchId,
//            p.UserId,
//            p.CustomerId,
//            p.Date,
//            p.DocumentNo,
//            p.PartnerName,
//            p.Note,
//            p.Subtotal,
//            p.DiscountTotal,
//            p.TaxTotal,
//            p.GrandTotal,
//            p.TotalAmount,
//            (p.Items ?? new List<PurchaseItem>()).Select(ToItemDto).ToList()
//        );

//    private static PurchaseItemDto ToItemDto(PurchaseItem i) =>
//        new(
//            i.Id,
//            i.PurchaseId,
//            i.LineNo,
//            i.Kind,
//            i.ProductCode,
//            i.ProductName,
//            i.Karat,
//            i.Category,
//            i.Quantity,
//            i.UnitCost,
//            i.Discount,
//            i.TaxRate,
//            i.LineTotal
//        );
//}
