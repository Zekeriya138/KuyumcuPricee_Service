using System;
using System.Collections.Generic;
using kuyumcu_domain.Enums;

namespace kuyumcu_domain.Entities
{
    public class Purchase : Entity
    {
        public Guid TenantId { get; set; }
        public Guid BranchId { get; set; }
        public Guid UserId { get; set; }
        public Guid? CustomerId { get; set; }   // müşteriden hurda alışı için
        public Guid? SupplierId { get; set; }    // toptancıdan alış için tedarikçi

        /// <summary>1=Müşteri (hurda/ziynet), 2=Toptancı (yeni ürün)</summary>
        public PurchaseType PurchaseType { get; set; } = PurchaseType.Musteri;
        /// <summary>Nakit, Emanet (cari), Veresiye (borç)</summary>
        public PurchasePaymentMethod PaymentMethod { get; set; } = PurchasePaymentMethod.Nakit;
        /// <summary>Alınan ürünlerin toplam has altın karşılığı (gram)</summary>
        public decimal? TotalHas { get; set; }

        public DateTime Date { get; set; } = DateTime.UtcNow;
        public string? DocumentNo { get; set; }
        public string? PartnerName { get; set; } // tedarikçi adı (SupplierId yoksa)
        public string? Note { get; set; }

        public decimal Subtotal { get; set; }
        public decimal DiscountTotal { get; set; }
        public decimal TaxTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public decimal TotalAmount { get; set; } // kalemlerin toplam
        public User User { get; set; } = null!;
        public Branch Branch { get; set; } = null!;
        public Customer? Customer { get; set; }
        public Supplier? Supplier { get; set; }

        public ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();
        public ICollection<PurchasePayment> Payments { get; set; } = new List<PurchasePayment>();
    }
}
