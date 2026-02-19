// kuyumcu_infrastructure/Persistence/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Tenancy;

namespace kuyumcu_infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        private readonly ITenantContext _tenant;
        public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
            : base(options) => _tenant = tenant;
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // --- DbSets ---
        public DbSet<User> Users => Set<User>();
        public DbSet<Branch> Branches => Set<Branch>();
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Sale> Sales => Set<Sale>();
        public DbSet<SaleItem> SaleItems => Set<SaleItem>();
        public DbSet<Purchase> Purchases => Set<Purchase>();
        public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
        public DbSet<Stock> Stocks => Set<Stock>();
        public DbSet<StockMovement> StockMovements => Set<StockMovement>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<ProductItem> ProductItems => Set<ProductItem>();
        public DbSet<Tenant> Tenants => Set<Tenant>();
        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            // --- Tenant ---
            b.Entity<Tenant>(e =>
            {
                e.ToTable("Tenants");
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            });
            // ===== ProductItem =====
            b.Entity<ProductItem>(e =>
            {
                e.ToTable("ProductItems");
                e.HasKey(x => x.Id);

                e.Property(x => x.Serial).HasMaxLength(64);
                e.Property(x => x.Barcode).HasMaxLength(64);
                e.Property(x => x.Karat).HasMaxLength(16);
                e.Property(x => x.Weight).HasColumnType("decimal(18,3)");

                // Sık aranan kolonlara index
                e.HasIndex(x => x.Barcode);
                e.HasIndex(x => x.Serial);
                e.HasIndex(x => new { x.ProductId, x.BranchId });

                // İlişkiler
                e.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Cascade); // ürün silinirse tekiller de silinsin (tercih)

                e.HasOne(x => x.Branch)
                    .WithMany()
                    .HasForeignKey(x => x.BranchId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.Barcode }).IsUnique();
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);

                // Soft delete varsa saygı göster
                var softProp = typeof(ProductItem).GetProperty("IsDeleted");
                if (softProp != null)
                    e.HasQueryFilter(x => EF.Property<bool>(x, "IsDeleted") == false);
            });
            // ===== Users =====
            b.Entity<User>(e =>
            {
                e.ToTable("Users");
                e.HasKey(x => x.Id);

                e.Property(x => x.Username).HasMaxLength(64).IsRequired();
                e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
                e.Property(x => x.Role).HasMaxLength(32);

                e.HasOne(x => x.Branch)
                    .WithMany(x => x.Users)
                    .HasForeignKey(x => x.BranchId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== Branches =====
            b.Entity<Branch>(e =>
            {
                e.ToTable("Branches");
                e.HasKey(x => x.Id);

                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
                e.Property(x => x.Code).HasMaxLength(32);
                e.Property(x => x.City).HasMaxLength(64);

                e.Property(x => x.Address).HasMaxLength(256);   // <<< eklendi
                e.Property(x => x.Phone).HasMaxLength(32);     // <<< eklendi
                e.Property(x => x.Email).HasMaxLength(128);

                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.HasOne(x => x.Tenant).WithMany(t => t.Branches)
           .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

                // İsteğe bağlı: Tenant içinde şube adı unique
                e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();

                // Filtre
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
                // (İstersen) Kod için benzersiz index:
                // e.HasIndex(x => x.Code).IsUnique().HasFilter("[Code] IS NOT NULL");
            });


            // ===== Customers =====
            b.Entity<Customer>(e =>
            {
                e.ToTable("Customers");
                e.HasKey(x => x.Id);

                e.Property(x => x.FullName).HasMaxLength(150).IsRequired();
                e.Property(x => x.NationalId).HasMaxLength(20);
                e.Property(x => x.Phone).HasMaxLength(32);
                e.Property(x => x.Email).HasMaxLength(150);

                e.HasQueryFilter(x => !x.IsDeleted);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== Sales =====
            b.Entity<Sale>(e =>
            {
                e.ToTable("Sales");
                e.HasKey(x => x.Id);

                // Aşağıdaki alanlar Sale entity'sinden kaldırıldığı için yoruma alındı/silindi:
                // e.Property(x => x.ProductCode).HasMaxLength(64);
                // e.Property(x => x.ProductName).HasMaxLength(256);
                // e.Property(x => x.Karat).HasMaxLength(16);
                // e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
                // e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
                // e.Property(x => x.TotalPrice).HasColumnType("decimal(18,2)");

                e.HasQueryFilter(x => !x.IsDeleted);

                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

                e.HasMany(x => x.Items)
                    .WithOne(x => x.Sale)
                    .HasForeignKey(x => x.SaleId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== SaleItem =====
            b.Entity<SaleItem>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.SaleId, x.LineNo }).IsUnique();

                e.Property(x => x.ProductCode).HasMaxLength(64);
                e.Property(x => x.ProductName).HasMaxLength(256);
                e.Property(x => x.Karat).HasMaxLength(16);
                e.Property(x => x.Category).HasMaxLength(64);

                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
                e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
                e.Property(x => x.Discount).HasColumnType("decimal(18,2)");
                e.Property(x => x.TaxRate).HasColumnType("decimal(9,4)");
                e.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");

                // YENİ: ProductItem bağlantısı
                e.HasOne(x => x.ProductItem)
                    .WithMany()
                    .HasForeignKey(x => x.ProductItemId)
                    .OnDelete(DeleteBehavior.SetNull); // Ürün parçası silinse bile satış kaydı dursun.

                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== Purchase =====
            b.Entity<Purchase>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
                e.Property(x => x.Subtotal).HasColumnType("decimal(18,2)");
                e.Property(x => x.DiscountTotal).HasColumnType("decimal(18,2)");
                e.Property(x => x.TaxTotal).HasColumnType("decimal(18,2)");
                e.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
                e.Property(x => x.Note).HasMaxLength(500);

                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== PurchaseItem =====
            b.Entity<PurchaseItem>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.PurchaseId, x.LineNo }).IsUnique();

                e.Property(x => x.ProductCode).HasMaxLength(64);
                e.Property(x => x.ProductName).HasMaxLength(256);
                e.Property(x => x.Karat).HasMaxLength(16);
                e.Property(x => x.Category).HasMaxLength(64);

                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
                e.Property(x => x.UnitCost).HasColumnType("decimal(18,2)");
                e.Property(x => x.Discount).HasColumnType("decimal(18,2)");
                e.Property(x => x.TaxRate).HasColumnType("decimal(9,4)");
                e.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");

                e.HasOne(x => x.Purchase)
                    .WithMany(x => x.Items)
                    .HasForeignKey(x => x.PurchaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== Stock =====
            b.Entity<Stock>(e =>
            {
                e.ToTable("Stocks");
                e.HasKey(x => x.Id);

                // Bu index kaldırıldı. Benzersizlik alttaki composite index ile sağlanıyor.
                // e.HasIndex(x => x.ProductId).IsUnique(); 
                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");

                e.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Ürün bazlı stok kaydı (Tenant + Branch + Product) ile benzersiz olmalıdır.
                e.HasIndex(s => new { s.TenantId, s.BranchId, s.ProductId })
                    .IsUnique()
                    .HasDatabaseName("IX_Stocks_Tenant_Branch_Product");

                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== StockMovement =====
            b.Entity<StockMovement>(e =>
            {
                e.ToTable("StockMovements");

                e.HasIndex(x => new { x.Date, x.BranchId });

                e.Property(x => x.ProductCode).HasMaxLength(64);
                e.Property(x => x.Karat).HasMaxLength(16);
                e.Property(x => x.Category).HasMaxLength(64);
                e.Property(x => x.Reason).HasMaxLength(256);
                e.Property(x => x.RefType).HasMaxLength(40);
                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");

                // ZORUNLU FKLAR
                e.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.Branch)
                    .WithMany()
                    .HasForeignKey(x => x.BranchId)
                    .OnDelete(DeleteBehavior.Restrict);

                // OPSIYONEL FKLAR (SetNull)
                e.HasOne(x => x.SaleItem)
                    .WithMany()
                    .HasForeignKey(x => x.SaleItemId)
                    .OnDelete(DeleteBehavior.SetNull);

                e.HasOne(x => x.PurchaseItem)
                    .WithMany()
                    .HasForeignKey(x => x.PurchaseItemId)
                    .OnDelete(DeleteBehavior.SetNull);

                // YENI: ProductItem — TEK VE AÇIK TANIM
                e.HasOne(x => x.ProductItem)
                    .WithMany()
                    .HasForeignKey(x => x.ProductItemId)
                    .OnDelete(DeleteBehavior.SetNull);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.ProductId, x.CreatedAt });
            });


            // ===== Product =====
            b.Entity<Product>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.ProductCode).HasMaxLength(64).IsRequired();
                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
                e.Property(x => x.Category).HasMaxLength(64);
                e.Property(x => x.Karat).HasMaxLength(16);
                e.Property(x => x.Barcode).HasMaxLength(64);

                e.HasIndex(x => x.ProductCode).IsUnique();
                e.HasIndex(x => x.Barcode);
                e.HasIndex(x => new { x.TenantId, x.ProductCode }).IsUnique();
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });
        }
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            var entries = ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added && e.Entity is ITenantScoped);

            foreach (var e in entries)
                ((ITenantScoped)e.Entity).TenantId = _tenant.TenantId;

            return base.SaveChangesAsync(ct);
        }
    }
}

