namespace kuyumcu_domain.Entities;

/// <summary>Gün sonu özet snapshot.</summary>
public class DayEndReport : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public DateTime BusinessDate { get; set; }

    public decimal OpeningTl { get; set; }
    public decimal OpeningUsd { get; set; }
    public decimal OpeningEur { get; set; }
    public decimal OpeningHas { get; set; }

    public decimal ClosingTl { get; set; }
    public decimal ClosingUsd { get; set; }
    public decimal ClosingEur { get; set; }
    public decimal ClosingHas { get; set; }

    public decimal TotalIncomeTl { get; set; }
    public decimal TotalExpenseTl { get; set; }

    /// <summary>Draft / Closed</summary>
    public string Status { get; set; } = "Draft";
    public string? PdfPath { get; set; }
}

