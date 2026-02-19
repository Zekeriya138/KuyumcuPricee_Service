using System;

namespace kuyumcu_domain.Entities
{
    public class Stock : Entity
    {
        public Guid BranchId { get; set; }
        public Guid TenantId { get; set; }
        public Guid ProductId { get; set; }
        public decimal Quantity { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public Product Product { get; set; } = null!;
    }
}