//// kuyumcu_infrastructure/Persistence/AppDbContext.cs
//using Microsoft.EntityFrameworkCore;
//using kuyumcu_domain.Entities;
//using kuyumcu_infrastructure.Tenancy;

//namespace kuyumcu_infrastructure.Persistence
//{
//    public class AppDbContext : DbContext
//    {
//        private readonly ITenantContext _tenant;
//        public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
//            : base(options) => _tenant = tenant;
//        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

//        // --- DbSets ---
//        public DbSet<User> Users => Set<User>();
//        public DbSet<Branch> Branches => Set<Branch>();
//        public DbSet<Customer> Customers => Set<Customer>();
//        public DbSet<Sale> Sales => Set<Sale>();
//        public DbSet<SaleItem> SaleItems => Set<SaleItem>();
//        public DbSet<Purchase> Purchases => Set<Purchase>();
//        public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
//        public DbSet<Stock> Stocks => Set<Stock>();
//        public DbSet<StockMovement> StockMovements => Set<StockMovement>();
//        public DbSet<Product> Products => Set<Product>();
//        public DbSet<ProductItem> ProductItems => Set<ProductItem>();
//        public DbSet<Tenant> Tenants => Set<Tenant>();
//        protected override void OnModelCreating(ModelBuilder b)
//        {
//            base.OnModelCreating(b);
//            // --- Tenant ---
//            b.Entity<Tenant>(e =>
//            {
//                e.ToTable("Tenants");
//                e.HasKey(x => x.Id);
//                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
//            });
//            // ===== ProductItem =====
//            b.Entity<ProductItem>(e =>
//            {
//                e.ToTable("ProductItems");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.Serial).HasMaxLength(64);
//                e.Property(x => x.Barcode).HasMaxLength(64);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Weight).HasColumnType("decimal(18,3)");

