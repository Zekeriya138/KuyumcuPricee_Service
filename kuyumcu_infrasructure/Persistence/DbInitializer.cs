using kuyumcu_infrastructure.Persistence;
using kuyumcu_domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public static class DbInitializer
{
    public static async Task EnsureSeedAsync(AppDbContext db, IConfiguration cfg, CancellationToken ct = default)
    {
        // 1) DB şemasını uygula
        await db.Database.MigrateAsync(ct);

        // 2) appsettings’ten seed’te kullanılacak TenantId
        var tenantIdStr = cfg["Tenancy:DefaultTenantId"]; // ör: "11111111-1111-1111-1111-111111111111"
        if (!Guid.TryParse(tenantIdStr, out var tenantId))
            throw new InvalidOperationException("Tenancy:DefaultTenantId appsettings.json içinde tanımlı ve geçerli bir GUID olmalıdır.");

        // 3) Tenant var mı? (global query filter'i atla!)
        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);

        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id = tenantId,
                Name = "Default Tenant",
                CreatedAt = DateTime.UtcNow
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
        }

        // 4) Bu tenant için şube var mı? (filter'i atla, tenant’a göre kontrol et)
        var branchExists = await db.Branches
            .IgnoreQueryFilters()
            .AnyAsync(b => b.TenantId == tenantId, ct);

        if (!branchExists)
        {
            var branch = new Branch
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = "Merkez",
                City = "İstanbul",
                CreatedAt = DateTime.UtcNow
            };
            db.Branches.Add(branch);

            // 5) Admin kullanıcı yoksa ekle (yine aynı tenant’a bağlı!)
            var hasAdmin = await db.Users
                .IgnoreQueryFilters()
                .AnyAsync(u => u.TenantId == tenantId && u.Username == "admin", ct);

            if (!hasAdmin)
            {
                db.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    BranchId = branch.Id,
                    Username = "admin",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("Zeki_1234"),
                    Role = "Owner",
                    NationalId = "10000000000",
                    BirthDate = new DateTime(1990, 1, 1),
                    CreatedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
        }

        // Hepsi bu: tenant varsa - branch varsa tekrar eklemeyiz.
    }
}




//public static class DbInitializer
//{
//    public static async Task EnsureSeedAsync(AppDbContext db)
//    {
//        await db.Database.MigrateAsync();

//        if (!await db.Branches.AnyAsync())
//        {
//            var b = new Branch { Name = "Merkez", City = "İstanbul" };
//            db.Branches.Add(b);

//            db.Users.Add(new User
//            {
//                Username = "admin",
//                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Zeki_1234"),
//                Role = "Owner",
//                Branch = b,
//                NationalId = "10000000000",
//                BirthDate = new DateTime(1990, 1, 1)
//            });

//            await db.SaveChangesAsync();
//        }
//    }
//}
