using kuyumcu_domain.Entities;

namespace kuyumcu_application.Abstractions;

public interface IAuthService
{
    Task<User?> LoginAsync(Guid tenantId ,string username, string password);
    Task<bool> ResetPasswordByUserIdAsync(Guid tenantId, Guid userId, string newPassword);
}
