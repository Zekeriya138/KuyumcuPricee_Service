using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("api/stocks")]
[Authorize]
public class DepoStokController : ControllerBase
{
    private readonly AppDbContext _db;

    public DepoStokController(AppDbContext db) => _db = db;

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

    public record DepoStokDto(Guid Id, string Ayar, decimal TotalGram, decimal BarcodedGram, decimal UnbarcodedGram, decimal OrtalamaMaliyet);
    public record DepoAddReq(Guid BranchId, string Ayar, decimal Gram, decimal BirimMaliyet);
    public record DepoSubtractReq(Guid BranchId, string Ayar, decimal Gram);
    public record DepoAddHavuzReq(
        Guid BranchId,
        string? TedarikciFirma,
        string MalTanim,
        string Ayar,
        decimal Gram,
        decimal BirimIscilikHas,
        decimal? ToplamIscilikHas);

    public record DepoStokHavuzDto(
        Guid Id,
        string Ayar,
        string MalTanimNorm,
        string TedarikciFirmaNorm,
        decimal BirimMaliyet,
        decimal TotalGram,
        decimal BarcodedGram,
        decimal UnbarcodedGram);

    /// <summary>DepoStokHavuz satırları (branch zorunlu) — Stok/Depo ekranında birebir gösterim için.</summary>
    [HttpGet("havuz")]
    public async Task<IActionResult> ListHavuz([FromQuery] Guid branchId, CancellationToken ct = default)
    {
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "branchId zorunludur." });

        var tenantId = GetTenantId();
        var list = await _db.DepoStokHavuzlar.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted)
            .OrderBy(x => x.TedarikciFirmaNorm)
            .ThenBy(x => x.Ayar)
            .ThenBy(x => x.MalTanimNorm)
            .ThenBy(x => x.BirimMaliyet)
            .Select(x => new DepoStokHavuzDto(
                x.Id,
                x.Ayar,
                x.MalTanimNorm,
                x.TedarikciFirmaNorm,
                x.BirimMaliyet,
                x.TotalGram,
                x.BarcodedGram,
                x.UnbarcodedGram))
            .ToListAsync(ct);
        return Ok(list);
    }

    public record MoveBarcodedTripleReq(
        Guid BranchId,
        string Ayar,
        string MalTanim,
        string TedarikciFirma,
        decimal BirimMaliyet,
        decimal Gram);

    /// <summary>Tüm depo bakiyelerini getirir (branchId opsiyonel).</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var query = _db.DepoStoklar.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            query = query.Where(x => x.BranchId == branchId.Value);

        var list = await query.OrderBy(x => x.Ayar)
            .Select(x => new DepoStokDto(x.Id, x.Ayar, x.TotalGram, x.BarcodedGram, x.UnbarcodedGram, x.OrtalamaMaliyet))
            .ToListAsync(ct);
        return Ok(list);
    }

    /// <summary>Belirli ayar ve şube için depo bakiyesini getirir.</summary>
    [HttpGet("by-ayar")]
    public async Task<IActionResult> GetByAyar([FromQuery] Guid branchId, [FromQuery] string ayar, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var depo = await _db.DepoStoklar.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.Ayar == (ayar ?? "").Trim() && !x.IsDeleted, ct);
        if (depo is null)
            return Ok(new DepoStokDto(Guid.Empty, ayar ?? "", 0, 0, 0, 0));
        return Ok(new DepoStokDto(depo.Id, depo.Ayar, depo.TotalGram, depo.BarcodedGram, depo.UnbarcodedGram, depo.OrtalamaMaliyet));
    }

    /// <summary>Depoya gram ekler (toptancıdan alış). Ortalama maliyet ağırlıklı güncellenir.</summary>
    [HttpPost("add")]
    public async Task<IActionResult> Add([FromBody] DepoAddReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req.Gram <= 0)
            return BadRequest(new { error = "Gram 0'dan büyük olmalı." });

        var depo = await _db.DepoStoklar
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && x.Ayar == (req.Ayar ?? "").Trim() && !x.IsDeleted, ct);

        if (depo is null)
        {
            depo = new DepoStok
            {
                TenantId = tenantId,
                BranchId = req.BranchId,
                Ayar = (req.Ayar ?? "").Trim(),
                TotalGram = 0,
                BarcodedGram = 0,
                UnbarcodedGram = 0,
                OrtalamaMaliyet = req.BirimMaliyet
            };
            _db.DepoStoklar.Add(depo);
        }

        depo.Add(req.Gram, req.BirimMaliyet);
        await _db.SaveChangesAsync(ct);
        return Ok(new DepoStokDto(depo.Id, depo.Ayar, depo.TotalGram, depo.BarcodedGram, depo.UnbarcodedGram, depo.OrtalamaMaliyet));
    }

    /// <summary>Stok/Depo ekranından manuel hammadde girişi (havuz + ayar deposu).</summary>
    [HttpPost("add-havuz")]
    public async Task<IActionResult> AddHavuz([FromBody] DepoAddHavuzReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req.BranchId == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });
        if (req.Gram <= 0)
            return BadRequest(new { error = "Gram 0'dan büyük olmalı." });
        if (string.IsNullOrWhiteSpace(req.MalTanim))
            return BadRequest(new { error = "Mal tanımı zorunludur." });

        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (branch is null)
            return BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." });

        var ayar = DepoStokTripleHelper.NormalizeAyarKarat(req.Ayar);
        if (string.IsNullOrWhiteSpace(ayar))
            return BadRequest(new { error = "Ayar değeri geçersiz." });

        var birim = DepoStokTripleHelper.RoundBirimMaliyet(req.BirimIscilikHas);
        if (birim < 0)
            return BadRequest(new { error = "Birim işçilik negatif olamaz." });

        if (req.ToplamIscilikHas.HasValue && req.ToplamIscilikHas.Value >= 0 && req.Gram > 0)
            birim = DepoStokTripleHelper.RoundBirimMaliyet(req.ToplamIscilikHas.Value / req.Gram);

        var firma = string.IsNullOrWhiteSpace(req.TedarikciFirma)
            ? "Nihai Tedarikçi"
            : req.TedarikciFirma.Trim();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var depo = await _db.DepoStoklar
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && x.Ayar == ayar && !x.IsDeleted, ct);
        if (depo is null)
        {
            depo = new DepoStok
            {
                TenantId = tenantId,
                BranchId = req.BranchId,
                Ayar = ayar,
                TotalGram = 0,
                BarcodedGram = 0,
                UnbarcodedGram = 0,
                OrtalamaMaliyet = birim
            };
            _db.DepoStoklar.Add(depo);
        }
        depo.Add(req.Gram, birim);

        await DepoStokTripleHelper.AddOrIncrementHavuzAsync(
            _db,
            tenantId,
            req.BranchId,
            ayar,
            req.MalTanim,
            firma,
            birim,
            req.Gram,
            ct);

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var malN = DepoStokTripleHelper.NormalizeMal(req.MalTanim);
        var firmaN = DepoStokTripleHelper.NormalizeFirma(firma);
        var row = await _db.DepoStokHavuzlar.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && !x.IsDeleted
                && x.Ayar == ayar && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN && x.BirimMaliyet == birim, ct);
        if (row is null)
            return Ok(new DepoStokHavuzDto(Guid.Empty, ayar, malN, firmaN, birim, 0, 0, 0));

        return Ok(new DepoStokHavuzDto(
            row.Id,
            row.Ayar,
            row.MalTanimNorm,
            row.TedarikciFirmaNorm,
            row.BirimMaliyet,
            row.TotalGram,
            row.BarcodedGram,
            row.UnbarcodedGram));
    }

    /// <summary>Depodan gram düşer (barkodlama). Yeterli bakiye yoksa 400 döner.</summary>
    [HttpPost("subtract")]
    public async Task<IActionResult> Subtract([FromBody] DepoSubtractReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req.Gram <= 0)
            return BadRequest(new { error = "Gram 0'dan büyük olmalı." });

        var depo = await _db.DepoStoklar
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && x.Ayar == (req.Ayar ?? "").Trim() && !x.IsDeleted, ct);

        if (depo is null || depo.UnbarcodedGram < req.Gram)
            return BadRequest(new { error = $"Depoda yeterli barkodsuz gram yok. İstenen: {req.Gram}, Barkodsuz: {(depo?.UnbarcodedGram ?? 0):0.###} gr" });

        if (!depo.MoveToBarcoded(req.Gram))
            return BadRequest(new { error = "Depodan barkodlamaya aktarım yapılamadı." });

        await _db.SaveChangesAsync(ct);
        return Ok(new DepoStokDto(depo.Id, depo.Ayar, depo.TotalGram, depo.BarcodedGram, depo.UnbarcodedGram, depo.OrtalamaMaliyet));
    }

    /// <summary>Mal + Tedarikçi + Birim maliyet + Ayar için havuz satırı (barkodsuz gram doğrulama).</summary>
    [HttpGet("by-triple")]
    public async Task<IActionResult> GetByTriple(
        [FromQuery] Guid branchId,
        [FromQuery] string ayar,
        [FromQuery] string malTanim,
        [FromQuery] string tedarikciFirma,
        [FromQuery] decimal birimMaliyet,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var ay = DepoStokTripleHelper.NormalizeAyarKarat(ayar);
        var malN = DepoStokTripleHelper.NormalizeMal(malTanim);
        var firmaN = DepoStokTripleHelper.NormalizeFirma(tedarikciFirma);
        var birim = DepoStokTripleHelper.RoundBirimMaliyet(birimMaliyet);
        if (string.IsNullOrEmpty(ay) || string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN))
            return Ok(new DepoStokHavuzDto(Guid.Empty, ay ?? "", malN, firmaN, birim, 0, 0, 0));

        var row = await _db.DepoStokHavuzlar.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted
                && x.Ayar == ay && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN && x.BirimMaliyet == birim, ct);
        if (row is null)
        {
            await DepoStokTripleHelper.TryEnsureHavuzRowFromPurchasesAsync(_db, tenantId, branchId, ay, malN, firmaN, birim, ct);
            row = await _db.DepoStokHavuzlar.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted
                    && x.Ayar == ay && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN && x.BirimMaliyet == birim, ct);
        }
        if (row is null)
            return Ok(new DepoStokHavuzDto(Guid.Empty, ay, malN, firmaN, birim, 0, 0, 0));
        return Ok(new DepoStokHavuzDto(row.Id, row.Ayar, row.MalTanimNorm, row.TedarikciFirmaNorm, row.BirimMaliyet,
            row.TotalGram, row.BarcodedGram, row.UnbarcodedGram));
    }

    /// <summary>Hammadde havuzundan barkodlu üretime atomik aktarım: TotalGram sabit; BarcodedGram += gram; UnbarcodedGram = TotalGram − BarcodedGram (DepoStok ayar satırı da).</summary>
    [HttpPost("move-barcoded-triple")]
    public async Task<IActionResult> MoveBarcodedTriple([FromBody] MoveBarcodedTripleReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req.Gram <= 0)
            return BadRequest(new { error = "Gram 0'dan büyük olmalı." });

        var ay = DepoStokTripleHelper.NormalizeAyarKarat(req.Ayar);
        var malN = DepoStokTripleHelper.NormalizeMal(req.MalTanim);
        var firmaN = DepoStokTripleHelper.NormalizeFirma(req.TedarikciFirma);
        var birim = DepoStokTripleHelper.RoundBirimMaliyet(req.BirimMaliyet);
        if (string.IsNullOrEmpty(ay) || string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN))
            return BadRequest(new { error = "Ayar, mal tanımı ve tedarikçi zorunludur." });

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        // Mevcut havuz satırını güncelle; barkodlama ile yeni DepoStokHavuz satırı eklenmez.
        var havuz = await _db.DepoStokHavuzlar
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && !x.IsDeleted
                && x.Ayar == ay && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN && x.BirimMaliyet == birim, ct);

        var depo = await _db.DepoStoklar
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && x.Ayar == ay && !x.IsDeleted, ct);

        if (havuz is null)
        {
            await tx.RollbackAsync(ct);
            return BadRequest(new { error = "Bu mal + tedarikçi + birim maliyet için havuz kaydı yok. Önce toptancı alışı yapın veya yönetici 'rebuild-havuz' senkronunu çalıştırsın." });
        }
        if (depo is null)
        {
            await tx.RollbackAsync(ct);
            return BadRequest(new { error = "Bu ayar için depo (DepoStok) kaydı bulunamadı." });
        }
        if (havuz.UnbarcodedGram < req.Gram - 0.0001m)
        {
            await tx.RollbackAsync(ct);
            return BadRequest(new { error = $"Havuzda yeterli barkodsuz gram yok. İstenen: {req.Gram:0.###}, Barkodsuz: {havuz.UnbarcodedGram:0.###} g" });
        }
        if (depo.UnbarcodedGram < req.Gram - 0.0001m)
        {
            await tx.RollbackAsync(ct);
            return BadRequest(new { error = $"Depoda (ayar) yeterli barkodsuz gram yok. İstenen: {req.Gram:0.###}, Barkodsuz: {depo.UnbarcodedGram:0.###} g" });
        }

        if (!havuz.MoveToBarcoded(req.Gram) || !depo.MoveToBarcoded(req.Gram))
        {
            await tx.RollbackAsync(ct);
            return BadRequest(new { error = "Aktarım işlemi tamamlanamadı." });
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Ok(new
        {
            depo = new DepoStokDto(depo.Id, depo.Ayar, depo.TotalGram, depo.BarcodedGram, depo.UnbarcodedGram, depo.OrtalamaMaliyet),
            havuz = new DepoStokHavuzDto(havuz.Id, havuz.Ayar, havuz.MalTanimNorm, havuz.TedarikciFirmaNorm, havuz.BirimMaliyet,
                havuz.TotalGram, havuz.BarcodedGram, havuz.UnbarcodedGram)
        });
    }

    /// <summary>Eski veriler için: şube bazında toptancı alışlarından havuz satırlarını yeniden kurar; ardından ürün kartlarından barkodlu aktarımı tekrar oynatır.</summary>
    [HttpPost("rebuild-havuz")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> RebuildHavuz([FromQuery] Guid branchId, CancellationToken ct = default)
    {
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "branchId zorunludur." });

        var tenantId = GetTenantId();
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _db.DepoStokHavuzlar
                .Where(x => x.TenantId == tenantId && x.BranchId == branchId)
                .ExecuteDeleteAsync(ct);

            var purchases = await _db.Purchases
                .Include(p => p.Items)
                .Where(p => p.TenantId == tenantId && !p.IsDeleted && p.PurchaseType == PurchaseType.Toptanci && p.BranchId == branchId)
                .ToListAsync(ct);

            foreach (var purchase in purchases)
            {
                foreach (var it in purchase.Items)
                {
                    var ayar = DepoStokTripleHelper.NormalizeAyarKarat(it.Karat);
                    if (string.IsNullOrEmpty(ayar) || it.Quantity <= 0) continue;
                    var mal = DepoStokTripleHelper.MalTanimFromPurchaseItem(it, it.Karat ?? "");
                    var birimHavuz = it.BirimIscilikHas ?? 0m;
                    await DepoStokTripleHelper.AddOrIncrementHavuzAsync(_db, tenantId, purchase.BranchId, ayar, mal, purchase.PartnerName ?? "", birimHavuz, it.Quantity, ct);
                }
            }

            var products = await _db.Products
                .Where(p => p.TenantId == tenantId && p.BranchId == branchId && !p.IsDeleted && !string.IsNullOrWhiteSpace(p.MalTanim))
                .ToListAsync(ct);

            foreach (var p in products)
            {
                var ayar = DepoStokTripleHelper.NormalizeAyarKarat(p.Karat);
                if (string.IsNullOrEmpty(ayar)) continue;
                var malN = DepoStokTripleHelper.NormalizeMal(p.MalTanim);
                var firmaN = DepoStokTripleHelper.NormalizeFirma(p.DepoTedarikciFirma);
                if (string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN)) continue;
                if (!p.DepoBirimMaliyet.HasValue) continue;
                var birim = DepoStokTripleHelper.RoundBirimMaliyet(p.DepoBirimMaliyet.Value);

                decimal gramMove;
                if ((p.InventoryType ?? InventoryType.Tekil) == InventoryType.Ziynet)
                {
                    var w = p.WeightGr ?? 0m;
                    var st = p.StokMiktari ?? 0;
                    gramMove = w * st;
                }
                else
                    gramMove = p.WeightGr ?? 0m;

                if (gramMove <= 0) continue;

                var havuz = await _db.DepoStokHavuzlar
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted
                        && x.Ayar == ayar && x.MalTanimNorm == malN && x.TedarikciFirmaNorm == firmaN && x.BirimMaliyet == birim, ct);
                if (havuz is null || havuz.UnbarcodedGram < gramMove - 0.0001m) continue;
                havuz.MoveToBarcoded(gramMove);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new { ok = true, message = "Havuz bu şube için yeniden kuruldu (alış + ürün kartı tekrarı)." });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public record DepoGumusLotDto(
        Guid Id,
        Guid BranchId,
        Guid? SupplierId,
        string SupplierName,
        string ProductCode,
        string ProductName,
        decimal Gram,
        decimal UnitCostTl,
        string? Note,
        DateTime EntryDate);

    public record DepoAddGumusLotItemReq(string ProductCode, decimal Gram, decimal UnitCostTl);

    public record DepoAddGumusLotsReq(
        Guid BranchId,
        Guid? SupplierId,
        string? SupplierName,
        string? Note,
        List<DepoAddGumusLotItemReq> Items);

    /// <summary>Stok/Depo gümüş sekmesi — manuel stok lotları (alış/kasa hariç).</summary>
    [HttpGet("gumus-lots")]
    public async Task<IActionResult> GetGumusLots([FromQuery] Guid branchId, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "branchId zorunludur." });

        var rows = await _db.DepoGumusLots.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted)
            .OrderByDescending(x => x.EntryDate)
            .Select(x => new DepoGumusLotDto(
                x.Id,
                x.BranchId,
                x.SupplierId,
                x.SupplierName,
                x.ProductCode,
                x.ProductName,
                x.Gram,
                x.UnitCostTl,
                x.Note,
                x.EntryDate))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Stok/Depo gümüş sekmesinden manuel stok girişi — Purchase veya kasa hareketi oluşturmaz.</summary>
    [HttpPost("add-gumus-lots")]
    public async Task<IActionResult> AddGumusLots([FromBody] DepoAddGumusLotsReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req.BranchId == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "En az bir lot girilmelidir." });

        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (branch is null)
            return BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." });

        var supplierName = string.IsNullOrWhiteSpace(req.SupplierName)
            ? "Nihai Tedarikçi"
            : req.SupplierName.Trim();

        var created = new List<DepoGumusLotDto>();
        var now = DateTime.UtcNow;

        foreach (var item in req.Items)
        {
            if (item.Gram <= 0)
                return BadRequest(new { error = "Gram 0'dan büyük olmalı." });
            if (item.UnitCostTl <= 0)
                return BadRequest(new { error = "Birim fiyat 0'dan büyük olmalı." });
            var code = (item.ProductCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code))
                return BadRequest(new { error = "Ürün kodu zorunludur." });

            var lot = new DepoGumusLot
            {
                TenantId = tenantId,
                BranchId = req.BranchId,
                SupplierId = req.SupplierId,
                SupplierName = supplierName,
                ProductCode = code.ToUpperInvariant(),
                ProductName = "Gümüş Külçe",
                Gram = Math.Round(item.Gram, 4, MidpointRounding.AwayFromZero),
                UnitCostTl = Math.Round(item.UnitCostTl, 2, MidpointRounding.AwayFromZero),
                Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
                EntryDate = now
            };
            _db.DepoGumusLots.Add(lot);
            created.Add(new DepoGumusLotDto(
                lot.Id,
                lot.BranchId,
                lot.SupplierId,
                lot.SupplierName,
                lot.ProductCode,
                lot.ProductName,
                lot.Gram,
                lot.UnitCostTl,
                lot.Note,
                lot.EntryDate));
        }

        await _db.SaveChangesAsync(ct);
        return Ok(created);
    }
}
