namespace kuyumcu_domain.Entities;

public enum SubscriptionPeriodType
{
    Turnkey = 1,
    Monthly = 2,
    Yearly = 3
}

public enum SubscriptionPackageType
{
    Full = 1,
    Standard = 2
}

public enum SubscriptionStatus
{
    PendingPayment = 0,
    Active = 1,
    Expired = 2,
    Cancelled = 3,
    Failed = 4
}

public sealed class BranchSubscription : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }

    public SubscriptionPeriodType PeriodType { get; set; }
    public SubscriptionPackageType PackageType { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.PendingPayment;

    public bool IsLifetime { get; set; }
    public bool IncludesEInvoice { get; set; }
    public bool IncludesAiAssistant { get; set; }

    public decimal Price { get; set; }
    public string Currency { get; set; } = "TRY";

    public DateTime? StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public DateTime? LastPaymentAtUtc { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }

    public string? IyzipayConversationId { get; set; }
    public string? IyzipayToken { get; set; }
    public string? IyzipayPaymentId { get; set; }
    public string? IyzipayStatus { get; set; }
    public string? IyzipayRawResponse { get; set; }
    public string? Note { get; set; }

    public Branch Branch { get; set; } = null!;
}