//                // Sık aranan kolonlara index
//                e.HasIndex(x => x.Barcode);
//                e.HasIndex(x => x.Serial);
//                e.HasIndex(x => new { x.ProductId, x.BranchId });

//                // İlişkiler
//                e.HasOne(x => x.Product)
//                    .WithMany()
//                    .HasForeignKey(x => x.ProductId)
//                    .OnDelete(DeleteBehavior.Cascade); // ürün silinirse tekiller de silinsin (tercih)

//                e.HasOne(x => x.Branch)
//                    .WithMany()
//                    .HasForeignKey(x => x.BranchId)
//                    .OnDelete(DeleteBehavior.Restrict);
//                e.HasIndex(x => new { x.TenantId, x.Barcode }).IsUnique();
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);

//                // Soft delete varsa saygı göster
//                var softProp = typeof(ProductItem).GetProperty("IsDeleted");
//                if (softProp != null)
//                    e.HasQueryFilter(x => EF.Property<bool>(x, "IsDeleted") == false);
//            });
//            // ===== Users =====
//            b.Entity<User>(e =>
//            {
//                e.ToTable("Users");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.Username).HasMaxLength(64).IsRequired();
//                e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
//                e.Property(x => x.Role).HasMaxLength(32);

//                e.HasOne(x => x.Branch)
//                    .WithMany(x => x.Users)
//                    .HasForeignKey(x => x.BranchId)
//                    .OnDelete(DeleteBehavior.Restrict);
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//            });

//            // ===== Branches =====
//            b.Entity<Branch>(e =>
//            {
//                e.ToTable("Branches");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
//                e.Property(x => x.Code).HasMaxLength(32);
//                e.Property(x => x.City).HasMaxLength(64);

//                e.Property(x => x.Address).HasMaxLength(256);   // <<< eklendi
//                e.Property(x => x.Phone).HasMaxLength(32);     // <<< eklendi
//                e.Property(x => x.Email).HasMaxLength(128);

