using kuyumcu_domain.Enums;

namespace kuyumcu_domain.Entities;

/// <summary>Şube bazlı tekrar eden hatırlatma kaydı.</summary>
public class BranchReminder : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public ReminderFrequency Frequency { get; set; } = ReminderFrequency.Daily;
    public DateTime StartsAt { get; set; } = DateTime.UtcNow;
    public DateTime NextRunAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
    public bool IsActive { get; set; } = true;
}
