namespace kuyumcu_domain.Entities;

public enum AccountType
{
    Asset = 0,
    Liability = 1,
    Equity = 2,
    Income = 3,
    Expense = 4
}

public class Account : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public AccountType Type { get; set; } = AccountType.Asset;
    public bool IsSystemAccount { get; set; }
}

public class JournalEntry : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } = "";
    public ICollection<JournalLine> Lines { get; set; } = new List<JournalLine>();
}

public class JournalLine : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid JournalEntryId { get; set; }
    public Guid AccountId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    public JournalEntry JournalEntry { get; set; } = null!;
    public Account Account { get; set; } = null!;
}
