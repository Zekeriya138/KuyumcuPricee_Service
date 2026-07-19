namespace kuyumcu_application.Abstractions;

public sealed class SmsSendResult
{
    public Guid SessionId { get; init; }
    public string MaskedPhone { get; init; } = "";
    public int ResendAfterSeconds { get; init; }
}

public sealed class SmsVerifyResult
{
    public string VerificationToken { get; init; } = "";
    public DateTime ExpiresAtUtc { get; init; }
}

public interface ISmsVerificationService
{
    Task<SmsSendResult> SendPasswordResetAsync(Guid tenantId, string username, CancellationToken ct = default);
    Task<SmsSendResult> SendBusinessRegistrationAsync(CancellationToken ct = default);
    Task<SmsVerifyResult> VerifyCodeAsync(Guid sessionId, string code, CancellationToken ct = default);
    Task<(Guid TenantId, Guid UserId)?> ConsumePasswordResetTokenAsync(string verificationToken, CancellationToken ct = default);
    Task<bool> ConsumeBusinessRegistrationTokenAsync(string verificationToken, CancellationToken ct = default);
}
