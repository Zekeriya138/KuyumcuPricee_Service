using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController, Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IConfiguration _cfg;
    private const string FallbackBusinessRegistrationPassword = "DevOnly_2026";

    public AuthController(IAuthService auth, IConfiguration cfg)
    {
        _auth = auth;
        _cfg = cfg;
    }

    public record LoginDto(string Username, string Password);
    public record ResetDto(string Username, string NationalId, string NewPassword);

    // ✅ Yeni kayıt için DTO
    public record RegisterUserReq(
        Guid BranchId,
        string Username,
        string Password,
        string Role,
        string? FullName,
        string? Email,
        string? NationalId,   // 🔹 Şifre sıfırlama için eklendi
        DateTime? BirthDate   // 🔹 Şifre sıfırlama için eklendi
    );
    public record RegisterBusinessReq(
        string BusinessName,
        string? OwnerName,
        string OwnerUsername,
        string OwnerPassword,
        string OwnerNationalId,
        string OwnerPhone,
        string DeveloperPassword);
    public record ResolveTenantReq(Guid TenantId, string BusinessName);
    public record VerifyDeveloperPasswordReq(string DeveloperPassword);
    public record ResetByDeveloperPasswordReq(string NationalId, string NewUsername, string NewPassword, string DeveloperPassword);

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
    [FromHeader(Name = "X-Tenant-Id")] Guid tenantId,
    [FromBody] LoginDto req)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "X-Tenant-Id header zorunludur." });

        var user = await _auth.LoginAsync(tenantId, req.Username, req.Password);
        if (user is null) return Unauthorized(new { error = "Kullanıcı adı/şifre hatalı" });

        var token = CreateJwt(user);
        return Ok(new { token, user = new { user.Id, user.Username, user.Role, user.BranchId } });
    }

    [HttpPost("reset")]
    [AllowAnonymous]
    public async Task<IActionResult> Reset(
        [FromHeader(Name = "X-Tenant-Id")] Guid tenantId,
        [FromBody] ResetDto dto)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "X-Tenant-Id header zorunludur." });

        if (string.IsNullOrWhiteSpace(dto.Username) ||
            string.IsNullOrWhiteSpace(dto.NationalId) ||
            string.IsNullOrWhiteSpace(dto.NewPassword))
        {
            return BadRequest(new { error = "Username, NationalId ve NewPassword zorunludur." });
        }

        if (!IsValidTckn(dto.NationalId))
            return BadRequest(new { error = "TC kimlik numarası geçersiz." });

        var ok = await _auth.ResetPasswordAsync(tenantId, dto.Username, dto.NationalId, dto.NewPassword);
        return ok ? Ok(new { message = "Şifre güncellendi" })
                  : BadRequest(new { error = "Bilgiler doğrulanamadı" });
    }

    [HttpPost("reset-by-dev-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetByDeveloperPassword(
        [FromHeader(Name = "X-Tenant-Id")] Guid tenantId,
        [FromBody] ResetByDeveloperPasswordReq dto)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "X-Tenant-Id header zorunludur." });
        if (string.IsNullOrWhiteSpace(dto.NationalId) ||
            string.IsNullOrWhiteSpace(dto.NewUsername) ||
            string.IsNullOrWhiteSpace(dto.NewPassword) ||
            string.IsNullOrWhiteSpace(dto.DeveloperPassword))
            return BadRequest(new { error = "NationalId, NewUsername, NewPassword ve DeveloperPassword zorunludur." });
        if (!IsValidTckn(dto.NationalId))
            return BadRequest(new { error = "TC kimlik numarası geçersiz." });

        var expectedPassword = _cfg["BusinessRegistration:DeveloperPassword"];
        if (string.IsNullOrWhiteSpace(expectedPassword))
            expectedPassword = FallbackBusinessRegistrationPassword;
        if (!string.Equals((dto.DeveloperPassword ?? "").Trim(), expectedPassword, StringComparison.Ordinal))
            return BadRequest(new { error = "Geliştirici şifresi hatalı." });

        var ok = await _auth.ResetCredentialsByDeveloperPasswordAsync(
            tenantId,
            dto.NationalId,
            dto.NewUsername,
            dto.NewPassword);
        return ok ? Ok(new { message = "Kullanıcı adı ve şifre güncellendi." })
                  : BadRequest(new { error = "Bilgiler doğrulanamadı veya kullanıcı adı kullanılıyor." });
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var name = User.FindFirstValue(ClaimTypes.Name) ?? "";
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "User";
        var isOwner = string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase);
        return Ok(new
        {
            id,
            username = name,
            role,
            canManageUsers = isOwner || HasPermissionClaim("perm_manage_users"),
            canManageBranches = isOwner || HasPermissionClaim("perm_manage_branches"),
            canSwitchBranches = isOwner || HasPermissionClaim("perm_switch_branches"),
            canUseEInvoice = isOwner || HasPermissionClaim("perm_einvoice"),
            canUseEArchive = isOwner || HasPermissionClaim("perm_earchive")
        });
    }

    [AllowAnonymous]
    [HttpPost("register-business")]
    public async Task<IActionResult> RegisterBusiness(
        [FromBody] RegisterBusinessReq req,
        [FromServices] AppDbContext db,
        [FromServices] ITenantContext tenantContext,
        CancellationToken ct)
    {
        var businessName = (req.BusinessName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(businessName))
            return BadRequest(new { error = "İşletme adı zorunludur." });
        var ownerUsername = (req.OwnerUsername ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ownerUsername))
            return BadRequest(new { error = "Owner kullanıcı adı zorunludur." });
        var ownerPassword = (req.OwnerPassword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ownerPassword) || ownerPassword.Length < 4)
            return BadRequest(new { error = "Owner şifresi en az 4 karakter olmalıdır." });
        var ownerNationalId = (req.OwnerNationalId ?? "").Trim();
        if (!IsValidTckn(ownerNationalId))
            return BadRequest(new { error = "Owner TC geçersiz. 11 haneli ve algoritma doğrulamasını sağlamalıdır." });
        var ownerPhone = (req.OwnerPhone ?? "").Trim();
        if (!IsValidPhone(ownerPhone))
            return BadRequest(new { error = "Owner telefon formatı geçersiz. Örn: 05XXXXXXXXX" });

        var expectedPassword = _cfg["BusinessRegistration:DeveloperPassword"];
        if (string.IsNullOrWhiteSpace(expectedPassword))
            expectedPassword = FallbackBusinessRegistrationPassword;

        if (!string.Equals((req.DeveloperPassword ?? "").Trim(), expectedPassword, StringComparison.Ordinal))
            return BadRequest(new { error = "Geliştirici şifresi hatalı." });

        var exists = await db.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Name == businessName, ct);
        if (exists)
            return Conflict(new { error = "Bu işletme adı zaten kayıtlı." });

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = businessName,
            CreatedAt = DateTime.UtcNow
        };

        tenantContext.TenantId = tenant.Id;
        tenantContext.BranchId = null;

        var merkezBranch = new Branch
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Merkez",
            Code = "MRKZ",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Tenants.Add(tenant);
        db.Branches.Add(merkezBranch);
        await EnsureUserPhoneColumnAsync(db, ct);

        var ownerUser = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            BranchId = merkezBranch.Id,
            Username = ownerUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(ownerPassword),
            Role = "Owner",
            NationalId = ownerNationalId,
            Phone = ownerPhone,
            FullName = string.IsNullOrWhiteSpace(req.OwnerName) ? null : req.OwnerName.Trim(),
            CanManageUsers = true,
            CanManageBranches = true,
            CanSwitchBranches = true,
            CanUseEInvoice = true,
            CanUseEArchive = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(ownerUser);
        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            tenantId = tenant.Id,
            businessName = tenant.Name,
            ownerName = string.IsNullOrWhiteSpace(req.OwnerName) ? null : req.OwnerName.Trim(),
            branchId = merkezBranch.Id,
            ownerUserId = ownerUser.Id,
            ownerUsername = ownerUser.Username,
            branchCount = 1,
            userCount = 1
        });
    }

    [AllowAnonymous]
    [HttpPost("resolve-tenant")]
    public async Task<IActionResult> ResolveTenant(
        [FromBody] ResolveTenantReq req,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        if (req.TenantId == Guid.Empty)
            return BadRequest(new { error = "İşletme ID zorunludur." });
        var businessName = (req.BusinessName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(businessName))
            return BadRequest(new { error = "İşletme adı zorunludur." });

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == req.TenantId, ct);
        if (tenant is null)
            return NotFound(new { error = "İşletme bulunamadı." });

        if (!string.Equals((tenant.Name ?? "").Trim(), businessName, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "İşletme adı ile ID eşleşmiyor." });

        return Ok(new { tenantId = tenant.Id, businessName = tenant.Name });
    }

    [AllowAnonymous]
    [HttpPost("verify-dev-password")]
    public IActionResult VerifyDeveloperPassword([FromBody] VerifyDeveloperPasswordReq req)
    {
        var expectedPassword = _cfg["BusinessRegistration:DeveloperPassword"];
        if (string.IsNullOrWhiteSpace(expectedPassword))
            expectedPassword = FallbackBusinessRegistrationPassword;

        if (!string.Equals((req.DeveloperPassword ?? "").Trim(), expectedPassword, StringComparison.Ordinal))
            return BadRequest(new { error = "Geliştirici şifresi hatalı." });

        return Ok(new { valid = true });
    }

    // ✅ Yeni kullanıcı ekleme (register)
    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromHeader(Name = "X-Tenant-Id")] Guid tenantId,
        [FromBody] RegisterUserReq req,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "X-Tenant-Id header zorunludur." });

        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Username ve Password zorunludur." });

        // 1) Branch var mı?
        var branchOk = await db.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);

        if (!branchOk)
            return BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." });

        // 2) Kullanıcı adı benzersiz mi?
        var exists = await db.Users
            .AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.Username == req.Username, ct);

        if (exists)
            return Conflict(new { error = "Bu kullanıcı adı zaten mevcut." });

        // 3) NationalId kontrolü (opsiyonel)
        string? national = string.IsNullOrWhiteSpace(req.NationalId) ? null : req.NationalId.Trim();
        if (!string.IsNullOrEmpty(national) && national.Length != 11)
            return BadRequest(new { error = "NationalId 11 haneli olmalıdır." });

        // 4) Şifre hash
        var hash = BCrypt.Net.BCrypt.HashPassword(req.Password);

        var normalizedRole = NormalizeRole(req.Role);
        var defaultPerms = BuildDefaultPermissions(normalizedRole);

        // 5) Kayıt
        var user = new User
        {
            TenantId = tenantId,
            BranchId = req.BranchId,
            Username = req.Username.Trim(),
            PasswordHash = hash,
            Role = normalizedRole,
            FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim(),
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            NationalId = national,         // 🔹 eklendi
            BirthDate = req.BirthDate,     // 🔹 eklendi
            CanManageUsers = defaultPerms.canManageUsers,
            CanManageBranches = defaultPerms.canManageBranches,
            CanSwitchBranches = defaultPerms.canSwitchBranches,
            CanUseEInvoice = defaultPerms.canUseEInvoice,
            CanUseEArchive = defaultPerms.canUseEArchive,
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Created(string.Empty, new
        {
            user.Id,
            user.Username,
            user.Role,
            user.BranchId,
            user.TenantId
        });
    }

    public record SwitchBranchDto(Guid BranchId);

    /// <summary>Seçilen şube için yeni JWT üretir (claim: branch_id güncellenir). WPF/WinForms şube seçim akışı.</summary>
    [HttpPost("switch-branch")]
    [Authorize]
    public async Task<IActionResult> SwitchBranch(
        [FromHeader(Name = "X-Tenant-Id")] Guid tenantId,
        [FromBody] SwitchBranchDto req,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty)
            return BadRequest(new { error = "X-Tenant-Id header zorunludur." });

        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
            return Unauthorized(new { error = "Geçersiz oturum." });

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId && !u.IsDeleted, ct);

        if (user is null)
            return Unauthorized(new { error = "Kullanıcı bulunamadı." });

        // Şube geçişi yetkisi olmayan kullanıcılar sadece atanmış şubelerinde çalışabilir.
        if (!CanSwitchBranches(user) && user.BranchId != req.BranchId)
            return StatusCode(403, new { error = "Bu kullanıcı sadece atanmış olduğu şubeye giriş yapabilir." });

        var branchOk = await db.Branches
            .AsNoTracking()
            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId && b.IsActive, ct);

        if (!branchOk)
            return BadRequest(new { error = "Geçersiz veya pasif şube." });

        var token = CreateJwt(user, req.BranchId);
        return Ok(new { token, user = new { user.Id, user.Username, user.Role, BranchId = req.BranchId } });
    }

    private string CreateJwt(User user, Guid? branchIdOverride = null)
    {
        var branchId = branchIdOverride ?? user.BranchId;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role ?? "User"),
        new Claim("perm_manage_users", EffectivePermission(user.CanManageUsers, user.Role).ToString()),
        new Claim("perm_manage_branches", EffectivePermission(user.CanManageBranches, user.Role).ToString()),
        new Claim("perm_switch_branches", EffectivePermission(user.CanSwitchBranches, user.Role).ToString()),
        new Claim("perm_einvoice", EffectivePermission(user.CanUseEInvoice, user.Role).ToString()),
        new Claim("perm_earchive", EffectivePermission(user.CanUseEArchive, user.Role).ToString()),
        // 🔽 tenant claim’i eklendi
        new Claim("tenant_id", user.TenantId.ToString()),
        new Claim("branch_id", branchId.ToString())
    };

        var jwt = new JwtSecurityToken(
            issuer: _cfg["Jwt:Issuer"],
            audience: _cfg["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_cfg.GetValue<int>("Jwt:ExpireMinutes")),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task EnsureUserPhoneColumnAsync(AppDbContext db, CancellationToken ct)
    {
        const string sql = """
                           IF COL_LENGTH('Users', 'Phone') IS NULL
                           BEGIN
                               ALTER TABLE [Users] ADD [Phone] nvarchar(32) NULL;
                           END
                           """;
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private static bool IsValidPhone(string value)
    {
        var digits = Regex.Replace(value ?? "", "[^0-9]", "");
        return Regex.IsMatch(digits, "^05[0-9]{9}$");
    }

    private static bool IsValidTckn(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var t = value.Trim();
        if (t.Length != 11 || !t.All(char.IsDigit)) return false;
        if (t[0] == '0') return false;

        var d = t.Select(ch => ch - '0').ToArray();
        var oddSum = d[0] + d[2] + d[4] + d[6] + d[8];
        var evenSum = d[1] + d[3] + d[5] + d[7];
        var check10 = ((oddSum * 7) - evenSum) % 10;
        if (check10 < 0) check10 += 10;
        if (check10 != d[9]) return false;

        var check11 = d.Take(10).Sum() % 10;
        return check11 == d[10];
    }

    private static bool EffectivePermission(bool value, string? role)
    {
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return value;
    }

    private static bool CanSwitchBranches(User user)
    {
        return string.Equals(user.Role, "Owner", StringComparison.OrdinalIgnoreCase)
               || user.CanSwitchBranches;
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
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

    private static (bool canManageUsers, bool canManageBranches, bool canSwitchBranches, bool canUseEInvoice, bool canUseEArchive)
        BuildDefaultPermissions(string role)
    {
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return (true, true, true, true, true);
        if (string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return (false, true, false, true, true);
        return (false, false, false, false, false);
    }

}