//                e.Property(x => x.IsActive).HasDefaultValue(true);
//                e.HasOne(x => x.Tenant).WithMany(t => t.Branches)
//           .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

//                // İsteğe bağlı: Tenant içinde şube adı unique
//                e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();

//                // Filtre
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//                // (İstersen) Kod için benzersiz index:
//                // e.HasIndex(x => x.Code).IsUnique().HasFilter("[Code] IS NOT NULL");
//            });


//            // ===== Customers =====
//            b.Entity<Customer>(e =>
//            {
//                e.ToTable("Customers");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.FullName).HasMaxLength(150).IsRequired();
//                e.Property(x => x.NationalId).HasMaxLength(20);
//                e.Property(x => x.Phone).HasMaxLength(32);
//                e.Property(x => x.Email).HasMaxLength(150);

//                e.HasQueryFilter(x => !x.IsDeleted);
//                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
//            });

//            // ===== Sales =====
//            b.Entity<Sale>(e =>
//            {
//                e.ToTable("Sales");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.ProductName).HasMaxLength(256);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
//                e.Property(x => x.TotalPrice).HasColumnType("decimal(18,2)");

//                e.HasQueryFilter(x => !x.IsDeleted);

//                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
//                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
//                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

//                e.HasMany(x => x.Items)
//                    .WithOne(x => x.Sale)
//                    .HasForeignKey(x => x.SaleId)
//                    .OnDelete(DeleteBehavior.Restrict);
//                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
//            });

//            // ===== SaleItem =====
//            b.Entity<SaleItem>(e =>
//            {
//                e.HasKey(x => x.Id);
//                e.HasIndex(x => new { x.SaleId, x.LineNo }).IsUnique();

//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.ProductName).HasMaxLength(256);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Category).HasMaxLength(64);

//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Discount).HasColumnType("decimal(18,2)");
//                e.Property(x => x.TaxRate).HasColumnType("decimal(9,4)");
//                e.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//            });

//            // ===== Purchase =====
//            b.Entity<Purchase>(e =>
//            {
//                e.HasKey(x => x.Id);
//                e.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Subtotal).HasColumnType("decimal(18,2)");
//                e.Property(x => x.DiscountTotal).HasColumnType("decimal(18,2)");
//                e.Property(x => x.TaxTotal).HasColumnType("decimal(18,2)");
//                e.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Note).HasMaxLength(500);

//                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
//                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
//                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//            });

//            // ===== PurchaseItem =====
//            b.Entity<PurchaseItem>(e =>
//            {
//                e.HasKey(x => x.Id);
//                e.HasIndex(x => new { x.PurchaseId, x.LineNo }).IsUnique();

//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.ProductName).HasMaxLength(256);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Category).HasMaxLength(64);

//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.Property(x => x.UnitCost).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Discount).HasColumnType("decimal(18,2)");
//                e.Property(x => x.TaxRate).HasColumnType("decimal(9,4)");
//                e.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");

//                e.HasOne(x => x.Purchase)
//                    .WithMany(x => x.Items)
//                    .HasForeignKey(x => x.PurchaseId)
//                    .OnDelete(DeleteBehavior.Cascade);
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//            });

//            // ===== Stock =====
//            b.Entity<Stock>(e =>
//            {
//                e.ToTable("Stocks");
//                e.HasKey(x => x.Id);

//                e.HasIndex(x => x.ProductId).IsUnique();
//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");

//                e.HasOne(x => x.Product)
//                    .WithMany()
//                    .HasForeignKey(x => x.ProductId)
//                    .OnDelete(DeleteBehavior.Cascade);
//                e.HasIndex(s => new { s.TenantId, s.BranchId, s.ProductId })
//         .IsUnique()
//         .HasDatabaseName("IX_Stocks_Tenant_Branch_Product");
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//            });

//            // ===== StockMovement =====
//            b.Entity<StockMovement>(e =>
//            {
//                e.ToTable("StockMovements");

//                e.HasIndex(x => new { x.Date, x.BranchId });

//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Category).HasMaxLength(64);
//                e.Property(x => x.Reason).HasMaxLength(256);
//                e.Property(x => x.RefType).HasMaxLength(40);
//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");

//                // ZORUNLU FKLAR
//                e.HasOne(x => x.Product)
//                    .WithMany()
//                    .HasForeignKey(x => x.ProductId)
//                    .OnDelete(DeleteBehavior.Restrict);

