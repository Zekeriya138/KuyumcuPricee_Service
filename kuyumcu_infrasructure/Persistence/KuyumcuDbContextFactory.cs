using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using kuyumcu_infrastructure.Tenancy;

namespace kuyumcu_infrastructure.Persistence
{
    // ITenantContext arayüzünü taklit eden basit sınıf
    public class DesignTimeTenantContext : ITenantContext
    {
        // Migration sırasında Guid.Empty döner
        public Guid TenantId { get; set; }= Guid.Empty;

        public Guid? BranchId { get; set; } = null;
    }

    // EF Core'un tasarım zamanında (migration oluştururken) DbContext'i oluşturması için fabrika
    public class KuyumcuDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext(string[] args)
        {
            // Bu Connection String'i kendi veritabanı ayarlarınızla güncelleyin!
            const string connectionString = "server = .;database = KuyumcuDb8;integrated security = true; Encrypt=False;";

            var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
            // Migration sınıfları bu projede (kuyumcu_infrasructure); başlangıç projesi API olsa da aynı assembly kullanılmalı.
            optionsBuilder.UseSqlServer(connectionString, b => b.MigrationsAssembly("kuyumcu_infrasructure"));

            // Tenant context'i sağlamak için mock (sahte) sınıf kullanıldı
            var dummyTenantContext = new DesignTimeTenantContext();

            // Sadece tek kalan yapıcıyı kullanır
            return new AppDbContext(optionsBuilder.Options, dummyTenantContext);
        }
    }
}