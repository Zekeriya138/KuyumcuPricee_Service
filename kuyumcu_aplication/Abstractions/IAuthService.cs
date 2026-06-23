using kuyumcu_domain.Entities;

namespace kuyumcu_application.Abstractions;

public interface IAuthService
{
    Task<User?> LoginAsync(Guid tenantId ,string username, string password);
    Task<bool> ResetPasswordAsync(Guid tenantId, string username, string nationalId, string newPassword);
    Task<bool> ResetCredentialsByDeveloperPasswordAsync(Guid tenantId, string nationalId, string newUsername, string newPassword);
}
