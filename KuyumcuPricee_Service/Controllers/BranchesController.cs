using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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

    private Guid? GetClaimBranchId()
    {
        var claimVal =
            User.FindFirst("branch_id")?.Value ??
            User.FindFirst("branchId")?.Value ??
            User.FindFirst("bid")?.Value;
        if (!string.IsNullOrWhiteSpace(claimVal) && Guid.TryParse(claimVal, out var branchId))
            return branchId;
        return null;
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
    public async Task<IActionResult> List([FromQuery] bool? onlyActive, [FromQuery] bool? forManagement, CancellationToken ct)
    {
        var tid = GetTenantId();
        var q = _db.Branches.AsNoTracking().Where(x => x.TenantId == tid);

        var isOwner = User?.Identity?.IsAuthenticated == true && User.IsInRole("Owner");
        var forMgmt = forManagement == true;
        var canSeeAllBranches = isOwner
            || (forMgmt && (HasPermissionClaim("perm_manage_branches") || HasPermissionClaim("perm_switch_branches")))
            || (!forMgmt && HasPermissionClaim("perm_switch_branches"));
        if (User?.Identity?.IsAuthenticated == true && !canSeeAllBranches)
        {
            var branchId = GetClaimBranchId();
            if (!branchId.HasValue || branchId.Value == Guid.Empty)
                return Ok(Array.Empty<BranchDto>());
            q = q.Where(x => x.Id == branchId.Value);
        }

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
        if (!CanManageBranchesPerm()) return Forbid();
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
        if (!CanManageBranchesPerm()) return Forbid();
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
        if (!CanManageBranchesPerm()) return Forbid();
        var tid = GetTenantId();

        var b = await _db.Branches.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (b is null) return NotFound();

        try
        {
            await DeleteBranchAllDataAsync(tid, id, ct);
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = "Şube silinirken beklenmeyen bir hata oluştu.",
                detail = ex.Message
            });
        }
    }

    private async Task DeleteBranchAllDataAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        var targets = _db.Model.GetEntityTypes()
            .Where(et =>
                et.ClrType != typeof(Branch) &&
                et.FindPrimaryKey() is not null &&
                et.FindProperty("BranchId") is not null &&
                !string.IsNullOrWhiteSpace(et.GetTableName()))
            .ToList();

        var parentToChildren = new Dictionary<IEntityType, HashSet<IEntityType>>();
        var indegree = targets.ToDictionary(t => t, _ => 0);

        foreach (var child in targets)
        {
            foreach (var fk in child.GetForeignKeys())
            {
                var parent = fk.PrincipalEntityType;
                if (!indegree.ContainsKey(parent)) continue;
                if (!parentToChildren.TryGetValue(parent, out var children))
                {
                    children = new HashSet<IEntityType>();
                    parentToChildren[parent] = children;
                }
                if (children.Add(child))
                    indegree[child]++;
            }
        }

        var queue = new Queue<IEntityType>(indegree.Where(x => x.Value == 0).Select(x => x.Key));
        var topo = new List<IEntityType>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            topo.Add(current);
            if (!parentToChildren.TryGetValue(current, out var childs)) continue;
            foreach (var child in childs)
            {
                indegree[child]--;
                if (indegree[child] == 0)
                    queue.Enqueue(child);
            }
        }

        if (topo.Count != targets.Count)
            topo = targets;

        var deleteOrder = topo.AsEnumerable().Reverse().ToList(); // önce child tablolar

        static string Esc(string v) => v.Replace("]", "]]");
        string FullTableName(IEntityType et)
        {
            var schema = et.GetSchema();
            var table = et.GetTableName()!;
            return string.IsNullOrWhiteSpace(schema)
                ? $"[{Esc(table)}]"
                : $"[{Esc(schema)}].[{Esc(table)}]";
        }

        using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Bazı kayıtlarda aktif şube dışında olsalar da varsayılan şube referansı tutulabilir.
            // (örn. Users.DefaultBranchId). Önce bu referansları boşalt.
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE [Users] SET [DefaultBranchId] = NULL WHERE [TenantId] = @p0 AND [DefaultBranchId] = @p1",
                new object[] { tenantId, branchId }, ct);

            // BranchId taşımayan ama satış/alışa FK ile bağlı child tabloları önce temizle.
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE si FROM [SaleItems] si INNER JOIN [Sales] s ON s.[Id] = si.[SaleId] WHERE s.[TenantId] = @p0 AND s.[BranchId] = @p1",
                new object[] { tenantId, branchId }, ct);
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE sp FROM [SalePayments] sp INNER JOIN [Sales] s ON s.[Id] = sp.[SaleId] WHERE s.[TenantId] = @p0 AND s.[BranchId] = @p1",
                new object[] { tenantId, branchId }, ct);
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE pi FROM [PurchaseItems] pi INNER JOIN [Purchases] p ON p.[Id] = pi.[PurchaseId] WHERE p.[TenantId] = @p0 AND p.[BranchId] = @p1",
                new object[] { tenantId, branchId }, ct);
            await _db.Database.ExecuteSqlRawAsync(
                "DELETE pp FROM [PurchasePayments] pp INNER JOIN [Purchases] p ON p.[Id] = pp.[PurchaseId] WHERE p.[TenantId] = @p0 AND p.[BranchId] = @p1",
                new object[] { tenantId, branchId }, ct);

            var handledTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var et in deleteOrder)
            {
                var fullTable = FullTableName(et);
                if (!handledTables.Add(fullTable)) continue;

                var hasTenantId = et.FindProperty("TenantId") is not null;
                if (hasTenantId)
                {
                    var sql = $"DELETE FROM {fullTable} WHERE [TenantId] = @p0 AND [BranchId] = @p1";
                    await _db.Database.ExecuteSqlRawAsync(sql, new object[] { tenantId, branchId }, ct);
                }
                else
                {
                    var sql = $"DELETE FROM {fullTable} WHERE [BranchId] = @p0";
                    await _db.Database.ExecuteSqlRawAsync(sql, new object[] { branchId }, ct);
                }
            }

            var branchSql = $"DELETE FROM {FullTableName(_db.Model.FindEntityType(typeof(Branch))!)} WHERE [TenantId] = @p0 AND [Id] = @p1";
            await _db.Database.ExecuteSqlRawAsync(branchSql, new object[] { tenantId, branchId }, ct);

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User?.FindFirstValue(claimType);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanManageBranchesPerm()
    {
        return User?.Identity?.IsAuthenticated == true
               && (User.IsInRole("Owner") || HasPermissionClaim("perm_manage_branches"));
    }
}
