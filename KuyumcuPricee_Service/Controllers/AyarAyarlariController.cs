using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AyarAyarlariController : ControllerBase
{
    private readonly AppDbContext _db;

    public AyarAyarlariController(AppDbContext db) => _db = db;

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

    public record AyarAyarDto(Guid Id, string Ayar, decimal Milyem, decimal Iscilik, decimal VarsayilanMaliyet);
    public record UpdateAyarAyarReq(decimal Milyem, decimal Iscilik, decimal VarsayilanMaliyet);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var list = await _db.AyarAyarlari.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .OrderBy(x => x.Ayar)
            .Select(x => new AyarAyarDto(x.Id, x.Ayar, x.Milyem, x.Iscilik, x.VarsayilanMaliyet))
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{ayar}")]
    public async Task<IActionResult> GetByAyar(string ayar, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var a = await _db.AyarAyarlari.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Ayar == (ayar ?? "").Trim() && !x.IsDeleted, ct);
        if (a is null) return NotFound();
        return Ok(new AyarAyarDto(a.Id, a.Ayar, a.Milyem, a.Iscilik, a.VarsayilanMaliyet));
    }

    [HttpPut("{ayar}")]
    public async Task<IActionResult> Update(string ayar, [FromBody] UpdateAyarAyarReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var a = await _db.AyarAyarlari
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Ayar == (ayar ?? "").Trim() && !x.IsDeleted, ct);
        if (a is null)
        {
            a = new AyarAyar
            {
                TenantId = tenantId,
                Ayar = (ayar ?? "").Trim(),
                Milyem = req.Milyem,
                Iscilik = req.Iscilik,
                VarsayilanMaliyet = req.VarsayilanMaliyet
            };
            _db.AyarAyarlari.Add(a);
        }
        else
        {
            a.Milyem = req.Milyem;
            a.Iscilik = req.Iscilik;
            a.VarsayilanMaliyet = req.VarsayilanMaliyet;
        }
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new AyarAyarDto(a.Id, a.Ayar, a.Milyem, a.Iscilik, a.VarsayilanMaliyet));
    }
}
