using System;

namespace kuyumcu_domain.Entities;

/// <summary>
/// Stok/Depo gümüş sekmesinden yapılan manuel stok giriş lotları.
/// Alış (Purchase) veya kasa hareketi oluşturmaz; yalnızca gümüş envanter takibi içindir.
/// </summary>
public class DepoGumusLot : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "Gümüş Külçe";
    public decimal Gram { get; set; }
    public decimal UnitCostTl { get; set; }
    public string? Note { get; set; }
    public DateTime EntryDate { get; set; } = DateTime.UtcNow;

    public Branch Branch { get; set; } = null!;
}
