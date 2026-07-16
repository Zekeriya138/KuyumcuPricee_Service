namespace kuyumcu_domain.Entities;

/// <summary>Şube bazlı kullanıcı notu.</summary>
public class BranchNote : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }

    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>CUSTOMER, SUPPLIER veya null (genel şube notu).</summary>
    public string? OwnerType { get; set; }
    /// <summary>Müşteri veya tedarikçi kimliği.</summary>
    public Guid? OwnerId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
