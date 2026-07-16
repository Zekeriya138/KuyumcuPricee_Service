using System.Security.Claims;
using System.Linq;
using KUYUMCU.Price_Service.Models;
using KUYUMCU.Price_Service.Services;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kuyumcu.PriceService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public ProductsController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    /// <summary>JWT / TenantContext üzerinden geçerli şube (switch-branch sonrası).</summary>
    private bool TryGetRequiredBranchId(out Guid branchId, out IActionResult? error)
    {
        branchId = _tenant.BranchId ?? Guid.Empty;
        if (branchId == Guid.Empty)
        {
            var claim = User?.Claims?.FirstOrDefault(c =>
                c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claim, out var c) && c != Guid.Empty) branchId = c;
        }
        if (branchId == Guid.Empty)
        {
            error = BadRequest(new { error = "Şube bilgisi eksik. Girişte şube seçin (JWT branch_id)." });
            return false;
        }
        error = null;
        return true;
    }

    // LIST + filtre + sayfalama. productType: 0 = Tekil (barkodlu), 1 = Adetli (Ziynet). Verilmezse tümü.
    [HttpGet]
    public async Task<IActionResult> List(
      [FromQuery] string? q,
      [FromQuery] string? category,
      [FromQuery] string? karat,
      [FromQuery] int? productType,
      [FromQuery] bool? isSpecial,
      [FromQuery] int page = 1,
      [FromQuery] int pageSize = 20,
      CancellationToken ct = default)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

        try
        {
            // Tenant: null-safe; yoksa veya Empty ise tek kiracı varsa onu kullan (WinForms uyumu)
            var tenantId = _tenant?.TenantId ?? Guid.Empty;
            if (tenantId == Guid.Empty)
            {
                var tenantCount = await _db.Tenants.AsNoTracking().CountAsync(ct);
                if (tenantCount == 1)
                    tenantId = await _db.Tenants.AsNoTracking().Select(t => t.Id).FirstAsync(ct);
                else if (tenantCount == 0)
                    return Ok(new { total = 0, page, pageSize, items = Array.Empty<ProductDto>() });
                else
                    return BadRequest(new { error = "TenantId eksik. İstek başlığında X-Tenant-Id gönderin veya giriş yapın." });
            }

            // Özel tekil stok 0 → 1 (satış doğrulaması); satış kalemi yoksa, kiracı bazlı (her liste isteğinde hafif onarım).
            if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;

            if (isSpecial == true)
            {
                try
                {
                    await _db.Database.ExecuteSqlRawAsync(@"
UPDATE p SET p.StokMiktari = 1
FROM Products p
WHERE p.TenantId = {0}
  AND p.BranchId = {1}
  AND p.IsDeleted = 0
  AND p.IsSpecialProduct = 1
  AND (p.InventoryType IS NULL OR p.InventoryType = 0)
  AND (p.StokMiktari IS NULL OR p.StokMiktari = 0)
  AND p.Barcode IS NOT NULL AND LEN(LTRIM(RTRIM(p.Barcode))) >= 10
  AND NOT EXISTS (
    SELECT 1 FROM SaleItems si
    INNER JOIN Sales s ON s.Id = si.SaleId
    WHERE si.TenantId = p.TenantId AND si.ProductCode = p.ProductCode AND s.BranchId = p.BranchId
  )", tenantId, branchId);
                }
                catch
                {
                    /* liste yine dönsün */
                }
            }

            // productType: 0 = Tekil, 1 = Adetli (Ziynet). InventoryType ile eşleşir.
            var inventoryFilter = (kuyumcu_domain.Enums.InventoryType?)productType;

            var query = _db.Products
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted);

            if (inventoryFilter.HasValue)
                query = query.Where(x => (x.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == inventoryFilter.Value);

            if (isSpecial == true)
                query = query.Where(x => x.IsSpecialProduct);
            else if (isSpecial == false)
                query = query.Where(x => !x.IsSpecialProduct);

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
                .Select(x => new ProductDto(
                    x.Id, x.ProductCode, x.Name, x.Category, x.Karat, x.WeightGr, x.Cost ?? 0m, x.Barcode, x.Olcu, x.CreatedAt,
                    (int)(x.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil), x.StokMiktari ?? 0, x.ZiynetTipi, x.IsSpecialProduct,
                    x.MalTanim, x.DepoTedarikciFirma, x.BelirlenenSatisFiyatiHas, x.BirimSatisIscilikHas, x.DepoBirimMaliyet,
                    null
                ))
                .ToListAsync(ct);

            return Ok(new { total, page, pageSize, items });
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { error = "Ürün listesi alınamadı.", detail = msg });
        }
    }

    /// <summary>Kategori koduna göre sıradaki ürün kodunu döndürür (örn: YZK → YZK-001).
    /// Soft-delete edilmiş ürünler de dikkate alınır; böylece silinen DRK-001 tekrar önerilmez.</summary>
    [HttpGet("next-code")]
    public async Task<IActionResult> GetNextProductCode([FromQuery] string categoryCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryCode))
            return BadRequest(new { error = "categoryCode gerekli." });
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;

        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
        {
            var tenantCount = await _db.Tenants.AsNoTracking().CountAsync(ct);
            if (tenantCount == 1)
                tenantId = await _db.Tenants.AsNoTracking().Select(t => t.Id).FirstAsync(ct);
            else
                return BadRequest(new { error = "TenantId eksik (X-Tenant-Id)." });
        }

        var prefix = categoryCode.Trim().ToUpperInvariant();
        // Soft-delete edilmiş kayıtlar dahil, tenant+branch kapsamında kod üret.
        var productsWithPrefix = await _db.Products.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && x.ProductCode.StartsWith(prefix + "-"))
            .Select(x => x.ProductCode)
            .ToListAsync(ct);
        int maxNum = 0;
        foreach (var code in productsWithPrefix)
        {
            var part = code.Length > prefix.Length + 1 ? code.Substring(prefix.Length + 1) : "";
            if (int.TryParse(part, out int num) && num > maxNum)
                maxNum = num;
        }
        var nextCode = prefix + "-" + (maxNum + 1).ToString("D7");
        return Ok(new { nextCode });
    }

    // GET by id
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;
        var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId && !x.IsDeleted, ct);
        return p is null ? NotFound() : Ok(ToDto(p));
    }

    /// <summary>Barkoda göre ürün getirir. Ziynet ürünler için kullanılır (ProductItems/by-barcode bulamazsa bu denenir). Ziynet ise StokMiktari ve ZiynetTipi dahil tam ProductDto döner.</summary>
    [HttpGet("by-barcode/{barcode}")]
    public async Task<IActionResult> GetByBarcode(string barcode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return BadRequest(new { error = "Barkod boş olamaz." });
        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
        {
            var tenantCount = await _db.Tenants.AsNoTracking().CountAsync(ct);
            if (tenantCount == 1)
                tenantId = await _db.Tenants.AsNoTracking().Select(t => t.Id).FirstAsync(ct);
            else
                return BadRequest(new { error = "TenantId eksik." });
        }
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;
        var p = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.Barcode == barcode.Trim(), ct);
        return p is null ? NotFound() : Ok(ToDto(p));
    }

    // CREATE — Özel ürün (IsSpecialProduct) tüm giriş yapanlar; diğer ürünler Owner/Admin veya alış/satış izni.
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateProductDto dto, CancellationToken ct)
    {
        var privileged = IsPrivilegedProductAdmin();
        if (!privileged && !dto.IsSpecialProduct && !HasProductOperationalPermission())
            return StatusCode(403, new { error = "Ürün kartı oluşturmak için alış veya satış izni gerekir." });

        if (!privileged && dto.IsSpecialProduct)
        {
            if (dto.DepoBranchId.HasValue && dto.DepoBranchId != Guid.Empty)
                return BadRequest(new { error = "Özel ürün kaydında depo atomik alanı kullanılamaz." });
            if (dto.InventoryType != 0)
                return BadRequest(new { error = "Özel ürün yalnızca tekil (inventoryType=0) olabilir." });
            if (dto.WeightGr is > 0)
                return BadRequest(new { error = "Özel ürün için gram alanı kullanılamaz." });
        }

        var tenantId = _tenant.TenantId;
        if (tenantId == Guid.Empty)
        {
            // Tek kiracı varsa otomatik kullan (geliştirme / tek kiracı senaryosu)
            var tenantCount = await _db.Tenants.AsNoTracking().CountAsync(ct);
            if (tenantCount == 1)
                tenantId = await _db.Tenants.AsNoTracking().Select(t => t.Id).FirstAsync(ct);
            else
                return BadRequest(new { error = "TenantId eksik. İstek başlığında X-Tenant-Id gönderin (geçerli bir GUID). Uygulama açılışında TenantId dosyası okunmuş olmalı." });
        }

        if (string.IsNullOrWhiteSpace(dto.ProductCode) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { error = "ProductCode ve Name zorunludur." });

        if (!TryGetRequiredBranchId(out var jwtBranchId, out var branchErr)) return branchErr!;

        var productCode = dto.ProductCode.Trim();
        var existsInBranch = await _db.Products.IgnoreQueryFilters()
            .AnyAsync(x => x.TenantId == tenantId && x.BranchId == jwtBranchId && x.ProductCode == productCode && !x.IsDeleted, ct);
        if (existsInBranch)
            return Conflict(new { error = "Bu şubede aynı ProductCode zaten kayıtlı." });

        // Tenant+Branch izolasyonu için indeks şemasını güvenceye al:
        // - eski global unique ProductCode indeksini kaldır
        // - eski Tenant+ProductCode indeksini kaldır
        // - hedef: TenantId+BranchId+ProductCode unique
        await EnsureProductCodeIndexCompatibilityAsync(ct);

        if (dto.IsSpecialProduct)
        {
            var bar = (dto.Barcode ?? "").Trim();
            if (!IsGoldStyleProductBarcode(bar))
                return BadRequest(new { error = "Özel ürün barkodu, barkodlu tekil parça ile aynı formatta olmalıdır: PI-YYMMDD-XXXXXX (16 karakter)." });
        }

        if (!string.IsNullOrWhiteSpace(dto.Barcode))
        {
            var barTrim = dto.Barcode.Trim();
            var dupBar = await _db.Products.IgnoreQueryFilters()
                .AnyAsync(x => x.TenantId == tenantId && x.BranchId == jwtBranchId && !x.IsDeleted && x.Barcode == barTrim, ct);
            if (dupBar)
                return Conflict(new { error = "Bu barkod bu şubede zaten kayıtlı." });
        }

        var productBranchId = jwtBranchId;
        var invEarly = (InventoryType)Math.Max(0, Math.Min(1, dto.InventoryType));
        var tekilDepoEarly = invEarly == InventoryType.Tekil
            && dto.DepoBranchId.HasValue && dto.DepoBranchId.Value != Guid.Empty
            && dto.WeightGr.HasValue && dto.WeightGr.Value > 0
            && !string.IsNullOrWhiteSpace(dto.MalTanim)
            && !string.IsNullOrWhiteSpace(dto.DepoTedarikciFirma)
            && dto.DepoBirimMaliyet.HasValue
            && !string.IsNullOrWhiteSpace(dto.Karat);
        if (tekilDepoEarly)
            productBranchId = dto.DepoBranchId!.Value;
        if (productBranchId != jwtBranchId)
            return BadRequest(new { error = "Ürün yalnızca seçili şubede oluşturulabilir (depo akışında DepoBranchId, oturum şubesi ile aynı olmalı)." });
        if (productBranchId == Guid.Empty)
            return BadRequest(new { error = "Şube bilgisi geçersiz." });

        var entity = new Product
        {
            TenantId = tenantId,
            BranchId = productBranchId,
            ProductCode = productCode,
            Name = dto.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category!.Trim(),
            Karat = string.IsNullOrWhiteSpace(dto.Karat) ? null : dto.Karat!.Trim(),
            WeightGr = dto.WeightGr,
            Cost = dto.Cost ?? 0m,
            Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? null : dto.Barcode!.Trim(),
            Olcu = string.IsNullOrWhiteSpace(dto.Olcu) ? null : dto.Olcu!.Trim(),
            InventoryType = (InventoryType)Math.Max(0, Math.Min(1, dto.InventoryType)),
            // Özel + tekil: her kart satırı en az 1 adet satılabilir stok (satış v2 bu alanı doğrular).
            StokMiktari = dto.IsSpecialProduct && dto.InventoryType == 0
                ? Math.Max(1, Math.Max(0, dto.StokMiktari))
                : Math.Max(0, dto.StokMiktari),
            ZiynetTipi = string.IsNullOrWhiteSpace(dto.ZiynetTipi) ? null : dto.ZiynetTipi.Trim(),
            IsSpecialProduct = dto.IsSpecialProduct,
            MalTanim = string.IsNullOrWhiteSpace(dto.MalTanim) ? null : dto.MalTanim.Trim(),
            DepoTedarikciFirma = string.IsNullOrWhiteSpace(dto.DepoTedarikciFirma) ? null : dto.DepoTedarikciFirma.Trim(),
            BelirlenenSatisFiyatiHas = dto.BelirlenenSatisFiyatiHas,
            BirimSatisIscilikHas = dto.BirimSatisIscilikHas,
            DepoBirimMaliyet = dto.DepoBirimMaliyet.HasValue
                ? DepoStokTripleHelper.RoundBirimMaliyet(dto.DepoBirimMaliyet.Value)
                : null,
            Image = DecodeImage(dto.ImageBase64),
        };

        var inv = entity.InventoryType ?? InventoryType.Tekil;
        var tekilDepoAtomik = inv == InventoryType.Tekil
            && dto.DepoBranchId.HasValue && dto.DepoBranchId.Value != Guid.Empty
            && dto.WeightGr.HasValue && dto.WeightGr.Value > 0
            && !string.IsNullOrWhiteSpace(dto.MalTanim)
            && !string.IsNullOrWhiteSpace(dto.DepoTedarikciFirma)
            && dto.DepoBirimMaliyet.HasValue
            && !string.IsNullOrWhiteSpace(dto.Karat);

        // Tekil + DepoBranchId: havuz (Mal+Tedarikçi+Birim) + DepoStok (ayar) barkodsuz→barkodlu + Product TEK transaction; biri başarısızsa rollback, ürün kaydı yok.
        if (tekilDepoAtomik)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var ay = DepoStokTripleHelper.NormalizeAyarKarat(dto.Karat);
                var malN = DepoStokTripleHelper.NormalizeMal(dto.MalTanim);
                var firmaN = DepoStokTripleHelper.NormalizeFirma(dto.DepoTedarikciFirma);
                var birim = DepoStokTripleHelper.RoundBirimMaliyet(dto.DepoBirimMaliyet!.Value);
                var gram = dto.WeightGr!.Value;
                var branchId = dto.DepoBranchId!.Value;

                if (string.IsNullOrEmpty(ay) || string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN))
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = "Depo hareketi için ayar, mal tanımı ve tedarikçi geçerli olmalıdır." });
                }

                // Barkodlama: mevcut DepoStokHavuz satırını güncelle (Mal+Tedarikçi+BirimMaliyet + ayar eşleşmesi); yeni havuz satırı oluşturulmaz.
                var havuz = await DepoStokTripleHelper.FindHavuzRowAsync(
                    _db, tenantId, branchId, ay, malN, firmaN, birim, ct, tracked: true);
                if (havuz is null)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = "Bu mal + tedarikçi + birim maliyet için havuz satırı yok. Önce toptancı alışı (hammadde girişi) yapın veya yönetici depo havuz senkronunu çalıştırın." });
                }

                var depo = await _db.DepoStoklar
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.Ayar == ay && !x.IsDeleted, ct);
                if (depo is null)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = "Bu ayar için depo (DepoStok) kaydı bulunamadı." });
                }

                // Barkodlanamayacak gram: Toplamdan fazla barkod (ör. 100g stoktan 101g) veya yetersiz barkodsuz.
                if (havuz.BarcodedGram + gram > havuz.TotalGram + 0.0001m)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = $"Havuz: Barkodlanamıyor. İstenen {gram:0.###} g; toplam {havuz.TotalGram:0.###} g, mevcut barkodlu {havuz.BarcodedGram:0.###} g (UnbarcodedGram = TotalGram − BarcodedGram)." });
                }
                if (depo.BarcodedGram + gram > depo.TotalGram + 0.0001m)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = $"Depo (ayar): Barkodlanamıyor. İstenen {gram:0.###} g; toplam {depo.TotalGram:0.###} g, mevcut barkodlu {depo.BarcodedGram:0.###} g." });
                }

                if (!havuz.MoveToBarcoded(gram) || !depo.MoveToBarcoded(gram))
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = "Depo hareketi (TotalGram sabit, BarcodedGram += gram, UnbarcodedGram = TotalGram − BarcodedGram) tamamlanamadı." });
                }

                // Havuz satırındaki birim işçilik ile birebir aynı değeri kaydet.
                entity.DepoBirimMaliyet = havuz.BirimMaliyet;

                _db.Products.Add(entity);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(ct);
                var msg = ex.InnerException?.Message ?? ex.Message;
                return BadRequest(new { error = "Veritabanı hatası: " + msg });
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            var saved = await _db.Products.AsNoTracking()
                .FirstAsync(x => x.Id == entity.Id, ct);
            return CreatedAtAction(nameof(GetById), new { id = saved.Id }, ToDto(saved));
        }

        try
        {
            _db.Products.Add(entity);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { error = "Veritabanı hatası: " + msg });
        }

        var savedP = await _db.Products.AsNoTracking()
            .FirstAsync(x => x.Id == entity.Id, ct);
        return CreatedAtAction(nameof(GetById), new { id = savedP.Id }, ToDto(savedP));
    }

    // UPDATE — Owner/Admin tam yetki; alış/satış izni tekil/özel tam güncelleme, ziynette sınırlı güncelleme.
    [HttpPut("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductDto dto, CancellationToken ct)
    {
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId && !x.IsDeleted, ct);
        if (p is null) return NotFound();

        var privileged = IsPrivilegedProductAdmin();
        var isZiynet = (p.InventoryType ?? InventoryType.Tekil) == InventoryType.Ziynet;
        if (!privileged)
        {
            if (!HasProductOperationalPermission())
                return StatusCode(403, new { error = "Ürün kartı güncellemek için alış veya satış izni gerekir." });
            if (isZiynet && !IsAllowedZiynetOperationalUpdate(p, dto))
                return StatusCode(403, new { error = "Bu izinle yalnızca ziynet stok ve maliyet alanları güncellenebilir." });
        }

        p.Name = string.IsNullOrWhiteSpace(dto.Name) ? p.Name : dto.Name.Trim();
        p.Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category!.Trim();
        p.Karat = string.IsNullOrWhiteSpace(dto.Karat) ? null : dto.Karat!.Trim();
        p.WeightGr = dto.WeightGr;
        if (dto.Cost.HasValue) p.Cost = dto.Cost.Value;
        p.Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? null : dto.Barcode!.Trim();
        p.Olcu = string.IsNullOrWhiteSpace(dto.Olcu) ? null : dto.Olcu!.Trim();
        if (dto.InventoryType.HasValue) p.InventoryType = (kuyumcu_domain.Enums.InventoryType)Math.Max(0, Math.Min(1, dto.InventoryType.Value));
        if (dto.StokMiktari.HasValue) p.StokMiktari = Math.Max(0, dto.StokMiktari.Value);
        if (dto.ZiynetTipi != null) p.ZiynetTipi = string.IsNullOrWhiteSpace(dto.ZiynetTipi) ? null : dto.ZiynetTipi.Trim();
        if (dto.IsSpecialProduct.HasValue) p.IsSpecialProduct = dto.IsSpecialProduct.Value;
        if (dto.MalTanim != null) p.MalTanim = string.IsNullOrWhiteSpace(dto.MalTanim) ? null : dto.MalTanim.Trim();
        if (dto.DepoTedarikciFirma != null) p.DepoTedarikciFirma = string.IsNullOrWhiteSpace(dto.DepoTedarikciFirma) ? null : dto.DepoTedarikciFirma.Trim();
        p.BelirlenenSatisFiyatiHas = dto.BelirlenenSatisFiyatiHas;
        p.BirimSatisIscilikHas = dto.BirimSatisIscilikHas;
        if (dto.DepoBirimMaliyet.HasValue)
            p.DepoBirimMaliyet = DepoStokTripleHelper.RoundBirimMaliyet(dto.DepoBirimMaliyet.Value);
        // null = görseli koru; "" = sil; dolu = güncelle.
        if (dto.ImageBase64 != null)
            p.Image = dto.ImageBase64.Length == 0 ? null : DecodeImage(dto.ImageBase64);

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(p));
    }

    // DELETE (soft) — Yalnızca Owner/Admin
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;
        var tenantId = _tenant.TenantId;
        var p = await _db.Products.FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId && !x.IsDeleted, ct);
        if (p is null) return NotFound();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var items = await _db.ProductItems
                .Where(x => x.TenantId == tenantId && x.ProductId == p.Id && !x.IsDeleted)
                .ToListAsync(ct);

            // Hurda vitrin barkodu depo hammadde havuzuna girmez; geri alma yapılmaz.
            var isHurdaSourced = items.Any(x => x.SourcePurchaseItemId.HasValue);

            // Tekil hammadde barkodlaması: Barkodlu → Barkodsuz (TotalGram sabit).
            var inv = p.InventoryType ?? InventoryType.Tekil;
            var shouldReverseDepo = !isHurdaSourced
                && inv == InventoryType.Tekil
                && !p.IsSpecialProduct
                && !string.IsNullOrWhiteSpace(p.MalTanim)
                && !string.IsNullOrWhiteSpace(p.DepoTedarikciFirma)
                && p.DepoBirimMaliyet.HasValue
                && !string.IsNullOrWhiteSpace(p.Karat);

            if (shouldReverseDepo)
            {
                // Satılmış (IsInStock=false) parçalar zaten OnBarcodedProductSold ile düşmüş; yalnızca stoktakileri geri al.
                var reverseGram = items.Count > 0
                    ? items.Where(x => x.IsInStock).Sum(x => x.Weight)
                    : (p.WeightGr ?? 0m);

                if (reverseGram > 0.0001m)
                {
                    var ay = DepoStokTripleHelper.NormalizeAyarKarat(p.Karat);
                    var malN = DepoStokTripleHelper.NormalizeMal(p.MalTanim);
                    var firmaN = DepoStokTripleHelper.NormalizeFirma(p.DepoTedarikciFirma);
                    var birim = DepoStokTripleHelper.RoundBirimMaliyet(p.DepoBirimMaliyet!.Value);

                    var havuz = await DepoStokTripleHelper.FindHavuzRowAsync(
                        _db, tenantId, branchId, ay, malN, firmaN, birim, ct, tracked: true);
                    var depo = await _db.DepoStoklar
                        .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.Ayar == ay && !x.IsDeleted, ct);

                    if (havuz != null && depo != null)
                    {
                        reverseGram = Math.Min(reverseGram, Math.Min(havuz.BarcodedGram, depo.BarcodedGram));
                        if (reverseGram > 0.0001m)
                        {
                            if (!havuz.MoveToUnbarcoded(reverseGram) || !depo.MoveToUnbarcoded(reverseGram))
                            {
                                await tx.RollbackAsync(ct);
                                return BadRequest(new { error = "Ürün silinemedi: depo barkodlu→barkodsuz geri alma başarısız." });
                            }
                        }
                    }
                }
            }

            foreach (var item in items)
                item.IsDeleted = true;

            p.IsDeleted = true;
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private bool IsPrivilegedProductAdmin()
        => User.IsInRole("Owner") || User.IsInRole("Admin");

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasProductOperationalPermission()
        => IsPrivilegedProductAdmin()
           || HasPermissionClaim("perm_access_purchase")
           || HasPermissionClaim("perm_access_sales");

    private static bool IsAllowedZiynetOperationalUpdate(Product existing, UpdateProductDto dto)
    {
        if (dto.InventoryType.HasValue && dto.InventoryType.Value != (int)InventoryType.Ziynet)
            return false;
        if (dto.IsSpecialProduct == true && !existing.IsSpecialProduct)
            return false;
        if (!string.IsNullOrWhiteSpace(dto.MalTanim))
            return false;
        if (!string.IsNullOrWhiteSpace(dto.DepoTedarikciFirma))
            return false;
        if (dto.DepoBirimMaliyet.HasValue)
            return false;
        if (dto.ImageBase64 != null)
            return false;
        return true;
    }

    private static ProductDto ToDto(Product p) =>
        new(p.Id, p.ProductCode, p.Name, p.Category, p.Karat, p.WeightGr, p.Cost ?? 0m, p.Barcode, p.Olcu, p.CreatedAt, (int)(p.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil), p.StokMiktari ?? 0, p.ZiynetTipi, p.IsSpecialProduct, p.MalTanim, p.DepoTedarikciFirma, p.BelirlenenSatisFiyatiHas, p.BirimSatisIscilikHas, p.DepoBirimMaliyet, p.Image != null ? Convert.ToBase64String(p.Image) : null);

    /// <summary>Base64 (data URI veya düz) görseli bayt dizisine çevirir; geçersizse null döner.</summary>
    private static byte[]? DecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        var raw = base64.Trim();
        var comma = raw.IndexOf(',');
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            raw = raw[(comma + 1)..];
        try { return Convert.FromBase64String(raw); }
        catch { return null; }
    }

    private async Task EnsureProductCodeIndexCompatibilityAsync(CancellationToken ct)
    {
        const string sql = """
                           IF EXISTS (
                               SELECT 1
                               FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N'[dbo].[Products]')
                                 AND name = N'IX_Products_ProductCode')
                           BEGIN
                               DROP INDEX [IX_Products_ProductCode] ON [dbo].[Products];
                           END

                           IF EXISTS (
                               SELECT 1
                               FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N'[dbo].[Products]')
                                 AND name = N'IX_Products_TenantId_ProductCode')
                           BEGIN
                               DROP INDEX [IX_Products_TenantId_ProductCode] ON [dbo].[Products];
                           END

                           IF NOT EXISTS (
                               SELECT 1
                               FROM sys.indexes
                               WHERE object_id = OBJECT_ID(N'[dbo].[Products]')
                                 AND name = N'IX_Products_TenantId_BranchId_ProductCode')
                           BEGIN
                               CREATE UNIQUE INDEX [IX_Products_TenantId_BranchId_ProductCode]
                                   ON [dbo].[Products]([TenantId], [BranchId], [ProductCode]);
                           END
                           """;
        await _db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    /// <summary>ProductItems.GenerateBarcode ile aynı desen: PI-yyMMdd-XXXXXX (toplam 16 karakter).</summary>
    private static bool IsGoldStyleProductBarcode(string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return false;
        var b = barcode.Trim().ToUpperInvariant();
        if (b.Length != 16 || !b.StartsWith("PI-", StringComparison.Ordinal) || b[9] != '-')
            return false;
        for (int i = 3; i <= 8; i++)
        {
            if (!char.IsDigit(b[i])) return false;
        }
        for (int i = 10; i < 16; i++)
        {
            var c = b[i];
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }
        return true;
    }
}
