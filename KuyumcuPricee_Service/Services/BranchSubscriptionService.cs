using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;

namespace KUYUMCU.Price_Service.Services;

public sealed class BranchSubscriptionAccess
{
    public bool IsActive { get; init; }
    public bool IncludesEInvoice { get; init; }
    public bool IncludesAiAssistant { get; init; }
    public DateTime? EndsAtUtc { get; init; }
    public string PlanLabel { get; init; } = "";
}

public interface IBranchSubscriptionService
{
    Task<BranchSubscriptionAccess> GetAccessAsync(Guid tenantId, Guid branchId, CancellationToken ct);
}

public sealed class BranchSubscriptionService : IBranchSubscriptionService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;

    public BranchSubscriptionService(AppDbContext db, IConfiguration cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public async Task<BranchSubscriptionAccess> GetAccessAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        if (!IsSubscriptionEnforcementEnabled())
        {
            return new BranchSubscriptionAccess
            {
                IsActive = true,
                IncludesAiAssistant = true,
                IncludesEInvoice = true,
                PlanLabel = "Abonelik Kontrolü Geçici Olarak Pasif"
            };
        }

        var now = DateTime.UtcNow;
        var sub = await _db.BranchSubscriptions
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            return new BranchSubscriptionAccess
            {
                IsActive = false,
                IncludesAiAssistant = false,
                IncludesEInvoice = false,
                PlanLabel = "Abonelik Yok"
            };
        }

        var active = sub.Status == SubscriptionStatus.Active &&
                     (sub.IsLifetime || !sub.EndsAtUtc.HasValue || sub.EndsAtUtc.Value >= now);

        var label = BuildPlanLabel(sub);
        return new BranchSubscriptionAccess
        {
            IsActive = active,
            IncludesAiAssistant = active && sub.IncludesAiAssistant,
            IncludesEInvoice = active && sub.IncludesEInvoice,
            EndsAtUtc = sub.EndsAtUtc,
            PlanLabel = label
        };
    }

    private static string BuildPlanLabel(BranchSubscription sub)
    {
        var period = sub.PeriodType switch
        {
            SubscriptionPeriodType.Turnkey => "Anahtar Teslim",
            SubscriptionPeriodType.Yearly => "Yıllık",
            SubscriptionPeriodType.Monthly => "Aylık",
            _ => "Bilinmeyen"
        };

        var pkg = sub.PackageType switch
        {
            SubscriptionPackageType.Full => "Tam Paket",
            SubscriptionPackageType.Standard => "Standart Paket",
            _ => "Bilinmeyen Paket"
        };

        return $"{period} / {pkg}";
    }

    private bool IsSubscriptionEnforcementEnabled()
    {
        return _cfg.GetValue<bool?>("Subscriptions:EnforcementEnabled") ?? true;
    }
}
