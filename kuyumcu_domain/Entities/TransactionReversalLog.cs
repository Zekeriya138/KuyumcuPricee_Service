namespace kuyumcu_domain.Entities;

/// <summary>Geri alınan işlem audit kaydı (müşteri/tedarikçi).</summary>
public class TransactionReversalLog : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }

    /// <summary>Customer veya Supplier</summary>
    public string PartyType { get; set; } = "";
    public Guid PartyId { get; set; }

    public Guid BatchId { get; set; }
    public string OperationType { get; set; } = "";

    public DateTime OriginalTxDate { get; set; }
    public string? OriginalPerformedBy { get; set; }

    public Guid? ReversedByUserId { get; set; }
    public string? ReversedByUserName { get; set; }
    public string Reason { get; set; } = "";
    public DateTime ReversedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Detay ekranı / Geri Alınan İşlemler sekmesi için JSON snapshot.</summary>
    public string SnapshotJson { get; set; } = "";

    public string DisplayGrup { get; set; } = "";
    public string DisplayKalem { get; set; } = "";
    public string DisplayDeger { get; set; } = "";
    public string DisplayCariDurum { get; set; } = "";
    public string DisplayAciklama { get; set; } = "";
}
