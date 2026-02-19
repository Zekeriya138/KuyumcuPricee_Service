using System;
using System.Collections.Generic;

namespace kuyumcu_domain.Entities
{
    public class Purchase : Entity
    {
        public Guid TenantId { get; set; }
        public Guid BranchId { get; set; }
        public Guid UserId { get; set; }
        public Guid? CustomerId { get; set; }   // müşteriden hurda alışı için

        public DateTime Date { get; set; } = DateTime.UtcNow;
        public string? DocumentNo { get; set; }
        public string? PartnerName { get; set; } // tedarikçi adı (müşteri yoksa)
        public string? Note { get; set; }

        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal TotalAmount { get; set; } // kalemlerin toplam
        public User User { get; set; } = null!;
        public Branch Branch { get; set; } = null!;
        public Customer? Customer { get; set; }

        public ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();
    }
}
