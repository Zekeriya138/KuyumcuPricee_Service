namespace kuyumcu_domain.Entities;

/// <summary>Gelir/gider defteri satırı.</summary>
public class CashTransaction : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid CashAccountId { get; set; }

    /// <summary>Income / Expense / Transfer</summary>
    public string TxType { get; set; } = "Income";
    /// <summary>Sale / Purchase / Manual / DayEnd</summary>
    public string SourceModule { get; set; } = "";
    public string Currency { get; set; } = "TL";
    public decimal Amount { get; set; }
    public DateTime TxDate { get; set; } = DateTime.UtcNow;

    public string? RefType { get; set; }
    public Guid? RefId { get; set; }
    public string? Description { get; set; }

    /// <summary>İşlemi yapan kullanıcı (JWT'den).</summary>
    public Guid? UserId { get; set; }
    /// <summary>İşlem anındaki kullanıcı görünen adı (Ad Soyad ya da kullanıcı adı).</summary>
    public string? KullaniciAdi { get; set; }

    public Guid? BatchId { get; set; }
    public bool IsReversed { get; set; }
    public DateTime? ReversedAt { get; set; }

    public CashAccount CashAccount { get; set; } = null!;
}

