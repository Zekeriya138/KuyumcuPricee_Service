using kuyumcu_domain.Enums;
using System;

namespace kuyumcu_domain.Entities
{
    public class StockMovement : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid ProductId { get; set; }
        public Guid BranchId { get; set; }

        // Tekil parça takibi için (opsiyonel)
        public Guid? ProductItemId { get; set; }

        public DateTime Date { get; set; } = DateTime.UtcNow;

        public MovementDirection Direction { get; set; }  // In / Out
        public ItemKind Kind { get; set; } = ItemKind.Unknown;
        public StockRefKind RefKind { get; set; }

        public string ProductCode { get; set; } = "";
        public string Karat { get; set; } = "";
        public string? Category { get; set; }

        public decimal Quantity { get; set; }      // gram/adet
        public string? Reason { get; set; }

        // Satış / Satınalma / Manuel gibi referans bilgileri
        public string RefType { get; set; } = "";   // "Sale" | "Purchase" | "Manual"
        public Guid? RefId { get; set; }
        public string? Note { get; set; }

        // (Önceki yapıyı koruyoruz)
        public Guid? SaleItemId { get; set; }
        public Guid? PurchaseItemId { get; set; }

        public decimal InQty { get; set; }          // giriş (+)
        public decimal OutQty { get; set; }         // çıkış (-)
        public decimal BeforeQty { get; set; }      // hareketten önce
        public decimal AfterQty { get; set; }       // hareketten sonra

        // Navigations
        public SaleItem? SaleItem { get; set; }
        public PurchaseItem? PurchaseItem { get; set; }
        public ProductItem? ProductItem { get; set; }   // <-- EKLENDİ

        public Branch Branch { get; set; } = null!;
        public Product Product { get; set; } = null!;
    }
}
