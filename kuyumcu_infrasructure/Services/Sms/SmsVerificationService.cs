using System.Security.Cryptography;
using System.Text;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace kuyumcu_infrastructure.Services.Sms;

public sealed class SmsVerificationService : ISmsVerificationService
{
    public const string PurposePasswordReset = "PasswordReset";
    public const string PurposeBusinessRegistration = "BusinessRegistration";

    private readonly AppDbContext _db;
    private readonly ISmsSender _sms;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SmsVerificationService> _logger;

    public SmsVerificationService(
        AppDbContext db,
        ISmsSender sms,
        IConfiguration cfg,
        ILogger<SmsVerificationService> logger)
    {
        _db = db;
        _sms = sms;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<SmsSendResult> SendPasswordResetAsync(Guid tenantId, string username, CancellationToken ct = default)
    {
        var normalizedUsername = (username ?? "").Trim();
        if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedUsername))
            throw new ArgumentException("Kullanıcı adı zorunludur.");

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u =>
                u.TenantId == tenantId &&
                u.Username == normalizedUsername &&
                !u.IsDeleted &&
                u.IsActive, ct);

        if (user is null || string.IsNullOrWhiteSpace(user.Phone) ||
            !PhoneNormalizer.TryNormalizeTurkishMobile(user.Phone, out var phoneDigits))
        {
            return new SmsSendResult
            {
                SessionId = Guid.Empty,
                MaskedPhone = "",
                ResendAfterSeconds = GetResendCooldownSeconds()
            };
        }

        return await SendCodeAsync(
            PurposePasswordReset,
            phoneDigits,
            tenantId,
            user.Id,
            normalizedUsername,
            "Kuyumcu şifre sıfırlama kodunuz: {0}. Kod 5 dakika geçerlidir.",
            ct);
    }

    public async Task<SmsSendResult> SendBusinessRegistrationAsync(CancellationToken ct = default)
    {
        var developerPhone = (_cfg["Sms:DeveloperPhone"] ?? "").Trim();
        if (!PhoneNormalizer.TryNormalizeTurkishMobile(developerPhone, out var phoneDigits))
            throw new InvalidOperationException("Sms:DeveloperPhone geçerli bir mobil numara olmalıdır (05XXXXXXXXX).");

        return await SendCodeAsync(
            PurposeBusinessRegistration,
            phoneDigits,
            null,
            null,
            null,
            "Kuyumcu yeni işletme kayıt kodunuz: {0}. Kod 5 dakika geçerlidir.",
            ct);
    }

    public async Task<SmsVerifyResult> VerifyCodeAsync(Guid sessionId, string code, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Oturum geçersiz.");

        var normalizedCode = (code ?? "").Trim();
        if (normalizedCode.Length != GetCodeLength())
            throw new ArgumentException("Doğrulama kodu geçersiz.");

        var entry = await _db.SmsVerificationCodes
            .FirstOrDefaultAsync(x => x.Id == sessionId && !x.IsDeleted && !x.IsUsed, ct);

        if (entry is null || entry.IsVerified)
            throw new InvalidOperationException("Doğrulama oturumu bulunamadı.");

        if (entry.ExpiresAtUtc < DateTime.UtcNow)
            throw new InvalidOperationException("Doğrulama kodunun süresi doldu.");

        entry.AttemptCount++;
        if (entry.AttemptCount > GetMaxAttempts())
        {
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Çok fazla hatalı deneme. Yeni kod isteyin.");
        }

        if (!VerifyCodeHash(normalizedCode, entry.CodeHash))
        {
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Doğrulama kodu hatalı.");
        }

        entry.IsVerified = true;
        entry.VerificationToken = Guid.NewGuid().ToString("N");
        entry.TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(GetTokenExpiryMinutes());
        await _db.SaveChangesAsync(ct);

        return new SmsVerifyResult
        {
            VerificationToken = entry.VerificationToken!,
            ExpiresAtUtc = entry.TokenExpiresAtUtc!.Value
        };
    }

    public async Task<(Guid TenantId, Guid UserId)?> ConsumePasswordResetTokenAsync(string verificationToken, CancellationToken ct = default)
    {
        var entry = await FindVerifiedTokenAsync(PurposePasswordReset, verificationToken, ct);
        if (entry?.TenantId is null || entry.UserId is null)
            return null;

        entry.IsUsed = true;
        await _db.SaveChangesAsync(ct);
        return (entry.TenantId.Value, entry.UserId.Value);
    }

    public async Task<bool> ConsumeBusinessRegistrationTokenAsync(string verificationToken, CancellationToken ct = default)
    {
        var entry = await FindVerifiedTokenAsync(PurposeBusinessRegistration, verificationToken, ct);
        if (entry is null) return false;

        entry.IsUsed = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<SmsVerificationCode?> FindVerifiedTokenAsync(string purpose, string verificationToken, CancellationToken ct)
    {
        var token = (verificationToken ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;

        var entry = await _db.SmsVerificationCodes
            .FirstOrDefaultAsync(x =>
                x.Purpose == purpose &&
                x.VerificationToken == token &&
                x.IsVerified &&
                !x.IsUsed &&
                !x.IsDeleted, ct);

        if (entry is null || entry.TokenExpiresAtUtc is null || entry.TokenExpiresAtUtc < DateTime.UtcNow)
            return null;

        return entry;
    }

    private async Task<SmsSendResult> SendCodeAsync(
        string purpose,
        string phoneDigits,
        Guid? tenantId,
        Guid? userId,
        string? username,
        string messageTemplate,
        CancellationToken ct)
    {
        var cooldown = GetResendCooldownSeconds();
        var recent = await _db.SmsVerificationCodes
            .AsNoTracking()
            .Where(x =>
                x.Purpose == purpose &&
                x.Phone == phoneDigits &&
                !x.IsUsed &&
                !x.IsDeleted &&
                (tenantId == null || x.TenantId == tenantId))
            .OrderByDescending(x => x.LastSentAtUtc)
            .FirstOrDefaultAsync(ct);

        if (recent is not null)
        {
            var elapsed = (DateTime.UtcNow - recent.LastSentAtUtc).TotalSeconds;
            if (elapsed < cooldown)
            {
                return new SmsSendResult
                {
                    SessionId = recent.Id,
                    MaskedPhone = PhoneNormalizer.Mask(phoneDigits),
                    ResendAfterSeconds = (int)Math.Ceiling(cooldown - elapsed)
                };
            }
        }

        var code = GenerateNumericCode(GetCodeLength());
        var entry = new SmsVerificationCode
        {
            Purpose = purpose,
            TenantId = tenantId,
            UserId = userId,
            Username = username,
            Phone = phoneDigits,
            CodeHash = HashCode(code),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(GetCodeExpiryMinutes()),
            LastSentAtUtc = DateTime.UtcNow
        };

        _db.SmsVerificationCodes.Add(entry);
        await _db.SaveChangesAsync(ct);

        try
        {
            await _sms.SendAsync(phoneDigits, string.Format(messageTemplate, code), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMS gönderimi başarısız. SessionId={SessionId}", entry.Id);
            throw new InvalidOperationException("SMS gönderilemedi. Lütfen daha sonra tekrar deneyin.");
        }

        return new SmsSendResult
        {
            SessionId = entry.Id,
            MaskedPhone = PhoneNormalizer.Mask(phoneDigits),
            ResendAfterSeconds = cooldown
        };
    }

    private int GetCodeLength() => Math.Clamp(_cfg.GetValue("Sms:CodeLength", 6), 4, 8);
    private int GetCodeExpiryMinutes() => Math.Clamp(_cfg.GetValue("Sms:CodeExpiryMinutes", 5), 1, 30);
    private int GetTokenExpiryMinutes() => Math.Clamp(_cfg.GetValue("Sms:TokenExpiryMinutes", 10), 1, 60);
    private int GetResendCooldownSeconds() => Math.Clamp(_cfg.GetValue("Sms:ResendCooldownSeconds", 60), 30, 600);
    private int GetMaxAttempts() => Math.Clamp(_cfg.GetValue("Sms:MaxAttempts", 5), 3, 10);

    private static string GenerateNumericCode(int length)
    {
        var max = (int)Math.Pow(10, length);
        var value = RandomNumberGenerator.GetInt32(0, max);
        return value.ToString().PadLeft(length, '0');
    }

    private static string HashCode(string code)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(bytes);
    }

    private static bool VerifyCodeHash(string code, string hash)
    {
        var computed = HashCode(code);
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(computed),
            Convert.FromHexString(hash));
    }
}