//                e.HasOne(x => x.Branch)
//                    .WithMany()
//                    .HasForeignKey(x => x.BranchId)
//                    .OnDelete(DeleteBehavior.Restrict);

//                // OPSIYONEL FKLAR (SetNull)
//                e.HasOne(x => x.SaleItem)
//                    .WithMany()
//                    .HasForeignKey(x => x.SaleItemId)
//                    .OnDelete(DeleteBehavior.SetNull);

//                e.HasOne(x => x.PurchaseItem)
//                    .WithMany()
//                    .HasForeignKey(x => x.PurchaseItemId)
//                    .OnDelete(DeleteBehavior.SetNull);

//                // YENI: ProductItem — TEK VE AÇIK TANIM
//                e.HasOne(x => x.ProductItem)
//                    .WithMany()
//                    .HasForeignKey(x => x.ProductItemId)
//                    .OnDelete(DeleteBehavior.SetNull);
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//                e.HasIndex(x => new { x.TenantId, x.BranchId, x.ProductId, x.CreatedAt });
//            });


//            // ===== Product =====
//            b.Entity<Product>(e =>
//            {
//                e.HasKey(x => x.Id);
//                e.Property(x => x.ProductCode).HasMaxLength(64).IsRequired();
//                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
//                e.Property(x => x.Category).HasMaxLength(64);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Barcode).HasMaxLength(64);

//                e.HasIndex(x => x.ProductCode).IsUnique();
//                e.HasIndex(x => x.Barcode);
//                e.HasIndex(x => new { x.TenantId, x.ProductCode }).IsUnique();
//                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
//            });
//        }
//        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
//        {
//            var entries = ChangeTracker.Entries()
//                .Where(e => e.State == EntityState.Added && e.Entity is ITenantScoped);

//            foreach (var e in entries)
//                ((ITenantScoped)e.Entity).TenantId = _tenant.TenantId;

//            return base.SaveChangesAsync(ct);
//        }
//    }
//}





//// kuyumcu_infrastructure/Persistence/AppDbContext.cs
//using Microsoft.EntityFrameworkCore;
//using kuyumcu_domain.Entities;

//namespace kuyumcu_infrastructure.Persistence
//{
//    public class AppDbContext : DbContext
//    {
//        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

//        // --- DbSets ---
//        public DbSet<User> Users => Set<User>();
//        public DbSet<Branch> Branches => Set<Branch>();
//        public DbSet<Customer> Customers => Set<Customer>();
//        public DbSet<Sale> Sales => Set<Sale>();

//        public DbSet<SaleItem> SaleItems => Set<SaleItem>();
//        public DbSet<Purchase> Purchases => Set<Purchase>();
//        public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
//        public DbSet<StockMovement> StockMovements => Set<StockMovement>();
//        public DbSet<Product> Products => Set<Product>();
//        public DbSet<Stock> Stocks => Set<Stock>();
//        protected override void OnModelCreating(ModelBuilder b)
//        {
//            base.OnModelCreating(b);

//            // ===== Users =====
//            b.Entity<User>(e =>
//            {
//                e.ToTable("Users");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.Username)
//                    .HasMaxLength(64)
//                    .IsRequired();

//                e.Property(x => x.PasswordHash)
//                    .HasMaxLength(256)
//                    .IsRequired();

//                e.Property(x => x.Role)
//                    .HasMaxLength(32);

//                e.HasOne(x => x.Branch)
//                .WithMany(x => x.Users)
//                .HasForeignKey(x => x.BranchId)
//                .OnDelete(DeleteBehavior.Restrict);

//                // İsteğe bağlı diğer kolon uzunlukları vs. burada kalabilir
//            });

//            // ===== Branches =====
//            b.Entity<Branch>(e =>
//            {
//                e.ToTable("Branches");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.Name).HasMaxLength(128);
//                e.Property(x => x.City).HasMaxLength(64);

//            });

//            // ===== Customers =====
//            b.Entity<Customer>(e =>
//            {
//                e.ToTable("Customers");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.FullName)
//                    .HasMaxLength(150)
//                    .IsRequired();

//                e.Property(x => x.NationalId).HasMaxLength(20);
//                e.Property(x => x.Phone).HasMaxLength(32);
//                e.Property(x => x.Email).HasMaxLength(150);

//                // Soft delete (Entity'de IsDeleted varsa)
//                e.HasQueryFilter(x => !x.IsDeleted);
//            });

//            // ===== Sales =====
//            b.Entity<Sale>(e =>
//            {


