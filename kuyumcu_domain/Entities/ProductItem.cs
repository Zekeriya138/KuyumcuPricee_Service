using kuyumcu_domain.Entities;
namespace kuyumcu_domain.Entities
{
    public class ProductItem : Entity, ITenantScoped
    {
        public Guid TenantId { get; set; }
        public Guid ProductId { get; set; }
        public Guid BranchId { get; set; }
        public string Serial { get; set; } = "";
        public string Barcode { get; set; } = "";
        public string Karat { get; set; } = "";
        public decimal Weight { get; set; } = 0m;
        public bool IsInStock { get; set; } = true;
        public decimal Cost { get; set; } = 0m;
        /// <summary>Müşteri hurda alış satırından vitrine barkodlanan parça (hurda listesi takibi).</summary>
        public Guid? SourcePurchaseItemId { get; set; }
        public DateTime UpdatedAt { get; set; }
        public Product Product { get; set; } = null!;
        public Branch Branch { get; set; } = null!;
    }

}
