using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public UsersController(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public sealed record UserListDto(
        Guid Id,
        string Username,
        string Role,
        string? NationalId,
        string? FullName,
        string? Phone,
        string? Email,
        Guid BranchId,
        string BranchName,
        bool IsActive,
        DateTime CreatedAt,
        decimal? CurrentSalary,
        DateTime? CurrentSalaryEffectiveFrom,
        bool CanManageUsers,
        bool CanManageBranches,
        bool CanSwitchBranches,
        bool CanUseEInvoice,
        bool CanUseEArchive
    );

    public sealed record UserUpsertReq(
        Guid BranchId,
        string Username,
        string? Password,
        string? Role,
        string? NationalId,
        string? FullName,
        string? Phone,
        string? Email,
        bool IsActive,
        bool? CanManageUsers,
        bool? CanManageBranches,
        bool? CanSwitchBranches,
        bool? CanUseEInvoice,
        bool? CanUseEArchive
    );

    public sealed record ChangePasswordReq(string NewPassword);
    public sealed record SalaryHistoryDto(Guid Id, decimal Amount, DateTime EffectiveFrom, string? Note, DateTime CreatedAt);
    public sealed record AddSalaryHistoryReq(decimal Amount, DateTime EffectiveFrom, string? Note);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var canManage = CanManageUsers();
        var currentUserId = GetCurrentUserId();

        IQueryable<User> query = _db.Users
            .AsNoTracking()
            .Include(x => x.Branch)
            .Where(x => !x.IsDeleted);

        if (!canManage)
        {
            if (!currentUserId.HasValue) return Unauthorized(new { error = "Geçersiz oturum." });
            query = query.Where(x => x.Id == currentUserId.Value);
        }

        var items = await query
            .OrderBy(x => x.Username)
            .Select(x => new UserListDto(
                x.Id,
                x.Username,
                x.Role,
                x.NationalId,
                x.FullName,
                x.Phone,
                x.Email,
                x.BranchId,
                x.Branch.Name,
                x.IsActive,
                x.CreatedAt,
                _db.UserSalaryHistories
                    .Where(s => !s.IsDeleted && s.UserId == x.Id)
                    .OrderByDescending(s => s.EffectiveFrom)
                    .ThenByDescending(s => s.CreatedAt)
                    .Select(s => (decimal?)s.Amount)
                    .FirstOrDefault(),
                _db.UserSalaryHistories
                    .Where(s => !s.IsDeleted && s.UserId == x.Id)
                    .OrderByDescending(s => s.EffectiveFrom)
                    .ThenByDescending(s => s.CreatedAt)
                    .Select(s => (DateTime?)s.EffectiveFrom)
                    .FirstOrDefault(),
                x.CanManageUsers,
                x.CanManageBranches,
                x.CanSwitchBranches,
                x.CanUseEInvoice,
                x.CanUseEArchive
            ))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!CanAccessUser(id)) return Forbid();

        var item = await _db.Users
            .AsNoTracking()
            .Include(x => x.Branch)
            .Where(x => x.Id == id && !x.IsDeleted)
            .Select(x => new UserListDto(
                x.Id,
                x.Username,
                x.Role,
                x.NationalId,
                x.FullName,
                x.Phone,
                x.Email,
                x.BranchId,
                x.Branch.Name,
                x.IsActive,
                x.CreatedAt,
                _db.UserSalaryHistories
                    .Where(s => !s.IsDeleted && s.UserId == x.Id)
                    .OrderByDescending(s => s.EffectiveFrom)
                    .ThenByDescending(s => s.CreatedAt)
                    .Select(s => (decimal?)s.Amount)
                    .FirstOrDefault(),
                _db.UserSalaryHistories
                    .Where(s => !s.IsDeleted && s.UserId == x.Id)
                    .OrderByDescending(s => s.EffectiveFrom)
                    .ThenByDescending(s => s.CreatedAt)
                    .Select(s => (DateTime?)s.EffectiveFrom)
                    .FirstOrDefault(),
                x.CanManageUsers,
                x.CanManageBranches,
                x.CanSwitchBranches,
                x.CanUseEInvoice,
                x.CanUseEArchive
            ))
            .FirstOrDefaultAsync(ct);

        return item is null ? NotFound() : Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserUpsertReq req, CancellationToken ct)
    {
        if (!CanManageUsers()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Kullanıcı adı ve şifre zorunludur." });
        if (!IsValidNationalId(req.NationalId))
            return BadRequest(new { error = "TC kimlik numarası zorunludur ve 11 haneli olmalıdır." });

        var username = req.Username.Trim();
        var branchExists = await _db.Branches.AsNoTracking().AnyAsync(x => x.Id == req.BranchId && x.IsActive, ct);
        if (!branchExists) return BadRequest(new { error = "Geçersiz veya pasif şube." });

        var exists = await _db.Users.AsNoTracking().AnyAsync(x => x.Username == username && !x.IsDeleted, ct);
        if (exists) return Conflict(new { error = "Bu kullanıcı adı zaten mevcut." });

        var normalizedRole = NormalizeRole(req.Role);
        if (normalizedRole.Equals("Owner", StringComparison.OrdinalIgnoreCase))
        {
            var existingOwner = await _db.Users.AsNoTracking()
                .AnyAsync(x => !x.IsDeleted && x.Role == "Owner", ct);
            if (existingOwner)
                return BadRequest(new { error = "Bu işletmede zaten bir Sahip kullanıcı var. Sahip rolü tek kişiye atanabilir." });
        }

        var perms = ResolvePermissions(normalizedRole, req);
        var user = new User
        {
            TenantId = _tenant.TenantId,
            BranchId = req.BranchId,
            DefaultBranchId = req.BranchId,
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password.Trim()),
            Role = normalizedRole,
            NationalId = NormalizeNationalId(req.NationalId)!,
            FullName = NormalizeNullable(req.FullName),
            Phone = NormalizeNullable(req.Phone),
            Email = NormalizeNullable(req.Email),
            IsActive = req.IsActive,
            CanManageUsers = perms.CanManageUsers,
            CanManageBranches = perms.CanManageBranches,
            CanSwitchBranches = perms.CanSwitchBranches,
            CanUseEInvoice = perms.CanUseEInvoice,
            CanUseEArchive = perms.CanUseEArchive
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = user.Id }, new { user.Id });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UserUpsertReq req, CancellationToken ct)
    {
        var canManage = CanManageUsers();
        if (!canManage && !IsCurrentUser(id)) return Forbid();

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (user is null) return NotFound();

        if (canManage)
        {
            if (string.IsNullOrWhiteSpace(req.Username))
                return BadRequest(new { error = "Kullanıcı adı zorunludur." });

            var username = req.Username.Trim();
            var branchExists = await _db.Branches.AsNoTracking().AnyAsync(x => x.Id == req.BranchId && x.IsActive, ct);
            if (!branchExists) return BadRequest(new { error = "Geçersiz veya pasif şube." });

            var exists = await _db.Users.AsNoTracking()
                .AnyAsync(x => x.Id != id && x.Username == username && !x.IsDeleted, ct);
            if (exists) return Conflict(new { error = "Bu kullanıcı adı başka bir kullanıcıya ait." });

            user.Username = username;
            user.BranchId = req.BranchId;
            user.DefaultBranchId = req.BranchId;
            var normalizedRole = NormalizeRole(req.Role);
            if (!string.Equals(user.Role, normalizedRole, StringComparison.OrdinalIgnoreCase))
            {
                if (normalizedRole.Equals("Owner", StringComparison.OrdinalIgnoreCase))
                {
                    var ownerExists = await _db.Users.AsNoTracking()
                        .AnyAsync(x => x.Id != id && !x.IsDeleted && x.Role == "Owner", ct);
                    if (ownerExists)
                        return BadRequest(new { error = "Bu işletmede zaten bir Sahip kullanıcı var. Sahip rolü tek kişiye atanabilir." });
                }
                else if (string.Equals(user.Role, "Owner", StringComparison.OrdinalIgnoreCase))
                {
                    var ownerCount = await _db.Users.AsNoTracking()
                        .CountAsync(x => !x.IsDeleted && x.Role == "Owner", ct);
                    if (ownerCount <= 1)
                        return BadRequest(new { error = "Sistemde en az bir Sahip kullanıcı bulunmalıdır." });
                }
            }
            user.Role = normalizedRole;
            user.IsActive = req.IsActive;
            if (!IsValidNationalId(req.NationalId))
                return BadRequest(new { error = "TC kimlik numarası zorunludur ve 11 haneli olmalıdır." });
            user.NationalId = NormalizeNationalId(req.NationalId)!;
            var perms = ResolvePermissions(normalizedRole, req);
            user.CanManageUsers = perms.CanManageUsers;
            user.CanManageBranches = perms.CanManageBranches;
            user.CanSwitchBranches = perms.CanSwitchBranches;
            user.CanUseEInvoice = perms.CanUseEInvoice;
            user.CanUseEArchive = perms.CanUseEArchive;
        }

        user.FullName = NormalizeNullable(req.FullName);
        user.Phone = NormalizeNullable(req.Phone);
        user.Email = NormalizeNullable(req.Email);

        await _db.SaveChangesAsync(ct);
        return Ok(new { user.Id });
    }

    [HttpPost("{id:guid}/change-password")]
    public async Task<IActionResult> ChangePassword(Guid id, [FromBody] ChangePasswordReq req, CancellationToken ct)
    {
        if (!CanManageUsers() && !IsCurrentUser(id)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.NewPassword))
            return BadRequest(new { error = "Yeni şifre zorunludur." });

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (user is null) return NotFound();

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword.Trim());
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Şifre güncellendi." });
    }

    [HttpGet("{id:guid}/salary-history")]
    public async Task<IActionResult> GetSalaryHistory(Guid id, CancellationToken ct)
    {
        if (!CanAccessUser(id)) return Forbid();

        var exists = await _db.Users.AsNoTracking().AnyAsync(x => x.Id == id && !x.IsDeleted, ct);
        if (!exists) return NotFound();

        var items = await _db.UserSalaryHistories
            .AsNoTracking()
            .Where(x => x.UserId == id && !x.IsDeleted)
            .OrderByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new SalaryHistoryDto(x.Id, x.Amount, x.EffectiveFrom, x.Note, x.CreatedAt))
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost("{id:guid}/salary-history")]
    public async Task<IActionResult> AddSalaryHistory(Guid id, [FromBody] AddSalaryHistoryReq req, CancellationToken ct)
    {
        if (!CanManageUsers()) return Forbid();
        if (req.Amount <= 0) return BadRequest(new { error = "Maaş tutarı sıfırdan büyük olmalıdır." });

        var targetUser = await _db.Users
            .AsNoTracking()
            .Where(x => x.Id == id && !x.IsDeleted)
            .Select(x => new
            {
                x.Id,
                x.BranchId,
                x.Username,
                x.FullName
            })
            .FirstOrDefaultAsync(ct);
        if (targetUser is null) return NotFound();

        var entry = new UserSalaryHistory
        {
            TenantId = _tenant.TenantId,
            UserId = id,
            Amount = Math.Round(req.Amount, 2, MidpointRounding.AwayFromZero),
            EffectiveFrom = req.EffectiveFrom == default ? DateTime.Today : req.EffectiveFrom,
            Note = NormalizeNullable(req.Note)
        };

        _db.UserSalaryHistories.Add(entry);
        await _db.SaveChangesAsync(ct);

        var actorUserId = GetCurrentUserId() ?? targetUser.Id;
        await UpsertMonthlySalaryReminderAsync(
            targetUser.Id,
            targetUser.BranchId,
            targetUser.FullName,
            targetUser.Username,
            actorUserId,
            ct);
        return Ok(new SalaryHistoryDto(entry.Id, entry.Amount, entry.EffectiveFrom, entry.Note, entry.CreatedAt));
    }

    private bool CanManageUsers()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (role.Equals("Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_manage_users");
    }

    private bool CanAccessUser(Guid id) => CanManageUsers() || IsCurrentUser(id);

    private bool IsCurrentUser(Guid id)
    {
        var currentUserId = GetCurrentUserId();
        return currentUserId.HasValue && currentUserId.Value == id;
    }

    private Guid? GetCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string NormalizeRole(string? role)
    {
        var value = (role ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) return "User";
        if (value.Equals("Owner", StringComparison.OrdinalIgnoreCase)) return "Owner";
        if (value.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "Admin";
        if (value.Equals("Manager", StringComparison.OrdinalIgnoreCase)) return "Admin";
        return "User";
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static UserPermissionSet ResolvePermissions(string normalizedRole, UserUpsertReq req)
    {
        var defaults = BuildDefaultPermissions(normalizedRole);
        if (normalizedRole.Equals("Owner", StringComparison.OrdinalIgnoreCase))
            return defaults;
        return new UserPermissionSet(
            req.CanManageUsers ?? defaults.CanManageUsers,
            req.CanManageBranches ?? defaults.CanManageBranches,
            req.CanSwitchBranches ?? defaults.CanSwitchBranches,
            req.CanUseEInvoice ?? defaults.CanUseEInvoice,
            req.CanUseEArchive ?? defaults.CanUseEArchive);
    }

    private static UserPermissionSet BuildDefaultPermissions(string normalizedRole)
    {
        if (normalizedRole.Equals("Owner", StringComparison.OrdinalIgnoreCase))
            return new UserPermissionSet(true, true, true, true, true);
        if (normalizedRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return new UserPermissionSet(false, true, false, true, true);
        return new UserPermissionSet(false, false, false, false, false);
    }

    private readonly record struct UserPermissionSet(
        bool CanManageUsers,
        bool CanManageBranches,
        bool CanSwitchBranches,
        bool CanUseEInvoice,
        bool CanUseEArchive
    );

    private static string? NormalizeNullable(string? value)
    {
        var cleaned = (value ?? "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? NormalizeNationalId(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(digits) ? null : digits;
    }

    private static bool IsValidNationalId(string? value)
    {
        var digits = NormalizeNationalId(value);
        return !string.IsNullOrWhiteSpace(digits) && digits.Length == 11;
    }

    private async Task UpsertMonthlySalaryReminderAsync(
        Guid targetUserId,
        Guid branchId,
        string? fullName,
        string username,
        Guid actorUserId,
        CancellationToken ct)
    {
        var displayName = string.IsNullOrWhiteSpace(fullName) ? username : fullName.Trim();
        var safeName = SanitizeReminderTagValue(displayName);
        var userTag = $"[USER_ID:{targetUserId}]";
        var now = DateTime.UtcNow;
        var firstRunAt = now.AddMonths(1);
        var title = $"Maaş Hatırlatma - {displayName}";
        var description =
            $"[Özel] {displayName} için aylık maaş ödeme hatırlatması." +
            $" [ENTITY_TYPE:USER][ENTITY_NAME:{safeName}]{userTag}";

        var existing = await _db.BranchReminders.FirstOrDefaultAsync(
            x => x.TenantId == _tenant.TenantId
                 && x.BranchId == branchId
                 && !x.IsDeleted
                 && x.Frequency == ReminderFrequency.Monthly
                 && x.Description.Contains(userTag),
            ct);

        if (existing is null)
        {
            _db.BranchReminders.Add(new BranchReminder
            {
                TenantId = _tenant.TenantId,
                BranchId = branchId,
                UserId = actorUserId,
                Title = title,
                Description = description,
                Frequency = ReminderFrequency.Monthly,
                StartsAt = firstRunAt,
                NextRunAt = firstRunAt,
                IsActive = true
            });
        }
        else
        {
            existing.UserId = actorUserId;
            existing.Title = title;
            existing.Description = description;
            existing.Frequency = ReminderFrequency.Monthly;
            existing.StartsAt = firstRunAt;
            existing.NextRunAt = firstRunAt;
            existing.LastRunAt = null;
            existing.IsActive = true;
            existing.IsDeleted = false;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string SanitizeReminderTagValue(string? value)
    {
        return (value ?? string.Empty).Replace("]", ")").Trim();
    }
}