//                e.ToTable("Sales");
//                e.HasKey(x => x.Id);

//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.ProductName).HasMaxLength(256);
//                e.Property(x => x.Karat).HasMaxLength(16);



//                // Soft delete
//                e.HasQueryFilter(x => !x.IsDeleted);

//                // İlişkiler — cascade kapalı (Restrict), “multiple cascade paths” olmasın
//                e.HasOne(x => x.User)
//                    .WithMany()
//                    .HasForeignKey(x => x.UserId)
//                    .OnDelete(DeleteBehavior.Restrict);

//                e.HasOne(x => x.Branch)
//                    .WithMany()
//                    .HasForeignKey(x => x.BranchId)
//                    .OnDelete(DeleteBehavior.Restrict);

//                e.HasOne(x => x.Customer)
//                    .WithMany()
//                    .HasForeignKey(x => x.CustomerId)
//                    .OnDelete(DeleteBehavior.Restrict);


//                e.HasMany(x => x.Items)
//                    .WithOne(x => x.Sale)
//                    .HasForeignKey(x => x.SaleId)
//                    .OnDelete(DeleteBehavior.Restrict);
//            });
//            // ====== SaleItem ======
//            b.Entity<SaleItem>(e =>
//            {
//                e.HasIndex(x => new { x.SaleId, x.LineNo }).IsUnique();

//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.ProductName).HasMaxLength(256);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Category).HasMaxLength(64);

//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Discount).HasColumnType("decimal(18,2)");
//                e.Property(x => x.TaxRate).HasColumnType("decimal(9,4)");
//                e.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
//            });
//            // ====== PurchaseItem ======
//            b.Entity<PurchaseItem>(e =>
//            {
//                e.HasIndex(x => new { x.PurchaseId, x.LineNo }).IsUnique();
//                e.HasKey(x => x.Id);
//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.ProductName).HasMaxLength(256);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Category).HasMaxLength(64);

//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.Property(x => x.UnitCost).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Discount).HasColumnType("decimal(18,2)");
//                e.Property(x => x.TaxRate).HasColumnType("decimal(9,4)");
//                e.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
//                e.HasOne(x => x.Purchase)
//       .WithMany(x => x.Items)
//       .HasForeignKey(x => x.PurchaseId)
//       .OnDelete(DeleteBehavior.Cascade);
//            });
//            // ====== Purchase ======
//            b.Entity<Purchase>(e =>
//            {
//                e.HasKey(x => x.Id);
//                e.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
//                e.Property(x => x.Note).HasMaxLength(500);

//                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
//                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
//                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
//            });

//            // ====== StockMovement ======
//            b.Entity<StockMovement>(e =>
//            {
//                e.HasIndex(x => new { x.Date, x.BranchId });
//                e.Property(x => x.ProductCode).HasMaxLength(64);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Category).HasMaxLength(64);
//                e.Property(x => x.Reason).HasMaxLength(256);
//                e.Property(x => x.RefType).HasMaxLength(40);
//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
//                e.HasOne(x => x.Branch)
//                    .WithMany()
//                    .HasForeignKey(x => x.BranchId)
//                    .OnDelete(DeleteBehavior.Restrict);

//                e.HasOne(x => x.SaleItem)
//                    .WithMany()
//                    .HasForeignKey(x => x.SaleItemId)
//                    .OnDelete(DeleteBehavior.Restrict);

//                e.HasOne(x => x.PurchaseItem)
//                    .WithMany()
//                    .HasForeignKey(x => x.PurchaseItemId)
//                    .OnDelete(DeleteBehavior.Restrict);
//            });
//            b.Entity<Stock>(e =>
//            {
//                e.ToTable("Stocks");
//                e.HasIndex(x => x.ProductId).IsUnique();        // her ürün için tek satır
//                e.Property(x => x.Quantity).HasColumnType("decimal(18,3)");
//                e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
//            });
//            // YENİ: Product konfigürasyonu
//            b.Entity<Product>(e =>
//            {
//                e.Property(x => x.ProductCode).HasMaxLength(64).IsRequired();
//                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
//                e.Property(x => x.Category).HasMaxLength(64);
//                e.Property(x => x.Karat).HasMaxLength(16);
//                e.Property(x => x.Barcode).HasMaxLength(64);

//                e.HasIndex(x => x.ProductCode).IsUnique();
//                e.HasIndex(x => x.Barcode);
//            });
//        }
//    }
//}
