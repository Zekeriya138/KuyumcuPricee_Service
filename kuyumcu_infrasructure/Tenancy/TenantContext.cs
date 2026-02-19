using System;

namespace kuyumcu_infrastructure.Tenancy
{
    /// <summary>
    /// İstek süresince geçerli kiracı (tenant) ve opsiyonel şube bilgisini taşır.
    /// Middleware tarafından doldurulur; servisler/controlller'lar buradan okur.
    /// </summary>
    public interface ITenantContext
    {
        Guid TenantId { get; set; }
        Guid? BranchId { get; set; }
    }

    /// <summary>
    /// Scoped (per-request) tenant konteksi.
    /// </summary>
    public sealed class TenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = Guid.Empty;
        public Guid? BranchId { get; set; } = null;
    }
}
