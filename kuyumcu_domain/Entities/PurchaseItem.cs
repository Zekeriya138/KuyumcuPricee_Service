using System;

namespace kuyumcu_domain.Entities
{
    public class PurchaseItem : Entity
    {
        public Guid TenantId { get; set; }
        public Guid PurchaseId { get; set; }
        public int LineNo { get; set; }

        public ItemKind Kind { get; set; } = ItemKind.Unknown;
        public string ProductCode { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string Karat { get; set; } = "";
        public string? Category { get; set; }

        public decimal Quantity { get; set; }         // gram/adet
        public decimal UnitCost { get; set; }         // TL
        public decimal Discount { get; set; }
        public decimal TaxRate { get; set; }
        public decimal LineTotal { get; set; }

        public Purchase Purchase { get; set; } = null!;
    }
}
