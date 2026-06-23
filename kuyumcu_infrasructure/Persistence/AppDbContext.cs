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
        public DbSet<UserSalaryHistory> UserSalaryHistories => Set<UserSalaryHistory>();
        public DbSet<RateDisplaySetting> RateDisplaySettings => Set<RateDisplaySetting>();
        public DbSet<BranchSubscription> BranchSubscriptions => Set<BranchSubscription>();
        public DbSet<Branch> Branches => Set<Branch>();
        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<CustomerBalance> CustomerBalances => Set<CustomerBalance>();
        public DbSet<CustomerTransaction> CustomerTransactions => Set<CustomerTransaction>();
        public DbSet<Sale> Sales => Set<Sale>();
        public DbSet<SaleItem> SaleItems => Set<SaleItem>();
        public DbSet<SalePayment> SalePayments => Set<SalePayment>();
        public DbSet<Purchase> Purchases => Set<Purchase>();
        public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
        public DbSet<PurchasePayment> PurchasePayments => Set<PurchasePayment>();
        public DbSet<Stock> Stocks => Set<Stock>();
        public DbSet<StockMovement> StockMovements => Set<StockMovement>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<ProductItem> ProductItems => Set<ProductItem>();
        public DbSet<Tenant> Tenants => Set<Tenant>();
        public DbSet<Invoice> Invoices => Set<Invoice>();
        public DbSet<EInvoiceProfile> EInvoiceProfiles => Set<EInvoiceProfile>();
        public DbSet<EInvoiceDocument> EInvoiceDocuments => Set<EInvoiceDocument>();
        public DbSet<EInvoiceOutbox> EInvoiceOutboxes => Set<EInvoiceOutbox>();
        public DbSet<EInvoiceWebhookLog> EInvoiceWebhookLogs => Set<EInvoiceWebhookLog>();
        public DbSet<ExpenseSlipDocument> ExpenseSlipDocuments => Set<ExpenseSlipDocument>();
        public DbSet<ExpenseSlipAuditLog> ExpenseSlipAuditLogs => Set<ExpenseSlipAuditLog>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Supplier> Suppliers => Set<Supplier>();
        public DbSet<SupplierBalance> SupplierBalances => Set<SupplierBalance>();
        public DbSet<SupplierTransaction> SupplierTransactions => Set<SupplierTransaction>();
        public DbSet<DepoStok> DepoStoklar => Set<DepoStok>();
        public DbSet<DepoStokHavuz> DepoStokHavuzlar => Set<DepoStokHavuz>();
        public DbSet<ScrapStock> ScrapStocks => Set<ScrapStock>();
        public DbSet<ScrapLedger> ScrapLedgers => Set<ScrapLedger>();
        public DbSet<AyarAyar> AyarAyarlari => Set<AyarAyar>();
        public DbSet<CashAccount> CashAccounts => Set<CashAccount>();
        public DbSet<CashTransaction> CashTransactions => Set<CashTransaction>();
        public DbSet<DayEndReport> DayEndReports => Set<DayEndReport>();
        public DbSet<BranchNote> BranchNotes => Set<BranchNote>();
        public DbSet<BranchReminder> BranchReminders => Set<BranchReminder>();
        public DbSet<Account> Accounts => Set<Account>();
        public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
        public DbSet<JournalLine> JournalLines => Set<JournalLine>();
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
                e.HasIndex(x => x.SourcePurchaseItemId);

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

                // Tek bir global filtre: tenant izolasyonu + soft-delete.
                // Not: _tenant design-time'da null olabildiği için null-safe bırakıldı.
                var softProp = typeof(ProductItem).GetProperty("IsDeleted");
                if (softProp != null)
                {
                    e.HasQueryFilter(x =>
                        (_tenant == null || x.TenantId == _tenant.TenantId) &&
                        EF.Property<bool>(x, "IsDeleted") == false);
                }
                else
                {
                    e.HasQueryFilter(x => _tenant == null || x.TenantId == _tenant.TenantId);
                }
            });
            // ===== Users =====
            b.Entity<User>(e =>
            {
                e.ToTable("Users");
                e.HasKey(x => x.Id);

                e.Property(x => x.Username).HasMaxLength(64).IsRequired();
                e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
                e.Property(x => x.Role).HasMaxLength(32);
                e.Property(x => x.FullName).HasMaxLength(128);
                e.Property(x => x.Email).HasMaxLength(128);
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.Property(x => x.CanManageUsers).HasDefaultValue(false);
                e.Property(x => x.CanManageBranches).HasDefaultValue(false);
                e.Property(x => x.CanSwitchBranches).HasDefaultValue(false);
                e.Property(x => x.CanUseEInvoice).HasDefaultValue(false);
                e.Property(x => x.CanUseEArchive).HasDefaultValue(false);

                e.HasOne(x => x.Branch)
                    .WithMany(x => x.Users)
                    .HasForeignKey(x => x.BranchId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            b.Entity<UserSalaryHistory>(e =>
            {
                e.ToTable("UserSalaryHistories");
                e.HasKey(x => x.Id);
                e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
                e.Property(x => x.EffectiveFrom).HasColumnType("datetime2");
                e.Property(x => x.Note).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.UserId, x.EffectiveFrom, x.CreatedAt });
                e.HasOne(x => x.User)
                    .WithMany()
                    .HasForeignKey(x => x.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<RateDisplaySetting>(e =>
            {
                e.ToTable("RateDisplaySettings");
                e.HasKey(x => x.Id);
                e.Property(x => x.Code).HasMaxLength(64).IsRequired();
                e.Property(x => x.CustomDisplay).HasMaxLength(128);
                e.Property(x => x.BidTlOffset).HasColumnType("decimal(18,4)");
                e.Property(x => x.AskTlOffset).HasColumnType("decimal(18,4)");
                e.Property(x => x.TlOffset).HasColumnType("decimal(18,4)");
                e.Property(x => x.IsVisible).HasDefaultValue(true);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Code }).IsUnique();
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<BranchSubscription>(e =>
            {
                e.ToTable("BranchSubscriptions");
                e.HasKey(x => x.Id);
                e.Property(x => x.PeriodType).HasConversion<int>();
                e.Property(x => x.PackageType).HasConversion<int>();
                e.Property(x => x.Status).HasConversion<int>();
                e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
                e.Property(x => x.Price).HasColumnType("decimal(18,2)");
                e.Property(x => x.StartsAtUtc).HasColumnType("datetime2");
                e.Property(x => x.EndsAtUtc).HasColumnType("datetime2");
                e.Property(x => x.LastPaymentAtUtc).HasColumnType("datetime2");
                e.Property(x => x.LastCheckedAtUtc).HasColumnType("datetime2");
                e.Property(x => x.IyzipayConversationId).HasMaxLength(120);
                e.Property(x => x.IyzipayToken).HasMaxLength(120);
                e.Property(x => x.IyzipayPaymentId).HasMaxLength(120);
                e.Property(x => x.IyzipayStatus).HasMaxLength(64);
                e.Property(x => x.IyzipayRawResponse).HasColumnType("nvarchar(max)");
                e.Property(x => x.Note).HasMaxLength(500);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.CreatedAt });
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Status, x.EndsAtUtc });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
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

                e.Property(x => x.CariTip).HasDefaultValue(0); // 0=Müşteri, 1=Tedarikçi
                e.Property(x => x.FullName).HasMaxLength(150).IsRequired();
                e.Property(x => x.NationalId).HasMaxLength(20);
                e.Property(x => x.Phone).HasMaxLength(32);
                e.Property(x => x.Email).HasMaxLength(150);
                e.Property(x => x.Note).HasMaxLength(2000);
                e.Property(x => x.TedarikciExtJson).HasColumnType("nvarchar(max)");
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.CreatedAt });

                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<CustomerBalance>(e =>
            {
                e.ToTable("CustomerBalances");
                e.HasKey(x => x.Id);
                e.Property(x => x.BalanceTL).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceUSD).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceEUR).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceHAS).HasColumnType("decimal(18,4)");
                e.HasIndex(x => new { x.TenantId, x.CustomerId }).IsUnique();
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<CustomerTransaction>(e =>
            {
                e.ToTable("CustomerTransactions");
                e.HasKey(x => x.Id);
                e.Property(x => x.GroupCode).HasMaxLength(24).IsRequired();
                e.Property(x => x.ItemName).HasMaxLength(64).IsRequired();
                e.Property(x => x.ItemType).HasMaxLength(32);
                e.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
                e.Property(x => x.Gram).HasColumnType("decimal(18,4)");
                e.Property(x => x.Ayar).HasMaxLength(16);
                e.Property(x => x.Milyem).HasColumnType("decimal(9,4)");
                e.Property(x => x.HasEquivalent).HasColumnType("decimal(18,6)");
                e.Property(x => x.UnitPriceTl).HasColumnType("decimal(18,4)");
                e.Property(x => x.TotalPriceTl).HasColumnType("decimal(18,4)");
                e.Property(x => x.CariDurum).HasMaxLength(16);
                e.Property(x => x.RefType).HasMaxLength(24);
                e.Property(x => x.Note).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.CustomerId, x.GroupCode, x.ItemName, x.ItemType, x.CreatedAt });
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== ScrapStock (Müşteri hurdası — ProductItem’dan ayrı) =====
            b.Entity<ScrapStock>(e =>
            {
                e.ToTable("ScrapStocks");
                e.HasKey(x => x.Id);
                e.Property(x => x.Karat).HasMaxLength(16).IsRequired();
                e.Property(x => x.WeightGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.PureGoldGram).HasColumnType("decimal(18,4)");
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Karat }).IsUnique();
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<ScrapLedger>(e =>
            {
                e.ToTable("ScrapLedgers");
                e.HasKey(x => x.Id);
                e.Property(x => x.Karat).HasMaxLength(16).IsRequired();
                e.Property(x => x.DeltaWeightGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.DeltaPureGoldGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.GoldPricePerGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.AmountTl).HasColumnType("decimal(18,2)");
                e.Property(x => x.Note).HasMaxLength(500);
                e.Property(x => x.Kind).HasConversion<int>();
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== DepoStok (Hammadde Depo) =====
            b.Entity<DepoStok>(e =>
            {
                e.ToTable("DepoStoklar");
                e.HasKey(x => x.Id);
                e.Property(x => x.Ayar).HasMaxLength(16).IsRequired();
                e.Property(x => x.TotalGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.BarcodedGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.UnbarcodedGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.OrtalamaMaliyet).HasColumnType("decimal(18,2)");
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Ayar }).IsUnique();
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== DepoStokHavuz (mal + tedarikçi + birim + ayar) =====
            b.Entity<DepoStokHavuz>(e =>
            {
                e.ToTable("DepoStokHavuzlar");
                e.HasKey(x => x.Id);
                e.Property(x => x.Ayar).HasMaxLength(16).IsRequired();
                e.Property(x => x.MalTanimNorm).HasMaxLength(512).IsRequired();
                e.Property(x => x.TedarikciFirmaNorm).HasMaxLength(256).IsRequired();
                e.Property(x => x.BirimMaliyet).HasColumnType("decimal(18,4)");
                e.Property(x => x.TotalGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.BarcodedGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.UnbarcodedGram).HasColumnType("decimal(18,4)");
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Ayar, x.MalTanimNorm, x.TedarikciFirmaNorm, x.BirimMaliyet }).IsUnique();
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== AyarAyar (Ayar Ayarları) =====
            b.Entity<AyarAyar>(e =>
            {
                e.ToTable("AyarAyarlari");
                e.HasKey(x => x.Id);
                e.Property(x => x.Ayar).HasMaxLength(16).IsRequired();
                e.Property(x => x.Milyem).HasColumnType("decimal(9,3)");
                e.Property(x => x.Iscilik).HasColumnType("decimal(18,2)");
                e.Property(x => x.VarsayilanMaliyet).HasColumnType("decimal(18,2)");
                e.HasIndex(x => new { x.TenantId, x.Ayar }).IsUnique();
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
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

                e.Property(x => x.PaymentType).HasMaxLength(32);
                e.HasQueryFilter(x => !x.IsDeleted);

                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

                e.HasMany(x => x.Items)
                    .WithOne(x => x.Sale)
                    .HasForeignKey(x => x.SaleId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasMany(x => x.Payments)
                    .WithOne(x => x.Sale)
                    .HasForeignKey(x => x.SaleId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== Invoice (IBAN satışlarda fatura) =====
            b.Entity<Invoice>(e =>
            {
                e.ToTable("Invoices");
                e.HasKey(x => x.Id);
                e.Property(x => x.PaymentType).HasMaxLength(32);
                e.HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<EInvoiceProfile>(e =>
            {
                e.ToTable("EInvoiceProfiles");
                e.HasKey(x => x.Id);
                e.Property(x => x.ProviderCode).HasMaxLength(64).IsRequired();
                e.Property(x => x.CompanyName).HasMaxLength(200).IsRequired();
                e.Property(x => x.CompanyAddress).HasMaxLength(300).IsRequired();
                e.Property(x => x.TaxNumber).HasMaxLength(32).IsRequired();
                e.Property(x => x.TaxOffice).HasMaxLength(128).IsRequired();
                e.Property(x => x.SenderLabel).HasMaxLength(128);
                e.Property(x => x.IntegratorCompanyCode).HasColumnType("nvarchar(max)");
                e.Property(x => x.IntegratorUsername).HasMaxLength(128);
                e.Property(x => x.IntegratorSecretRef).HasMaxLength(256);
                e.Property(x => x.DefaultInvoicePrefix).HasMaxLength(16).IsRequired();
                e.Property(x => x.DefaultArchivePrefix).HasMaxLength(16).IsRequired();
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId }).IsUnique();
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<EInvoiceDocument>(e =>
            {
                e.ToTable("EInvoiceDocuments");
                e.HasKey(x => x.Id);
                e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
                e.Property(x => x.DocumentType).HasMaxLength(16).IsRequired();
                e.Property(x => x.Scenario).HasMaxLength(32).IsRequired();
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.InvoiceNumber).HasMaxLength(64).IsRequired();
                e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
                e.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
                e.Property(x => x.IntegratorDocumentId).HasMaxLength(128);
                e.Property(x => x.Uuid).HasMaxLength(64);
                e.Property(x => x.Ettn).HasMaxLength(64);
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.Property(x => x.RawLastResponse).HasColumnType("nvarchar(max)");
                e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(x => new { x.TenantId, x.InvoiceId }).IsUnique();
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Status, x.CreatedAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<EInvoiceOutbox>(e =>
            {
                e.ToTable("EInvoiceOutboxes");
                e.HasKey(x => x.Id);
                e.Property(x => x.Operation).HasMaxLength(16).IsRequired();
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.HasIndex(x => new { x.TenantId, x.Status, x.NextAttemptAt });
                e.HasIndex(x => new { x.TenantId, x.DocumentId, x.Operation, x.Status });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<EInvoiceWebhookLog>(e =>
            {
                e.ToTable("EInvoiceWebhookLogs");
                e.HasKey(x => x.Id);
                e.Property(x => x.ProviderCode).HasMaxLength(64).IsRequired();
                e.Property(x => x.Signature).HasMaxLength(256);
                e.Property(x => x.EventId).HasMaxLength(128);
                e.Property(x => x.EventType).HasMaxLength(128);
                e.Property(x => x.IntegratorDocumentId).HasMaxLength(128);
                e.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
                e.Property(x => x.ProcessError).HasMaxLength(1000);
                e.HasIndex(x => new { x.TenantId, x.ProviderCode, x.EventId }).IsUnique().HasFilter("[EventId] IS NOT NULL");
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.ReceivedAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<ExpenseSlipDocument>(e =>
            {
                e.ToTable("ExpenseSlipDocuments");
                e.HasKey(x => x.Id);
                e.Property(x => x.DocumentNo).HasMaxLength(64).IsRequired();
                e.Property(x => x.Status).HasMaxLength(32).IsRequired();
                e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
                e.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
                e.Property(x => x.BuyerName).HasMaxLength(256).IsRequired();
                e.Property(x => x.BuyerTaxNumber).HasMaxLength(32).IsRequired();
                e.Property(x => x.Description).HasMaxLength(512);
                e.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
                e.Property(x => x.RawLastResponse).HasColumnType("nvarchar(max)");
                e.Property(x => x.IntegratorDocumentId).HasMaxLength(128);
                e.Property(x => x.Uuid).HasMaxLength(64);
                e.Property(x => x.LastError).HasMaxLength(1000);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Status, x.CreatedAt });
                e.HasIndex(x => new { x.TenantId, x.DocumentNo }).IsUnique();
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<ExpenseSlipAuditLog>(e =>
            {
                e.ToTable("ExpenseSlipAuditLogs");
                e.HasKey(x => x.Id);
                e.Property(x => x.Action).HasMaxLength(64).IsRequired();
                e.Property(x => x.StatusBefore).HasMaxLength(32);
                e.Property(x => x.StatusAfter).HasMaxLength(32);
                e.Property(x => x.RequestJson).HasColumnType("nvarchar(max)");
                e.Property(x => x.ResponseRaw).HasColumnType("nvarchar(max)");
                e.Property(x => x.ErrorMessage).HasMaxLength(1000);
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.DocumentId, x.CreatedAt });
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

            // ===== SalePayment =====
            b.Entity<SalePayment>(e =>
            {
                e.ToTable("SalePayments");
                e.HasKey(x => x.Id);
                e.Property(x => x.Method).HasMaxLength(32).IsRequired();
                e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
                e.Property(x => x.Amount).HasColumnType("decimal(18,4)");
                e.Property(x => x.Direction).HasMaxLength(16).IsRequired();
                e.Property(x => x.LedgerType).HasMaxLength(32).IsRequired();
                e.Property(x => x.Account).HasMaxLength(128);
                e.Property(x => x.Note).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.SaleId, x.CreatedAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== CashAccount =====
            b.Entity<CashAccount>(e =>
            {
                e.ToTable("CashAccounts");
                e.HasKey(x => x.Id);
                e.Property(x => x.AccountType).HasMaxLength(32).IsRequired();
                e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
                e.Property(x => x.CurrentBalance).HasColumnType("decimal(18,4)");
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.AccountType, x.Currency, x.Name }).IsUnique();
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== CashTransaction =====
            b.Entity<CashTransaction>(e =>
            {
                e.ToTable("CashTransactions");
                e.HasKey(x => x.Id);
                e.Property(x => x.TxType).HasMaxLength(16).IsRequired();
                e.Property(x => x.SourceModule).HasMaxLength(24).IsRequired();
                e.Property(x => x.Currency).HasMaxLength(8).IsRequired();
                e.Property(x => x.Amount).HasColumnType("decimal(18,4)");
                e.Property(x => x.RefType).HasMaxLength(32);
                e.Property(x => x.Description).HasMaxLength(500);
                e.HasOne(x => x.CashAccount).WithMany().HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.TxDate, x.CreatedAt });
                e.HasIndex(x => new { x.TenantId, x.RefType, x.RefId });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== DayEndReport =====
            b.Entity<DayEndReport>(e =>
            {
                e.ToTable("DayEndReports");
                e.HasKey(x => x.Id);
                e.Property(x => x.BusinessDate).HasColumnType("date");
                e.Property(x => x.OpeningTl).HasColumnType("decimal(18,4)");
                e.Property(x => x.OpeningUsd).HasColumnType("decimal(18,4)");
                e.Property(x => x.OpeningEur).HasColumnType("decimal(18,4)");
                e.Property(x => x.OpeningHas).HasColumnType("decimal(18,4)");
                e.Property(x => x.ClosingTl).HasColumnType("decimal(18,4)");
                e.Property(x => x.ClosingUsd).HasColumnType("decimal(18,4)");
                e.Property(x => x.ClosingEur).HasColumnType("decimal(18,4)");
                e.Property(x => x.ClosingHas).HasColumnType("decimal(18,4)");
                e.Property(x => x.TotalIncomeTl).HasColumnType("decimal(18,4)");
                e.Property(x => x.TotalExpenseTl).HasColumnType("decimal(18,4)");
                e.Property(x => x.Status).HasMaxLength(16).IsRequired();
                e.Property(x => x.PdfPath).HasMaxLength(256);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.BusinessDate }).IsUnique();
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== Branch Notes =====
            b.Entity<BranchNote>(e =>
            {
                e.ToTable("BranchNotes");
                e.HasKey(x => x.Id);
                e.Property(x => x.Title).HasMaxLength(160).IsRequired();
                e.Property(x => x.Content).HasMaxLength(4000).IsRequired();
                e.Property(x => x.UpdatedAt).HasColumnType("datetime2");
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.UpdatedAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== Branch Reminders =====
            b.Entity<BranchReminder>(e =>
            {
                e.ToTable("BranchReminders");
                e.HasKey(x => x.Id);
                e.Property(x => x.Title).HasMaxLength(160).IsRequired();
                e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
                e.Property(x => x.Frequency).HasConversion<int>();
                e.Property(x => x.StartsAt).HasColumnType("datetime2");
                e.Property(x => x.NextRunAt).HasColumnType("datetime2");
                e.Property(x => x.LastRunAt).HasColumnType("datetime2");
                e.Property(x => x.IsActive).HasDefaultValue(true);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.NextRunAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== Accounting (Double Entry) =====
            b.Entity<Account>(e =>
            {
                e.ToTable("Accounts");
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
                e.Property(x => x.Code).HasMaxLength(32).IsRequired();
                e.Property(x => x.Type).HasConversion<int>();
                e.Property(x => x.IsSystemAccount).HasDefaultValue(false);
                e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<JournalEntry>(e =>
            {
                e.ToTable("JournalEntries");
                e.HasKey(x => x.Id);
                e.Property(x => x.Date).HasColumnType("datetime2");
                e.Property(x => x.Description).HasMaxLength(512).IsRequired();
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.Date, x.CreatedAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<JournalLine>(e =>
            {
                e.ToTable("JournalLines");
                e.HasKey(x => x.Id);
                e.Property(x => x.Debit).HasColumnType("decimal(18,4)");
                e.Property(x => x.Credit).HasColumnType("decimal(18,4)");
                e.HasOne(x => x.JournalEntry)
                    .WithMany(x => x.Lines)
                    .HasForeignKey(x => x.JournalEntryId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.Account)
                    .WithMany()
                    .HasForeignKey(x => x.AccountId)
                    .OnDelete(DeleteBehavior.Restrict);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.AccountId, x.CreatedAt });
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            // ===== Supplier (Tedarikçi) =====
            b.Entity<Supplier>(e =>
            {
                e.ToTable("Suppliers");
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).HasMaxLength(150);
                e.Property(x => x.SupplierCode).HasMaxLength(32);
                e.Property(x => x.CompanyName).HasMaxLength(200);
                e.Property(x => x.ContactName).HasMaxLength(100);
                e.Property(x => x.Phone).HasMaxLength(32);
                e.Property(x => x.Whatsapp).HasMaxLength(32);
                e.Property(x => x.Email).HasMaxLength(150);
                e.Property(x => x.City).HasMaxLength(64);
                e.Property(x => x.District).HasMaxLength(64);
                e.Property(x => x.Address).HasMaxLength(256);
                e.Property(x => x.TaxNumber).HasMaxLength(32);
                e.Property(x => x.TaxOffice).HasMaxLength(64);
                e.Property(x => x.Notes).HasMaxLength(2000);
                e.Property(x => x.CurrentDebt).HasColumnType("decimal(18,2)");
                e.Property(x => x.CurrentCredit).HasColumnType("decimal(18,2)");
                e.Property(x => x.Balance).HasColumnType("decimal(18,2)");
                e.Property(x => x.DefaultPaymentType).HasConversion<int>();
                e.Property(x => x.BankName).HasMaxLength(100);
                e.Property(x => x.IBAN).HasMaxLength(34);
                e.Property(x => x.RiskLimit).HasColumnType("decimal(18,2)");
                e.Property(x => x.CurrencyType).HasConversion<int>();
                e.Property(x => x.SupplierType).HasConversion<int>();
                e.Property(x => x.PricingType).HasConversion<int>();
                e.Property(x => x.ProductCategoriesWorkedWith).HasMaxLength(500);
                e.Property(x => x.KaratTypes).HasMaxLength(100);
                e.Property(x => x.DefaultLaborCostPerGram).HasColumnType("decimal(18,4)");
                e.Property(x => x.FireRate).HasColumnType("decimal(9,4)");
                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<SupplierBalance>(e =>
            {
                e.ToTable("SupplierBalances");
                e.HasKey(x => x.Id);
                e.Property(x => x.BalanceTL).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceUSD).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceEUR).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceHAS).HasColumnType("decimal(18,4)");
                e.Property(x => x.BalanceGUMUS).HasColumnType("decimal(18,4)");
                e.HasIndex(x => new { x.TenantId, x.SupplierId }).IsUnique();
                e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
            });

            b.Entity<SupplierTransaction>(e =>
            {
                e.ToTable("SupplierTransactions");
                e.HasKey(x => x.Id);
                e.Property(x => x.TxType).HasMaxLength(24).IsRequired();
                e.Property(x => x.SourceUnit).HasMaxLength(16).IsRequired();
                e.Property(x => x.SourceAmount).HasColumnType("decimal(18,6)");
                e.Property(x => x.TargetUnit).HasMaxLength(16).IsRequired();
                e.Property(x => x.TargetAmount).HasColumnType("decimal(18,6)");
                e.Property(x => x.SourceUnitTlRate).HasColumnType("decimal(18,6)");
                e.Property(x => x.TargetUnitTlRate).HasColumnType("decimal(18,6)");
                e.Property(x => x.Description).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.SupplierId, x.TxDate, x.CreatedAt });
                e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.SetNull);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
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
                e.Property(x => x.PurchaseType).HasConversion<int>();
                e.Property(x => x.PaymentMethod).HasConversion<int>();
                e.Property(x => x.TotalHas).HasColumnType("decimal(18,4)");

                e.HasOne(x => x.Branch).WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.SetNull);
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
                e.Property(x => x.BirimIscilikHas).HasColumnType("decimal(18,6)");
                e.Property(x => x.OdenecekToplamHas).HasColumnType("decimal(18,6)");

                e.HasOne(x => x.Purchase)
                    .WithMany(x => x.Items)
                    .HasForeignKey(x => x.PurchaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            b.Entity<PurchasePayment>(e =>
            {
                e.ToTable("PurchasePayments");
                e.HasKey(x => x.Id);
                e.Property(x => x.PaymentType).HasConversion<int>();
                e.Property(x => x.Amount).HasColumnType("decimal(18,2)");
                e.Property(x => x.GoldWeight).HasColumnType("decimal(18,4)");
                e.Property(x => x.GoldPrice).HasColumnType("decimal(18,4)");
                e.Property(x => x.GoldKarat).HasMaxLength(16);
                e.Property(x => x.BankName).HasMaxLength(128);
                e.Property(x => x.IBAN).HasMaxLength(34);
                e.Property(x => x.CashAccount).HasMaxLength(128);
                e.Property(x => x.UnitCode).HasMaxLength(16);
                e.Property(x => x.UnitAmount).HasColumnType("decimal(18,4)");
                e.Property(x => x.SilverWeight).HasColumnType("decimal(18,4)");
                e.Property(x => x.GoldSource).HasDefaultValue(0);
                e.HasOne(x => x.Purchase)
                    .WithMany(x => x.Payments)
                    .HasForeignKey(x => x.PurchaseId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasQueryFilter(x => !x.IsDeleted && x.TenantId == _tenant.TenantId);
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


            // ===== Category =====
            b.Entity<Category>(e =>
            {
                e.ToTable("Categories");
                e.HasKey(x => x.Id);
                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
                e.Property(x => x.KategoriKodu).HasMaxLength(16).IsRequired();
                e.HasIndex(x => new { x.TenantId, x.KategoriKodu }).IsUnique();
                e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
            });

            // ===== Product =====
            b.Entity<Product>(e =>
            {
                e.HasKey(x => x.Id);
                e.Property(x => x.ProductCode).HasMaxLength(64).IsRequired();
                e.Property(x => x.Name).HasMaxLength(128).IsRequired();
                e.Property(x => x.Category).HasMaxLength(64);
                e.Property(x => x.MalTanim).HasMaxLength(128);
                e.Property(x => x.DepoTedarikciFirma).HasMaxLength(128);
                e.Property(x => x.Karat).HasMaxLength(16);
                e.Property(x => x.Barcode).HasMaxLength(64);
                e.Property(x => x.Olcu).HasMaxLength(64);
                e.Property(x => x.InventoryType).HasConversion<int>(); // 0 = Tekil, 1 = Ziynet
                e.Property(x => x.StokMiktari);
                e.Property(x => x.ZiynetTipi).HasMaxLength(32); // Çeyrek, Yarım, Tam, Gram Altın vb.
                e.Property(x => x.IsSpecialProduct).HasDefaultValue(false);
                e.Property(x => x.BelirlenenSatisFiyatiHas).HasColumnType("decimal(18,4)");
                e.Property(x => x.BirimSatisIscilikHas).HasColumnType("decimal(18,4)");

                e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(DeleteBehavior.Restrict);

                // Benzersizlik: kiracı + şube + ProductCode (şubeler ayrı katalog)
                e.HasIndex(x => x.Barcode);
                e.HasIndex(x => new { x.TenantId, x.BranchId, x.ProductCode }).IsUnique();
                e.HasQueryFilter(x => _tenant != null && x.TenantId == _tenant.TenantId);
            });
        }
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            if (_tenant != null)
            {
                var entries = ChangeTracker.Entries()
                    .Where(e => e.State == EntityState.Added && e.Entity is ITenantScoped);
                foreach (var e in entries)
                    ((ITenantScoped)e.Entity).TenantId = _tenant.TenantId;
            }
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
