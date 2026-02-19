using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_domain.Entities;
using kuyumcu_application.Abstractions;
using kuyumcu_infrastructure.Tenancy; // ITenantContext (sende hangi namespace ise onu kullan)

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BranchesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public BranchesController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    // --- küçük yardımcı ---
    private Guid GetTenantId()
    {
        // 1) Header: X-Tenant-Id
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hv) &&
            Guid.TryParse(hv.ToString(), out var fromHeader))
            return fromHeader;

        // 2) JWT claims (birkaç olası anahtar)
        string? claimVal =
            User.FindFirst("tenant_id")?.Value ??            // ← senin tokenındaki isim
            User.FindFirst("tenantid")?.Value ??
            User.FindFirst("tenantId")?.Value ??
            User.FindFirst("tenant")?.Value ??
            User.FindFirst("tid")?.Value;

        if (!string.IsNullOrWhiteSpace(claimVal) && Guid.TryParse(claimVal, out var fromJwt))
            return fromJwt;

        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
    }

    // ===== DTO'lar (class olarak, DataAnnotations property'lerde) =====
    public sealed class BranchCreateReq
    {
        [Required, MaxLength(128)]
        public string Name { get; set; } = null!;
        [MaxLength(32)] public string? Code { get; set; }
        [MaxLength(64)] public string? City { get; set; }
        [MaxLength(256)] public string? Address { get; set; }
        [MaxLength(32)] public string? Phone { get; set; }
        [EmailAddress, MaxLength(128)]
        public string? Email { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public sealed class BranchUpdateReq
    {
        [Required, MaxLength(128)]
        public string Name { get; set; } = null!;
        [MaxLength(32)] public string? Code { get; set; }
        [MaxLength(64)] public string? City { get; set; }
        [MaxLength(256)] public string? Address { get; set; }
        [MaxLength(32)] public string? Phone { get; set; }
        [EmailAddress, MaxLength(128)]
        public string? Email { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public sealed record BranchDto(
        Guid Id, string Name, string? Code, string? City, string? Address, string? Phone, string? Email, bool IsActive
    );

    private static BranchDto ToDto(Branch b)
        => new(b.Id, b.Name, b.Code, b.City, b.Address, b.Phone, b.Email, b.IsActive);

    // ===== LIST =====
    // ===== LIST =====
    [HttpGet]
    [AllowAnonymous]   // 🔹 BUNU EKLE
    public async Task<IActionResult> List([FromQuery] bool? onlyActive, CancellationToken ct)
    {
        var tid = GetTenantId();
        var q = _db.Branches.AsNoTracking().Where(x => x.TenantId == tid);
        if (onlyActive == true) q = q.Where(x => x.IsActive);
        var items = await q.OrderBy(x => x.Name).ToListAsync(ct);
        return Ok(items.Select(ToDto));
    }

    //[HttpGet]
    //public async Task<IActionResult> List([FromQuery] bool? onlyActive, CancellationToken ct)
    //{
    //    var tid = GetTenantId();
    //    var q = _db.Branches.AsNoTracking().Where(x => x.TenantId == tid);
    //    if (onlyActive == true) q = q.Where(x => x.IsActive);
    //    var items = await q.OrderBy(x => x.Name).ToListAsync(ct);
    //    return Ok(items.Select(ToDto));
    //}

    // ===== GET BY ID =====
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();
        var b = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        return b is null ? NotFound() : Ok(ToDto(b));
    }

    // ===== CREATE =====
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] BranchCreateReq req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var b = new Branch
        {
            TenantId = tid,
            Name = req.Name.Trim(),
            Code = req.Code?.Trim(),
            City = req.City?.Trim(),
            Address = req.Address?.Trim(),
            Phone = req.Phone?.Trim(),
            Email = req.Email?.Trim(),
            IsActive = req.IsActive
        };

        _db.Branches.Add(b);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = b.Id }, ToDto(b));
    }

    // ===== UPDATE =====
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] BranchUpdateReq req, CancellationToken ct)
    {
        var tid = GetTenantId();

        var b = await _db.Branches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (b is null) return NotFound();

        b.Name = req.Name.Trim();
        b.Code = req.Code?.Trim();
        b.City = req.City?.Trim();
        b.Address = req.Address?.Trim();
        b.Phone = req.Phone?.Trim();
        b.Email = req.Email?.Trim();
        b.IsActive = req.IsActive;

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(b));
    }

    // ===== DELETE =====
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tid = GetTenantId();

        var b = await _db.Branches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (b is null) return NotFound();

        _db.Branches.Remove(b);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
