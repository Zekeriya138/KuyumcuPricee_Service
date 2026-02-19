using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
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

    public AuthController(IAuthService auth, IConfiguration cfg)
    {
        _auth = auth; _cfg = cfg;
    }

    public record LoginDto(string Username, string Password);
    public record ResetDto(string Username, string NationalId, DateTime BirthDate, string NewPassword);

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

        var ok = await _auth.ResetPasswordAsync(tenantId, dto.Username, dto.NationalId, dto.BirthDate, dto.NewPassword);
        return ok ? Ok(new { message = "Şifre güncellendi" })
                  : BadRequest(new { error = "Bilgiler doğrulanamadı" });
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var name = User.FindFirstValue(ClaimTypes.Name) ?? "";
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "User";
        return Ok(new { id, username = name, role });
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

        // 5) Kayıt
        var user = new User
        {
            TenantId = tenantId,
            BranchId = req.BranchId,
            Username = req.Username.Trim(),
            PasswordHash = hash,
            Role = string.IsNullOrWhiteSpace(req.Role) ? "User" : req.Role.Trim(),
            FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim(),
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            NationalId = national,         // 🔹 eklendi
            BirthDate = req.BirthDate,     // 🔹 eklendi
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

    private string CreateJwt(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role ?? "User"),
        // 🔽 tenant claim’i eklendi
        new Claim("tenant_id", user.TenantId.ToString()),
        // istersen şube de ekleyebilirsin:
        new Claim("branch_id", user.BranchId.ToString())
    };

        var jwt = new JwtSecurityToken(
            issuer: _cfg["Jwt:Issuer"],
            audience: _cfg["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_cfg.GetValue<int>("Jwt:ExpireMinutes")),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

}
