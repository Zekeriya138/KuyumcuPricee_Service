namespace kuyumcu_domain.Entities;

public sealed class UserSalaryHistory : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public string? Note { get; set; }

    public User User { get; set; } = null!;
}

public sealed class RateDisplaySetting : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public string Code { get; set; } = "";
    public bool IsVisible { get; set; } = true;
    public decimal BidTlOffset { get; set; }
    public decimal AskTlOffset { get; set; }
    public decimal TlOffset { get; set; }
    public string? CustomDisplay { get; set; }
}
