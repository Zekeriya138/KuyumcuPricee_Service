using System.Security.Claims;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

/// <summary>
/// Ürün sayımı (envanter/stok sayım). Barkodlanan ürünlerin fiziksel sayımı için oturum + okutma uçları.
/// Hem masaüstü (kamera/Bluetooth okuyucu) hem geliştirilen mobil uygulama bu uçları kullanır. Şube + kiracı bazlıdır.
/// </summary>
[ApiController]
[Route("api/stockcount")]
[Authorize]
public sealed class StockCountController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public StockCountController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public sealed record CreateSessionReq(string? Name);
    public sealed record ScanReq(string Barcode);

    // ---- Sessions ----

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionReq req, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var tenantId = await ResolveTenantAsync(ct);
        if (tenantId == Guid.Empty) return BadRequest(new { error = "Kiracı bilgisi eksik." });

        var expected = await ExpectedBarcodeCountAsync(branchId, ct);
        var (uid, uname) = CurrentUser();

        var session = new StockCountSession
        {
            TenantId = tenantId,
            BranchId = branchId,
            Name = string.IsNullOrWhiteSpace(req?.Name) ? $"Sayım {DateTime.Now:dd.MM.yyyy HH:mm}" : req!.Name!.Trim(),
            Status = "Open",
            ExpectedCount = expected,
            MissingCount = expected,
            CreatedByUserId = uid,
            CreatedByName = uname
        };
        _db.StockCountSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return Ok(ToSessionDto(session));
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions([FromQuery] string? status, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var q = _db.StockCountSessions.AsNoTracking().Where(x => x.BranchId == branchId);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status);
        var list = await q.OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        return Ok(list.Select(ToSessionDto));
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var s = await _db.StockCountSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId, ct);
        return s is null ? NotFound() : Ok(ToSessionDto(s));
    }

    // ---- Scan ----

    [HttpPost("sessions/{id:guid}/scan")]
    public async Task<IActionResult> Scan(Guid id, [FromBody] ScanReq req, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var barcode = (req?.Barcode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(barcode)) return BadRequest(new { error = "Barkod boş olamaz." });

        var session = await _db.StockCountSessions.FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId, ct);
        if (session is null) return NotFound(new { error = "Sayım oturumu bulunamadı." });
        if (!string.Equals(session.Status, "Open", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Bu sayım oturumu kapalı." });

        // Yalnızca barkodlu tekil/özel ürünler taranır; ziynet (adetli) kamerayla sayılmaz.
        // InventoryType null ise Tekil kabul edilir; doğrudan "!= Ziynet" karşılaştırması NULL satırları düşürür.
        var product = await _db.Products.AsNoTracking()
            .Where(x => x.BranchId == branchId && !x.IsDeleted && x.Barcode == barcode
                        && (x.InventoryType ?? InventoryType.Tekil) != InventoryType.Ziynet)
            .Select(x => new { x.Id, x.ProductCode, x.Name, x.IsSpecialProduct, Stok = x.StokMiktari ?? 0 })
            .FirstOrDefaultAsync(ct);

        if (product == null)
        {
            var tenantId = await ResolveTenantAsync(ct);
            product = await _db.ProductItems.AsNoTracking()
                .Where(pi => pi.TenantId == tenantId && pi.BranchId == branchId && !pi.IsDeleted && pi.IsInStock && pi.Barcode == barcode)
                .Join(
                    _db.Products.AsNoTracking().Where(p => p.BranchId == branchId && !p.IsDeleted
                        && (p.InventoryType ?? InventoryType.Tekil) != InventoryType.Ziynet),
                    pi => pi.ProductId,
                    p => p.Id,
                    (pi, p) => new { p.Id, p.ProductCode, p.Name, p.IsSpecialProduct, Stok = p.StokMiktari ?? 0 })
                .FirstOrDefaultAsync(ct);
        }

        var already = await _db.StockCountScans.AsNoTracking()
            .AnyAsync(s => s.SessionId == session.Id && s.Barcode == barcode, ct);

        // Satılmış özel ürün (adet 0) mağazada fiziksel olarak bulunmaz; sayılan değil "satılmış" olarak bildirilir.
        var satilmisOzel = product != null && product.IsSpecialProduct && product.Stok <= 0;

        var status = already ? "Duplicate"
            : product == null ? "Unknown"
            : satilmisOzel ? "Sold"
            : "Matched";
        var (uid, uname) = CurrentUser();

        _db.StockCountScans.Add(new StockCountScan
        {
            TenantId = session.TenantId,
            SessionId = session.Id,
            Barcode = barcode,
            ProductId = product?.Id,
            ProductCode = product?.ProductCode,
            ProductName = product?.Name,
            MatchStatus = status,
            ScannedAt = DateTime.UtcNow,
            ScannedByUserId = uid,
            ScannedByName = uname
        });
        await _db.SaveChangesAsync(ct);

        session.MatchedCount = await _db.StockCountScans.CountAsync(s => s.SessionId == session.Id && s.MatchStatus == "Matched", ct);
        session.UnknownCount = await _db.StockCountScans.CountAsync(s => s.SessionId == session.Id && s.MatchStatus == "Unknown", ct);
        session.ExpectedCount = await ExpectedBarcodeCountAsync(branchId, ct);
        session.MissingCount = Math.Max(0, session.ExpectedCount - session.MatchedCount);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            status,
            barcode,
            productCode = product?.ProductCode,
            productName = product?.Name,
            isSpecial = product?.IsSpecialProduct ?? false,
            expectedCount = session.ExpectedCount,
            matchedCount = session.MatchedCount,
            unknownCount = session.UnknownCount,
            missingCount = session.MissingCount
        });
    }

    // ---- Report ----

    [HttpGet("sessions/{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var session = await _db.StockCountSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId, ct);
        if (session is null) return NotFound();

        // Beklenen = barkodlu tekil + stokta olan özel ürünler (ziynet hariç). Ziynet ayrıca onay listesi olarak döner.
        var expected = await _db.Products.AsNoTracking()
            .Where(x => x.BranchId == branchId && !x.IsDeleted && x.Barcode != null && x.Barcode != ""
                        && (x.InventoryType ?? InventoryType.Tekil) != InventoryType.Ziynet
                        && (!x.IsSpecialProduct || (x.StokMiktari ?? 0) > 0))
            .Select(x => new { x.ProductCode, Name = x.Name, Barcode = x.Barcode!, x.IsSpecialProduct })
            .ToListAsync(ct);

        var expectedByBarcode = expected
            .GroupBy(x => x.Barcode)
            .ToDictionary(g => g.Key, g => g.First());

        var matchedBarcodes = await _db.StockCountScans.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.MatchStatus == "Matched")
            .Select(s => s.Barcode)
            .ToListAsync(ct);
        var matchedSet = matchedBarcodes.ToHashSet();

        var matched = expectedByBarcode.Values
            .Where(x => matchedSet.Contains(x.Barcode))
            .Select(x => new { productCode = x.ProductCode, productName = x.Name, barcode = x.Barcode, isSpecial = x.IsSpecialProduct })
            .OrderBy(x => x.productCode)
            .ToList();

        var missing = expectedByBarcode.Values
            .Where(x => !matchedSet.Contains(x.Barcode))
            .Select(x => new { productCode = x.ProductCode, productName = x.Name, barcode = x.Barcode, isSpecial = x.IsSpecialProduct })
            .OrderBy(x => x.productCode)
            .ToList();

        var unknown = await _db.StockCountScans.AsNoTracking()
            .Where(s => s.SessionId == session.Id && s.MatchStatus == "Unknown")
            .OrderBy(s => s.ScannedAt)
            .Select(s => new { barcode = s.Barcode, scannedAt = s.ScannedAt, scannedByName = s.ScannedByName })
            .ToListAsync(ct);

        // Ziynet (adetli) — beklenen adetler; kullanıcı fiziksel sayımda onaylar.
        var ziynetRaw = await _db.Products.AsNoTracking()
            .Where(x => x.BranchId == branchId && !x.IsDeleted && x.InventoryType == InventoryType.Ziynet && (x.StokMiktari ?? 0) > 0)
            .Select(x => new { Ad = x.Name, Tip = x.ZiynetTipi, Adet = x.StokMiktari ?? 0 })
            .ToListAsync(ct);
        var ziynet = ziynetRaw
            .GroupBy(x => new { Ad = x.Ad ?? "", Tip = x.Tip ?? "" })
            .Select(g => new { ad = g.Key.Ad, tip = g.Key.Tip, adet = g.Sum(z => z.Adet) })
            .OrderBy(x => x.ad)
            .ToList();

        return Ok(new
        {
            sessionId = session.Id,
            name = session.Name,
            status = session.Status,
            expectedCount = expectedByBarcode.Count,
            matchedCount = matched.Count,
            missingCount = missing.Count,
            unknownCount = unknown.Count,
            matched,
            missing,
            unknown,
            ziynet
        });
    }

    // ---- Complete / Cancel ----

    [HttpPost("sessions/{id:guid}/complete")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var session = await _db.StockCountSessions.FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId, ct);
        if (session is null) return NotFound();

        session.ExpectedCount = await ExpectedBarcodeCountAsync(branchId, ct);
        session.MatchedCount = await _db.StockCountScans.CountAsync(s => s.SessionId == session.Id && s.MatchStatus == "Matched", ct);
        session.UnknownCount = await _db.StockCountScans.CountAsync(s => s.SessionId == session.Id && s.MatchStatus == "Unknown", ct);
        session.MissingCount = Math.Max(0, session.ExpectedCount - session.MatchedCount);
        session.Status = "Completed";
        session.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToSessionDto(session));
    }

    [HttpDelete("sessions/{id:guid}")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!TryGetBranch(out var branchId, out var err)) return err!;
        var session = await _db.StockCountSessions.FirstOrDefaultAsync(x => x.Id == id && x.BranchId == branchId, ct);
        if (session is null) return NotFound();
        session.IsDeleted = true;
        session.Status = "Cancelled";
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Helpers ----

    private async Task<int> ExpectedBarcodeCountAsync(Guid branchId, CancellationToken ct)
    {
        return await _db.Products.AsNoTracking()
            .Where(x => x.BranchId == branchId && !x.IsDeleted && x.Barcode != null && x.Barcode != ""
                        && (x.InventoryType ?? InventoryType.Tekil) != InventoryType.Ziynet
                        && (!x.IsSpecialProduct || (x.StokMiktari ?? 0) > 0))
            .Select(x => x.Barcode)
            .Distinct()
            .CountAsync(ct);
    }

    private static object ToSessionDto(StockCountSession s) => new
    {
        id = s.Id,
        name = s.Name,
        status = s.Status,
        expectedCount = s.ExpectedCount,
        matchedCount = s.MatchedCount,
        unknownCount = s.UnknownCount,
        missingCount = s.MissingCount,
        createdAt = s.CreatedAt,
        completedAt = s.CompletedAt,
        createdByName = s.CreatedByName
    };

    private (Guid? Id, string? Name) CurrentUser()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? id = Guid.TryParse(raw, out var g) ? g : null;
        var name = User.FindFirstValue("full_name");
        if (string.IsNullOrWhiteSpace(name)) name = User.FindFirstValue(ClaimTypes.Name);
        return (id, name);
    }

    private async Task<Guid> ResolveTenantAsync(CancellationToken ct)
    {
        var t = _tenant.TenantId;
        if (t != Guid.Empty) return t;
        var count = await _db.Tenants.AsNoTracking().CountAsync(ct);
        if (count == 1) return await _db.Tenants.AsNoTracking().Select(x => x.Id).FirstAsync(ct);
        return Guid.Empty;
    }

    private bool TryGetBranch(out Guid branchId, out IActionResult? error)
    {
        branchId = _tenant.BranchId ?? Guid.Empty;
        if (branchId == Guid.Empty)
        {
            var claim = User?.Claims?.FirstOrDefault(c => c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claim, out var c) && c != Guid.Empty) branchId = c;
        }
        if (branchId == Guid.Empty)
        {
            error = BadRequest(new { error = "Şube bilgisi eksik. Girişte şube seçin." });
            return false;
        }
        error = null;
        return true;
    }
}
