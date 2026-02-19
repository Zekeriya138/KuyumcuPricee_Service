using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
namespace kuyumcu_infrastructure.Services;

// AuthService.cs
public class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    public AuthService(AppDbContext db) => _db = db;

    public async Task<User?> LoginAsync(Guid tenantId, string username, string password)
    {
        // Tenant filtresi + IsDeleted=false
        var user = await _db.Users
            .Include(u => u.Branch)
            .AsNoTracking()
            .FirstOrDefaultAsync(u =>
                u.TenantId == tenantId &&
                u.Username == username &&
                !u.IsDeleted);

        if (user is null) return null;

        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
    }

    public async Task<bool> ResetPasswordAsync(Guid tenantId, string username, string nationalId, DateTime birthDate, string newPassword)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u =>
            u.TenantId == tenantId &&
            u.Username == username &&
            !u.IsDeleted);

        if (user is null) return false;
        if ((user.NationalId ?? "").Trim() != (nationalId ?? "").Trim()) return false;
        if (user.BirthDate?.Date != birthDate.Date) return false;

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.SaveChangesAsync();
        return true;
    }
}
