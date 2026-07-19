namespace kuyumcu_domain.Entities;

public class SmsVerificationCode : Entity
{
    public string Purpose { get; set; } = "";
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string Phone { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public string? VerificationToken { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? TokenExpiresAtUtc { get; set; }
    public DateTime LastSentAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public bool IsVerified { get; set; }
    public bool IsUsed { get; set; }
}
