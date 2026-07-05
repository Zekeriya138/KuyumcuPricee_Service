using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// Gerekli Standart ASP.NET Core Using'leri
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;

// domain/app/infra
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Services;
using kuyumcu_application.Abstractions;
using Kuyumcu.PriceService.Services;
using Kuyumcu.PriceService.Models;
using KUYUMCU.Price_Service.Services;
using KUYUMCU.Price_Service.Middleware;
using kuyumcu_infrastructure.Tenancy;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ****************************** 1. SERV�S TANIMLAMALARI ******************************

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// İşlemi yapan kullanıcıyı cari/kasa hareketlerine damgalayan interceptor.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<KUYUMCU.Price_Service.Persistence.UserStampInterceptor>();

// DbContext (MigrationsAssembly: migration'lar kuyumcu_infrasructure içinde)
builder.Services.AddDbContext<AppDbContext>((sp, opt) =>
    opt.UseSqlServer(cfg.GetConnectionString("SqlServer"), b => b.MigrationsAssembly("kuyumcu_infrasructure"))
       .AddInterceptors(sp.GetRequiredService<KUYUMCU.Price_Service.Persistence.UserStampInterceptor>())
       .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

// Http + CORS + reverse proxy (IIS / nginx arkasında HTTPS algısı)
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddHttpClient();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));


// ---------------- HaremAPI fiyat beslemesi (https://haremapi.tr/docs/#prices-endpoints) ----------------
builder.Services.AddSingleton<PriceCache>();
builder.Services.AddHttpClient<HaremApiClient>()
    .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
builder.Services.AddSingleton<GoldPriceService>();
builder.Services.AddScoped<ExchangeRateService>();
builder.Services.AddHostedService<GoldPriceBackgroundRefresher>();


// ... (Sat�r 75 civar�)

// ---------------- TENANT MANAGEMENT (G�NCELLEND�) ----------------

// ---------------- TENANT MANAGEMENT (D�ZELT�LD�) ----------------
builder.Services.AddHttpContextAccessor();

// Tek bir TenantContext olu�tur (Scoped)
builder.Services.AddScoped<TenantContext>();

// ITenantContext istendi�inde ayn� instance d�ns�n
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
// ---------------- END TENANT MANAGEMENT ----------------

//builder.Services.AddHttpContextAccessor();

//// Ad�m 1: Somut TenantContext s�n�f�n� kaydet ve t�m loji�i buraya ta��
//// Bu, ITenantContext'i DI'dan isteyen her �eyin do�ru yap�land�rmay� almas�n� sa�lar.
//builder.Services.AddScoped<TenantContext>(sp =>
//{
//    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
//    var http = httpContextAccessor.HttpContext;

//    // HttpContext yoksa (arka plan servisi)
//    if (http == null)
//    {
//        return new TenantContext { TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001") };
//    }

//    // 1. Claims'den TenantId'yi al
//    var tenantStr = http.User.FindFirst("tenant_id")?.Value;

//    // 2. Header'dan TenantId'yi al
//    if (string.IsNullOrWhiteSpace(tenantStr))
//    {
//        tenantStr = http.Request.Headers["X-Tenant-Id"].FirstOrDefault();
//    }

//    Guid tenantId;
//    if (!Guid.TryParse(tenantStr, out tenantId))
//    {
//        tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
//    }

//    // BranchId'yi kontrol et
//    Guid? branchId = null;
//    var branchStr = http.User.FindFirst("branch_id")?.Value ?? http.Request.Headers["X-Branch-Id"].FirstOrDefault();
//    if (Guid.TryParse(branchStr, out var bId))
//    {
//        branchId = bId;
//    }

//    return new TenantContext { TenantId = tenantId, BranchId = branchId };
//});

//// Ad�m 2: ITenantContext aray�z� istendi�inde, somut s�n�f� d�nd�r.
//// Bu, TenantContext'in Fabrika Loji�i ile olu�turuldu�unu garanti eder.
//builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// ---------------- END TENANT MANAGEMENT ----------------


// App services (Art�k ITenantContext'i g�venle ��zebilirler)
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IStockService, StockService>(); // Stock 
builder.Services.AddScoped<IScrapService, ScrapService>();   // Hurda stok
builder.Services.AddScoped<IFinanceService, FinanceService>();
builder.Services.AddScoped<IAiService, AiService>();
builder.Services.AddScoped<IBranchSubscriptionService, BranchSubscriptionService>();
builder.Services.AddScoped<IAccountingJournalService, AccountingJournalService>();
builder.Services.AddScoped<IBalanceSheetService, BalanceSheetService>();
builder.Services.AddSingleton<IJewelryTaxCalculator, JewelryTaxCalculator>();
builder.Services.AddSingleton<IJewelryProductTypeMapper, JewelryProductTypeMapper>();
builder.Services.AddScoped<IUblInvoiceBuilder, UblInvoiceBuilder>();
builder.Services.AddScoped<IEInvoiceWorkflowService, EInvoiceWorkflowService>();
builder.Services.AddScoped<IEInvoiceProviderResolver, EInvoiceProviderResolver>();
builder.Services.AddHttpClient<EdmSoapEInvoiceProviderAdapter>((sp, client) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["EInvoice:Edm:Endpoint"];
    if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        client.BaseAddress = uri;
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddScoped<IEInvoiceProviderAdapter>(sp => sp.GetRequiredService<EdmSoapEInvoiceProviderAdapter>());
builder.Services.AddHttpClient<UyumsoftEInvoiceProviderAdapter>((sp, client) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var baseUrl = configuration["EInvoice:Uyumsoft:BaseUrl"];
    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        client.BaseAddress = uri;
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddScoped<IEInvoiceProviderAdapter>(sp => sp.GetRequiredService<UyumsoftEInvoiceProviderAdapter>());
builder.Services.AddScoped<IEInvoiceProviderAdapter, StubEInvoiceProviderAdapter>();
builder.Services.AddHostedService<EInvoiceOutboxWorker>();

// Controllers
builder.Services.AddControllers();


// Yetkilendirme Politikalar�
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("BranchAdmin", p => p.RequireRole("Owner", "Admin"));
});


// JWT Authentication �emas�
var jwtKey = cfg["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException("Jwt:Key tanimli degil. Azure App Service -> Ortam degiskenleri -> Jwt__Key ekleyin.");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = cfg["Jwt:Issuer"],
            ValidAudience = cfg["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();


// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // API Key Tan�m�
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = cfg["Auth:HeaderName"] ?? "x-app-key",
        Type = SecuritySchemeType.ApiKey,
        Description = "Uygulama anahtar�"
    });
    // JWT Bearer Tan�m�
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Bearer {token}"
    });
    // G�venlik Gereksinimleri
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="ApiKey"}}, Array.Empty<string>() },
        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="Bearer"}}, Array.Empty<string>() }
    });
});

// User Secrets en son: appsettings'teki boş ApiKey ile ezilmesin (Upstream:HaremApi:ApiKey)
builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);

var app = builder.Build();

// ****************************** 2. REQUEST PIPELINE ******************************

// DB migrate + seed (Tek bir noktada topland�)
var autoMigrate = cfg.GetValue<bool?>("Hosting:AutoMigrateDatabase") ?? true;
if (autoMigrate)
{
try
{
var sqlConn = cfg.GetConnectionString("SqlServer");
if (string.IsNullOrWhiteSpace(sqlConn))
    throw new InvalidOperationException("ConnectionStrings:SqlServer tanimli degil. Azure -> ConnectionStrings__SqlServer ekleyin.");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // KRİTİK idempotent güvenlik: Invoices.PaymentSplitRatio kolonu (migration atlanmış/uygulanmamış
    // ortamlar için). Kendi bağımsız try/catch bloğunda; sonraki güvenlik ifadeleri ne olursa olsun çalışır.
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[Invoices]', N'U') IS NOT NULL
   AND COL_LENGTH('Invoices', 'PaymentSplitRatio') IS NULL
    ALTER TABLE [dbo].[Invoices] ADD [PaymentSplitRatio] decimal(18,10) NOT NULL CONSTRAINT [DF_Invoices_PaymentSplitRatio] DEFAULT 1.0;");
    }
    catch (Exception ex) { Console.WriteLine("EnsureInvoicePaymentSplitRatio: " + ex.Message); }

    // Tahsilat kaynaklı (satışsız) taslak faturalar için alıcı + has altın kalem meta bilgisi.
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[Invoices]', N'U') IS NOT NULL
   AND COL_LENGTH('Invoices', 'CollectionMetaJson') IS NULL
    ALTER TABLE [dbo].[Invoices] ADD [CollectionMetaJson] nvarchar(max) NULL;");
    }
    catch (Exception ex) { Console.WriteLine("EnsureInvoiceCollectionMetaJson: " + ex.Message); }

    // Tahsilat (satışsız) taslak faturaları SaleId olmadan kaydedilir — FK nullable olmalı.
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[Invoices]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Invoices_Sales_SaleId' AND parent_object_id = OBJECT_ID(N'[dbo].[Invoices]'))
        ALTER TABLE [dbo].[Invoices] DROP CONSTRAINT [FK_Invoices_Sales_SaleId];

    IF COL_LENGTH('Invoices', 'SaleId') IS NOT NULL
       AND EXISTS (
            SELECT 1 FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            WHERE t.name = N'Invoices' AND c.name = N'SaleId' AND c.is_nullable = 0)
        ALTER TABLE [dbo].[Invoices] ALTER COLUMN [SaleId] uniqueidentifier NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_Invoices_Sales_SaleId' AND parent_object_id = OBJECT_ID(N'[dbo].[Invoices]'))
       AND COL_LENGTH('Invoices', 'SaleId') IS NOT NULL
        ALTER TABLE [dbo].[Invoices] ADD CONSTRAINT [FK_Invoices_Sales_SaleId]
            FOREIGN KEY ([SaleId]) REFERENCES [dbo].[Sales]([Id]);
END");
    }
    catch (Exception ex) { Console.WriteLine("EnsureInvoiceSaleIdNullable: " + ex.Message); }

    // Gelen e-Faturalar tablosu (EDM inbox). Migration atlanmış ortamlar için idempotent oluşturma.
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[IncomingEInvoices]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[IncomingEInvoices](
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_IncomingEInvoices] PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [Uuid] nvarchar(64) NOT NULL,
        [InvoiceNumber] nvarchar(64) NULL,
        [SenderName] nvarchar(400) NULL,
        [SenderTaxNumber] nvarchar(16) NULL,
        [DocumentType] nvarchar(16) NULL,
        [Status] nvarchar(64) NULL,
        [StatusDescription] nvarchar(400) NULL,
        [PayableAmount] decimal(18,2) NOT NULL DEFAULT 0,
        [Currency] nvarchar(8) NULL,
        [IssueDate] datetime2 NULL,
        [EnvelopeIdentifier] nvarchar(128) NULL,
        [RawContent] nvarchar(max) NULL,
        [FetchedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [CreatedAt] datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        [UpdatedAt] datetime2 NULL,
        [IsDeleted] bit NOT NULL DEFAULT 0
    );
    CREATE UNIQUE INDEX [IX_IncomingEInvoices_Tenant_Uuid] ON [dbo].[IncomingEInvoices]([TenantId],[Uuid]);
    CREATE INDEX [IX_IncomingEInvoices_Tenant_Branch_IssueDate] ON [dbo].[IncomingEInvoices]([TenantId],[BranchId],[IssueDate]);
END");
        // Yeni kolonlar (varsa ekle, yoksa geç)
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('IncomingEInvoices') AND name = 'ReceiverName') ALTER TABLE [dbo].[IncomingEInvoices] ADD [ReceiverName] nvarchar(400) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('IncomingEInvoices') AND name = 'ReceiverTaxNumber') ALTER TABLE [dbo].[IncomingEInvoices] ADD [ReceiverTaxNumber] nvarchar(16) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('IncomingEInvoices') AND name = 'GibStatusDescription') ALTER TABLE [dbo].[IncomingEInvoices] ADD [GibStatusDescription] nvarchar(400) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('IncomingEInvoices') AND name = 'ProfileId') ALTER TABLE [dbo].[IncomingEInvoices] ADD [ProfileId] nvarchar(64) NULL");
    }
    catch (Exception ex) { Console.WriteLine("EnsureIncomingEInvoices: " + ex.Message); }

    // Manuel gümüş stok lotları (Stok/Depo gümüş sekmesi — alış/kasa hariç).
    try
    {
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[DepoGumusLots]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[DepoGumusLots] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_DepoGumusLots] PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [SupplierId] uniqueidentifier NULL,
        [SupplierName] nvarchar(256) NOT NULL,
        [ProductCode] nvarchar(64) NOT NULL,
        [ProductName] nvarchar(256) NOT NULL,
        [Gram] decimal(18,4) NOT NULL,
        [UnitCostTl] decimal(18,2) NOT NULL,
        [Note] nvarchar(512) NULL,
        [EntryDate] datetime2 NOT NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_DepoGumusLots_IsDeleted] DEFAULT 0,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_DepoGumusLots_CreatedAt] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [FK_DepoGumusLots_Branches_BranchId] FOREIGN KEY ([BranchId]) REFERENCES [dbo].[Branches]([Id])
    );
    CREATE INDEX [IX_DepoGumusLots_BranchId] ON [dbo].[DepoGumusLots]([BranchId]);
    CREATE INDEX [IX_DepoGumusLots_TenantId_BranchId_ProductCode] ON [dbo].[DepoGumusLots]([TenantId],[BranchId],[ProductCode]);
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[DepoGumusLots]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = N'20260705000000_CreateDepoGumusLotsTable')
    INSERT INTO [dbo].[__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260705000000_CreateDepoGumusLotsTable', N'9.0.8');");
    }
    catch (Exception ex) { Console.WriteLine("EnsureDepoGumusLots: " + ex.Message); }

    try
    {
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'InventoryType') ALTER TABLE Products ADD InventoryType int NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'StokMiktari') ALTER TABLE Products ADD StokMiktari int NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'ZiynetTipi') ALTER TABLE Products ADD ZiynetTipi nvarchar(32) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'IsSpecialProduct') ALTER TABLE Products ADD IsSpecialProduct bit NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'BirimSatisIscilikHas') ALTER TABLE Products ADD BirimSatisIscilikHas decimal(18,4) NULL");
        // DepoBirimMaliyet: birim işçilik (has/milyem) 4+ hane gerektirir; decimal(18,2) 0.074 → 0.07 yuvarlıyordu.
        await db.Database.ExecuteSqlRawAsync(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'DepoBirimMaliyet')
BEGIN
    ALTER TABLE Products ALTER COLUMN DepoBirimMaliyet decimal(18,6) NULL;
END");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Sales') AND name = 'DeliveryType') ALTER TABLE Sales ADD DeliveryType nvarchar(16) NULL");
        // İşlemi yapan kullanıcı takibi: cari/kasa hareketlerine UserId + KullaniciAdi (görünen ad anlık kopyası).
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CustomerTransactions') AND name = 'UserId') ALTER TABLE CustomerTransactions ADD UserId uniqueidentifier NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CustomerTransactions') AND name = 'KullaniciAdi') ALTER TABLE CustomerTransactions ADD KullaniciAdi nvarchar(200) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SupplierTransactions') AND name = 'UserId') ALTER TABLE SupplierTransactions ADD UserId uniqueidentifier NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SupplierTransactions') AND name = 'KullaniciAdi') ALTER TABLE SupplierTransactions ADD KullaniciAdi nvarchar(200) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CashTransactions') AND name = 'UserId') ALTER TABLE CashTransactions ADD UserId uniqueidentifier NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('CashTransactions') AND name = 'KullaniciAdi') ALTER TABLE CashTransactions ADD KullaniciAdi nvarchar(200) NULL");
        // Özel tekil ürünlerde StokMiktari 0 kalmış; ilgili kiracıda hiç satış kalemi yoksa 1 yap (Sales join gerekmez).
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
UPDATE p SET p.StokMiktari = 1
FROM Products p
WHERE p.IsDeleted = 0
  AND p.IsSpecialProduct = 1
  AND (p.InventoryType IS NULL OR p.InventoryType = 0)
  AND (p.StokMiktari IS NULL OR p.StokMiktari = 0)
  AND p.Barcode IS NOT NULL AND LEN(LTRIM(RTRIM(p.Barcode))) >= 10
  AND NOT EXISTS (
    SELECT 1 FROM SaleItems si
    WHERE si.TenantId = p.TenantId AND si.ProductCode = p.ProductCode
  )");
        }
        catch (Exception ex) { Console.WriteLine("FixOzelTekilStok: " + ex.Message); }
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'Note') ALTER TABLE Customers ADD Note nvarchar(2000) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Customers') AND name = 'TedarikciExtJson') ALTER TABLE Customers ADD TedarikciExtJson nvarchar(max) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'Image') ALTER TABLE Products ADD Image varbinary(max) NULL");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[StockCountSessions]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StockCountSessions](
        [Id] uniqueidentifier NOT NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_StockCountSessions_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [Name] nvarchar(160) NOT NULL CONSTRAINT [DF_StockCountSessions_Name] DEFAULT(N''),
        [Status] nvarchar(16) NOT NULL CONSTRAINT [DF_StockCountSessions_Status] DEFAULT(N'Open'),
        [ExpectedCount] int NOT NULL CONSTRAINT [DF_StockCountSessions_Expected] DEFAULT(0),
        [MatchedCount] int NOT NULL CONSTRAINT [DF_StockCountSessions_Matched] DEFAULT(0),
        [UnknownCount] int NOT NULL CONSTRAINT [DF_StockCountSessions_Unknown] DEFAULT(0),
        [MissingCount] int NOT NULL CONSTRAINT [DF_StockCountSessions_Missing] DEFAULT(0),
        [CreatedByUserId] uniqueidentifier NULL,
        [CreatedByName] nvarchar(160) NULL,
        [CompletedAt] datetime2 NULL,
        CONSTRAINT [PK_StockCountSessions] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_StockCountSessions_Tenant_Branch_Status] ON [dbo].[StockCountSessions]([TenantId],[BranchId],[Status],[CreatedAt]);
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[StockCountScans]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[StockCountScans](
        [Id] uniqueidentifier NOT NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_StockCountScans_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [SessionId] uniqueidentifier NOT NULL,
        [Barcode] nvarchar(64) NOT NULL,
        [ProductId] uniqueidentifier NULL,
        [ProductCode] nvarchar(64) NULL,
        [ProductName] nvarchar(200) NULL,
        [MatchStatus] nvarchar(16) NOT NULL CONSTRAINT [DF_StockCountScans_MatchStatus] DEFAULT(N'Unknown'),
        [ScannedAt] datetime2 NOT NULL,
        [ScannedByUserId] uniqueidentifier NULL,
        [ScannedByName] nvarchar(160) NULL,
        CONSTRAINT [PK_StockCountScans] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_StockCountScans_Session] ON [dbo].[StockCountScans]([TenantId],[SessionId],[ScannedAt]);
END");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanManageUsers') ALTER TABLE Users ADD CanManageUsers bit NOT NULL CONSTRAINT DF_Users_CanManageUsers DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanManageBranches') ALTER TABLE Users ADD CanManageBranches bit NOT NULL CONSTRAINT DF_Users_CanManageBranches DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanSwitchBranches') ALTER TABLE Users ADD CanSwitchBranches bit NOT NULL CONSTRAINT DF_Users_CanSwitchBranches DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanUseEInvoice') ALTER TABLE Users ADD CanUseEInvoice bit NOT NULL CONSTRAINT DF_Users_CanUseEInvoice DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanUseEArchive') ALTER TABLE Users ADD CanUseEArchive bit NOT NULL CONSTRAINT DF_Users_CanUseEArchive DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanUseExpenseSlip') ALTER TABLE Users ADD CanUseExpenseSlip bit NOT NULL CONSTRAINT DF_Users_CanUseExpenseSlip DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanManageRates') ALTER TABLE Users ADD CanManageRates bit NOT NULL CONSTRAINT DF_Users_CanManageRates DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanViewBalanceSheet') ALTER TABLE Users ADD CanViewBalanceSheet bit NOT NULL CONSTRAINT DF_Users_CanViewBalanceSheet DEFAULT(0)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanAccessCustomers') ALTER TABLE Users ADD CanAccessCustomers bit NOT NULL CONSTRAINT DF_Users_CanAccessCustomers DEFAULT(1)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanAccessSuppliers') ALTER TABLE Users ADD CanAccessSuppliers bit NOT NULL CONSTRAINT DF_Users_CanAccessSuppliers DEFAULT(1)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanAccessPurchase') ALTER TABLE Users ADD CanAccessPurchase bit NOT NULL CONSTRAINT DF_Users_CanAccessPurchase DEFAULT(1)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanAccessSales') ALTER TABLE Users ADD CanAccessSales bit NOT NULL CONSTRAINT DF_Users_CanAccessSales DEFAULT(1)");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Users') AND name = 'CanCreateIncomeExpense') ALTER TABLE Users ADD CanCreateIncomeExpense bit NOT NULL CONSTRAINT DF_Users_CanCreateIncomeExpense DEFAULT(1)");
        await db.Database.ExecuteSqlRawAsync(@"UPDATE Users SET Role='Admin' WHERE Role='Manager'");
        // Users.IsActive, UserSalaryHistories, RateDisplaySettings: EF migration 20260414150057_kjdavkjahsj ile yönetiliyor.
        // Ancak bazı ortamlarda migration atlanabildiği için RateDisplaySettings.BranchId için emniyetli/idempotent düzeltme.
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('RateDisplaySettings') AND name = 'BranchId')
    ALTER TABLE [RateDisplaySettings] ADD [BranchId] uniqueidentifier NULL");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('RateDisplaySettings') AND name = 'BidTlOffset')
        ALTER TABLE [RateDisplaySettings] ADD [BidTlOffset] decimal(18,4) NOT NULL CONSTRAINT [DF_RateDisplaySettings_BidTlOffset] DEFAULT 0;
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('RateDisplaySettings') AND name = 'AskTlOffset')
        ALTER TABLE [RateDisplaySettings] ADD [AskTlOffset] decimal(18,4) NOT NULL CONSTRAINT [DF_RateDisplaySettings_AskTlOffset] DEFAULT 0;
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[ExpenseSlipDocuments]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ExpenseSlipDocuments](
        [Id] uniqueidentifier NOT NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [SourceSaleId] uniqueidentifier NULL,
        [DocumentNo] nvarchar(64) NOT NULL,
        [Status] nvarchar(32) NOT NULL,
        [Currency] nvarchar(8) NOT NULL,
        [GrandTotal] decimal(18,2) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_GrandTotal] DEFAULT(0),
        [BuyerName] nvarchar(256) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_BuyerName] DEFAULT(N''),
        [BuyerTaxNumber] nvarchar(32) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_BuyerTaxNumber] DEFAULT(N''),
        [Description] nvarchar(512) NULL,
        [PayloadJson] nvarchar(max) NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_PayloadJson] DEFAULT(N'{}'),
        [RawLastResponse] nvarchar(max) NULL,
        [IntegratorDocumentId] nvarchar(128) NULL,
        [Uuid] nvarchar(64) NULL,
        [LastError] nvarchar(1000) NULL,
        [RetryCount] int NOT NULL CONSTRAINT [DF_ExpenseSlipDocuments_RetryCount] DEFAULT(0),
        [SubmittedAt] datetime2 NULL,
        CONSTRAINT [PK_ExpenseSlipDocuments] PRIMARY KEY ([Id])
    );
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[ExpenseSlipDocuments]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ExpenseSlipDocuments') AND name = 'RawLastResponse')
        ALTER TABLE [dbo].[ExpenseSlipDocuments] ADD [RawLastResponse] nvarchar(max) NULL;
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExpenseSlipDocuments_Tenant_Branch_Status_CreatedAt' AND object_id = OBJECT_ID('ExpenseSlipDocuments'))
        CREATE INDEX [IX_ExpenseSlipDocuments_Tenant_Branch_Status_CreatedAt]
            ON [dbo].[ExpenseSlipDocuments]([TenantId], [BranchId], [Status], [CreatedAt]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExpenseSlipDocuments_Tenant_DocumentNo' AND object_id = OBJECT_ID('ExpenseSlipDocuments'))
        CREATE UNIQUE INDEX [IX_ExpenseSlipDocuments_Tenant_DocumentNo]
            ON [dbo].[ExpenseSlipDocuments]([TenantId], [DocumentNo]);
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('EInvoiceProfiles', 'IntegratorCompanyCode') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[EInvoiceProfiles] ALTER COLUMN [IntegratorCompanyCode] nvarchar(max) NULL;
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[ExpenseSlipAuditLogs]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ExpenseSlipAuditLogs](
        [Id] uniqueidentifier NOT NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_ExpenseSlipAuditLogs_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [Action] nvarchar(64) NOT NULL,
        [StatusBefore] nvarchar(32) NULL,
        [StatusAfter] nvarchar(32) NULL,
        [IsSuccess] bit NOT NULL CONSTRAINT [DF_ExpenseSlipAuditLogs_IsSuccess] DEFAULT(0),
        [RequestJson] nvarchar(max) NULL,
        [ResponseRaw] nvarchar(max) NULL,
        [ErrorMessage] nvarchar(1000) NULL,
        CONSTRAINT [PK_ExpenseSlipAuditLogs] PRIMARY KEY ([Id])
    );
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[ExpenseSlipAuditLogs]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ExpenseSlipAuditLogs_Tenant_Branch_Document_CreatedAt' AND object_id = OBJECT_ID('ExpenseSlipAuditLogs'))
        CREATE INDEX [IX_ExpenseSlipAuditLogs_Tenant_Branch_Document_CreatedAt]
            ON [dbo].[ExpenseSlipAuditLogs]([TenantId], [BranchId], [DocumentId], [CreatedAt]);
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('RateDisplaySettings') AND name = 'BranchId')
BEGIN
    ;WITH FirstBranch AS (
        SELECT t.Id AS TenantId,
               (
                   SELECT TOP 1 b2.Id
                   FROM Branches b2
                   WHERE b2.TenantId = t.Id
                   ORDER BY b2.CreatedAt
               ) AS BranchId
        FROM Tenants t
    )
    UPDATE r
    SET r.BranchId = fb.BranchId
    FROM RateDisplaySettings r
    INNER JOIN FirstBranch fb ON fb.TenantId = r.TenantId
    WHERE r.BranchId IS NULL
      AND fb.BranchId IS NOT NULL;

    UPDATE r
    SET r.BidTlOffset = r.TlOffset,
        r.AskTlOffset = r.TlOffset
    FROM RateDisplaySettings r
    WHERE r.BidTlOffset = 0
      AND r.AskTlOffset = 0
      AND r.TlOffset <> 0;
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[RateDisplaySettings]', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('RateDisplaySettings') AND name = 'BranchId')
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RateDisplaySettings_TenantId_Code' AND object_id = OBJECT_ID(N'[RateDisplaySettings]'))
        DROP INDEX [IX_RateDisplaySettings_TenantId_Code] ON [RateDisplaySettings];

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RateDisplaySettings_BranchId' AND object_id = OBJECT_ID(N'[RateDisplaySettings]'))
        CREATE INDEX [IX_RateDisplaySettings_BranchId] ON [RateDisplaySettings]([BranchId]);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RateDisplaySettings_TenantId_BranchId_Code' AND object_id = OBJECT_ID(N'[RateDisplaySettings]'))
        CREATE UNIQUE INDEX [IX_RateDisplaySettings_TenantId_BranchId_Code] ON [RateDisplaySettings]([TenantId], [BranchId], [Code]);

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_RateDisplaySettings_Branches_BranchId')
        ALTER TABLE [RateDisplaySettings] WITH CHECK
        ADD CONSTRAINT [FK_RateDisplaySettings_Branches_BranchId]
        FOREIGN KEY([BranchId]) REFERENCES [Branches]([Id]) ON DELETE NO ACTION;
END");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'SupplierCode') ALTER TABLE Suppliers ADD SupplierCode nvarchar(32) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'CompanyName') ALTER TABLE Suppliers ADD CompanyName nvarchar(200) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'ContactName') ALTER TABLE Suppliers ADD ContactName nvarchar(100) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'SupplierType') ALTER TABLE Suppliers ADD SupplierType int NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'Whatsapp') ALTER TABLE Suppliers ADD Whatsapp nvarchar(32) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'City') ALTER TABLE Suppliers ADD City nvarchar(64) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'District') ALTER TABLE Suppliers ADD District nvarchar(64) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'Notes') ALTER TABLE Suppliers ADD Notes nvarchar(2000) NULL");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'Balance') ALTER TABLE Suppliers ADD Balance decimal(18,2) NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(@"IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Suppliers') AND name = 'IsActive') ALTER TABLE Suppliers ADD IsActive bit NOT NULL DEFAULT 1");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[BranchSubscriptions]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[BranchSubscriptions](
        [Id] uniqueidentifier NOT NULL,
        [TenantId] uniqueidentifier NOT NULL,
        [BranchId] uniqueidentifier NOT NULL,
        [PeriodType] int NOT NULL,
        [PackageType] int NOT NULL,
        [Status] int NOT NULL,
        [IsLifetime] bit NOT NULL CONSTRAINT [DF_BranchSubscriptions_IsLifetime] DEFAULT(0),
        [IncludesEInvoice] bit NOT NULL CONSTRAINT [DF_BranchSubscriptions_IncludesEInvoice] DEFAULT(0),
        [IncludesAiAssistant] bit NOT NULL CONSTRAINT [DF_BranchSubscriptions_IncludesAiAssistant] DEFAULT(0),
        [Price] decimal(18,2) NOT NULL CONSTRAINT [DF_BranchSubscriptions_Price] DEFAULT(0),
        [Currency] nvarchar(8) NOT NULL CONSTRAINT [DF_BranchSubscriptions_Currency] DEFAULT(N'TRY'),
        [StartsAtUtc] datetime2 NULL,
        [EndsAtUtc] datetime2 NULL,
        [LastPaymentAtUtc] datetime2 NULL,
        [LastCheckedAtUtc] datetime2 NULL,
        [IyzipayConversationId] nvarchar(120) NULL,
        [IyzipayToken] nvarchar(120) NULL,
        [IyzipayPaymentId] nvarchar(120) NULL,
        [IyzipayStatus] nvarchar(64) NULL,
        [IyzipayRawResponse] nvarchar(max) NULL,
        [Note] nvarchar(500) NULL,
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_BranchSubscriptions_IsDeleted] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_BranchSubscriptions_CreatedAt] DEFAULT(sysutcdatetime()),
        CONSTRAINT [PK_BranchSubscriptions] PRIMARY KEY CLUSTERED([Id] ASC)
    );
END");
        await db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[dbo].[BranchSubscriptions]', N'U') IS NOT NULL
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchSubscriptions_Tenant_Branch_CreatedAt' AND object_id = OBJECT_ID(N'[BranchSubscriptions]'))
        CREATE INDEX [IX_BranchSubscriptions_Tenant_Branch_CreatedAt] ON [dbo].[BranchSubscriptions]([TenantId],[BranchId],[CreatedAt]);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchSubscriptions_Tenant_Branch_Status_EndsAtUtc' AND object_id = OBJECT_ID(N'[BranchSubscriptions]'))
        CREATE INDEX [IX_BranchSubscriptions_Tenant_Branch_Status_EndsAtUtc] ON [dbo].[BranchSubscriptions]([TenantId],[BranchId],[Status],[EndsAtUtc]);
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_BranchSubscriptions_Branches_BranchId')
        ALTER TABLE [dbo].[BranchSubscriptions]  WITH CHECK ADD CONSTRAINT [FK_BranchSubscriptions_Branches_BranchId] FOREIGN KEY([BranchId])
        REFERENCES [dbo].[Branches] ([Id]) ON DELETE NO ACTION;
END");
        // Product.BranchId geçişinde (eski veri) şube tarihinden eski ürünler yanlış şubeye düşmüş olabilir.
        // Kural: ürün tarihi > seçilen şube oluşturma tarihi olmalı; değilse tenant'ın ilk şubesine geri taşı.
        await db.Database.ExecuteSqlRawAsync(@"
;WITH FirstBranch AS (
    SELECT t.Id AS TenantId,
           (
               SELECT TOP 1 b2.Id
               FROM Branches b2
               WHERE b2.TenantId = t.Id
               ORDER BY b2.CreatedAt
           ) AS BranchId
    FROM Tenants t
)
UPDATE p
SET p.BranchId = fb.BranchId
FROM Products p
INNER JOIN Branches b ON b.Id = p.BranchId
INNER JOIN FirstBranch fb ON fb.TenantId = p.TenantId
WHERE p.CreatedAt < b.CreatedAt
  AND fb.BranchId IS NOT NULL
  AND p.BranchId <> fb.BranchId;
");
        // İlk şube, yanlışlıkla soft-delete olduysa tarihsel veriye erişim için geri aç.
        await db.Database.ExecuteSqlRawAsync(@"
;WITH FirstBranch AS (
    SELECT t.Id AS TenantId,
           (
               SELECT TOP 1 b2.Id
               FROM Branches b2
               WHERE b2.TenantId = t.Id
               ORDER BY b2.CreatedAt
           ) AS BranchId
    FROM Tenants t
)
UPDATE b
SET b.IsDeleted = 0,
    b.IsActive = 1
FROM Branches b
INNER JOIN FirstBranch fb ON fb.BranchId = b.Id
WHERE b.IsDeleted = 1;
");
    }
    catch (Exception ex) { Console.WriteLine("EnsureColumns: " + ex.Message); }
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await DbInitializer.EnsureSeedAsync(db, configuration);
}
}
catch (Exception ex)
{
    Console.WriteLine("Startup DB migrate/seed BASARISIZ (API yine de acilir, /health kontrol edin): " + ex.Message);
    if (ex.InnerException != null)
        Console.WriteLine("Inner: " + ex.InnerException.Message);
}
}

// Harem kurları: uygulama açılır açılmaz bir kez doldur (arka plan zamanlayıcısından önce)
try
{
    var goldSvc = app.Services.GetRequiredService<GoldPriceService>();
    await goldSvc.RefreshAsync(CancellationToken.None);
}
catch (Exception ex)
{
    Console.WriteLine("İlk kur yenileme atlandı: " + ex.Message);
}

// CORS + reverse proxy + (opsiyonel) Swagger
app.UseForwardedHeaders();
app.UseCors();

var enableSwagger = cfg.GetValue<bool?>("Hosting:EnableSwagger")
    ?? app.Environment.IsDevelopment();
if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment() && (cfg.GetValue<bool?>("Hosting:EnableHttpsRedirection") ?? true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Global exception handler: 500 hatalarında JSON { "detail": "..." } döndür (masaüstü uygulama gösterebilsin)
app.Use(async (ctx, next) =>
{
    try { await next(ctx); }
    catch (Exception ex)
    {
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var msg = ex.Message;
        if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
        await ctx.Response.WriteAsJsonAsync(new { detail = msg, error = msg });
    }
});

// 1. Custom API-Key middleware (JWT'den �nce �al���r)
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "";
    // Swagger, /api/auth ve /ping endpoint'lerini serbest b�rak
    if (p.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/auth", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/api/einvoice/webhook", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("/ping", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("/health/", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("/ping/", StringComparison.OrdinalIgnoreCase))
    { await next(); return; }

    var headerName = cfg["Auth:HeaderName"] ?? "x-app-key";
    var allowed = cfg.GetSection("Auth:AllowedKeys").Get<string[]>() ?? Array.Empty<string>();

    if (!ctx.Request.Headers.TryGetValue(headerName, out var key) ||
        !allowed.Contains(key.ToString(), StringComparer.Ordinal))
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized: Missing or invalid App Key");
        return;
    }

    await next();
});


// Standart ASP.NET Core s�ralamas�:
// 2. Authentication (JWT Token'� do�rular)
app.UseAuthentication();

// 3. Tenant Middleware (JWT�den User.Claims dolduktan SONRA tenant�� doldurur)
app.UseMiddleware<kuyumcu_infrastructure.Tenancy.TenantMiddleware>();
app.UseMiddleware<BranchSubscriptionMiddleware>();

// 4. Authorization (Claims'e g�re [Authorize] attribute'lerini kontrol eder)
app.UseAuthorization();


app.MapGet("/ping", () => Results.Ok("pong"));
app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
{
    try
    {
        var ok = await db.Database.CanConnectAsync(ct);
        return ok
            ? Results.Ok(new { status = "healthy", utc = DateTime.UtcNow })
            : Results.Json(new { status = "unhealthy", reason = "db_unreachable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", reason = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapControllers();

// Son veriler
app.MapGet("/gold/latest", (GoldPriceService svc) =>
{
    var list = svc.LatestOrEmpty();
    return Results.Ok(list);
});

app.MapPost("/gold/refresh", async (GoldPriceService svc, CancellationToken ct) =>
{
    var list = await svc.RefreshAsync(ct);
    return Results.Ok(list);
});
app.MapGet("/gold/harem-debug", async (HaremApiClient cli, CancellationToken ct) =>
{
    var r = await cli.FetchPricesAsync(ct);
    return Results.Ok(new { count = r.Items.Count, updatedAt = r.UpdatedAt, stale = r.Stale, sample = r.Items.Take(12) });
});

app.Run();

//using System.Text;
//using Microsoft.AspNetCore.Authentication.JwtBearer;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.IdentityModel.Tokens;
//using Microsoft.OpenApi.Models;

//// Gerekli Standart ASP.NET Core Using'leri
//using System.Linq;
//using Microsoft.AspNetCore.Http;
//using Microsoft.Extensions.Configuration;
//using Microsoft.Extensions.DependencyInjection;
//using System.Security.Claims;
//using Microsoft.AspNetCore.Builder; // WebApplicationBuilder i�in
//using Microsoft.AspNetCore.Hosting; // Hosting Environment i�in

//// domain/app/infra
//using kuyumcu_infrastructure.Persistence;
//using kuyumcu_infrastructure.Services;
//using kuyumcu_application.Abstractions;
//using Kuyumcu.PriceService.Services;
//using Kuyumcu.PriceService.Models;
//using kuyumcu_infrastructure.Tenancy;

//var builder = WebApplication.CreateBuilder(args);
//var cfg = builder.Configuration;

//// ****************************** 1. SERV�S TANIMLAMALARI ******************************

//// Logging
//builder.Logging.ClearProviders();
//builder.Logging.AddConsole();

//// DbContext
//builder.Services.AddDbContext<AppDbContext>(opt =>
//    opt.UseSqlServer(cfg.GetConnectionString("SqlServer")));

//// App services
//builder.Services.AddScoped<IAuthService, AuthService>();
//builder.Services.AddScoped<ICustomerService, CustomerService>();
//// Kuyumcu.PriceService.Models namespace'indeki PriceCache ve GoldApiClient'i kullanmak i�in
//// Sadece bir tanesi kald�.
//// builder.Services.AddScoped<kuyumcu_infrastructure.Tenancy.TenantContext>(); // Tekrarl�yd�, kald�r�ld�

//// Http + CORS
//builder.Services.AddHttpClient();
//builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

//// ---------------- GoldAPI Feed ----------------
//// Tekrarl� kay�tlar temizlendi.
//builder.Services.AddSingleton<PriceCache>();
//builder.Services.AddHttpClient<GoldApiClient>();
//builder.Services.AddSingleton<GoldPriceService>();
//builder.Services.AddHostedService<GoldPriceBackgroundRefresher>();
//// builder.Services.AddScoped<TenantContext>(); // Tekrarl�yd�, kald�r�ld�

//// Yetkilendirme Politikalar�
//builder.Services.AddAuthorization(opts =>
//{
//    opts.AddPolicy("BranchAdmin", p => p.RequireRole("Owner", "Manager"));
//});

//// Controllers
//builder.Services.AddControllers();

//// Stock 
//builder.Services.AddScoped<IStockService, StockService>();

//// JWT Authentication �emas�
//builder.Services
//    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
//    .AddJwtBearer(o =>
//    {
//        o.TokenValidationParameters = new TokenValidationParameters
//        {
//            ValidateIssuer = true,
//            ValidateAudience = true,
//            ValidateIssuerSigningKey = true,
//            ValidIssuer = cfg["Jwt:Issuer"],
//            ValidAudience = cfg["Jwt:Audience"],
//            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!))
//        };
//    });
//builder.Services.AddAuthorization(); // Authorization hizmetini etkinle�tir

//// Tenant Management
//builder.Services.AddHttpContextAccessor();
//// Tenant Context'in JWT veya Header'dan ID okumas�n� sa�la
//builder.Services.AddScoped<ITenantContext>(sp =>
//{
//    var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;

//    // Attempt to get TenantId from JWT Claims first
//    var tenantStr = http?.User?.FindFirst("tenant_id")?.Value;

//    // Fallback to Header if not found in claims
//    if (string.IsNullOrWhiteSpace(tenantStr))
//    {
//        tenantStr = http?.Request.Headers["X-Tenant-Id"].FirstOrDefault();
//    }

//    // Tasar�m zaman� veya bo� durum i�in fallback
//    if (string.IsNullOrWhiteSpace(tenantStr) || !Guid.TryParse(tenantStr, out var tenantId))
//    {
//        tenantId = Guid.Parse("00000000-0000-0000-0000-000000000001"); // sabit DefaultTenant
//    }

//    return new TenantContext { TenantId = tenantId };
//});

//// Swagger
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen(c =>
//{
//    // API Key Tan�m�
//    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
//    {
//        In = ParameterLocation.Header,
//        Name = cfg["Auth:HeaderName"] ?? "x-app-key",
//        Type = SecuritySchemeType.ApiKey,
//        Description = "Uygulama anahtar�"
//    });
//    // JWT Bearer Tan�m�
//    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
//    {
//        Name = "Authorization",
//        Type = SecuritySchemeType.Http,
//        Scheme = "bearer",
//        BearerFormat = "JWT",
//        In = ParameterLocation.Header,
//        Description = "Bearer {token}"
//    });
//    // G�venlik Gereksinimleri
//    c.AddSecurityRequirement(new OpenApiSecurityRequirement
//    {
//        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="ApiKey"}}, Array.Empty<string>() },
//        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="Bearer"}}, Array.Empty<string>() }
//    });
//});

//var app = builder.Build();

//// ****************************** 2. REQUEST PIPELINE ******************************

//// DB migrate + seed (Tek bir noktada topland�)
//using (var scope = app.Services.CreateScope())
//{
//    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
//    // DbInitializer'�n namespace'i ile �a�r�lmas� (CS0103 hatas�n� ��zer)
//    await DbInitializer.EnsureSeedAsync(db, configuration);
//}

//// CORS + Swagger
//app.UseCors();
//app.UseSwagger();
//app.UseSwaggerUI();

//// 1. Custom API-Key middleware (JWT'den �nce �al���r)
//app.Use(async (ctx, next) =>
//{
//    var p = ctx.Request.Path.Value ?? "";
//    // Swagger, /api/auth ve /ping endpoint'lerini serbest b�rak
//    if (p.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
//        p.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
//        p.Equals("/ping", StringComparison.OrdinalIgnoreCase))
//    { await next(); return; }

//    var headerName = cfg["Auth:HeaderName"] ?? "x-app-key";
//    var allowed = cfg.GetSection("Auth:AllowedKeys").Get<string[]>() ?? Array.Empty<string>();

//    if (!ctx.Request.Headers.TryGetValue(headerName, out var key) ||
//        !allowed.Contains(key.ToString(), StringComparer.Ordinal))
//    {
//        ctx.Response.StatusCode = 401;
//        // ctx.Response.WriteAsync'in do�ru �ekilde �a�r�lmas�
//        await ctx.Response.WriteAsync("Unauthorized: Missing or invalid App Key");
//        return;
//    }

//    await next();
//});


//// Standart ASP.NET Core s�ralamas�:
//// 2. Authentication (JWT Token'� do�rular)
//app.UseAuthentication();
//// Not: Duplicate UseAuthentication �a�r�s� kald�r�ld�.

//// 3. Tenant Middleware (JWT�den User.Claims dolduktan SONRA tenant�� doldurur)
//app.UseMiddleware<kuyumcu_infrastructure.Tenancy.TenantMiddleware>();

//// 4. Authorization (Claims'e g�re [Authorize] attribute'lerini kontrol eder)
//// Not: Duplicate UseAuthorization �a�r�s� kald�r�ld�.
//app.UseAuthorization();


//app.MapGet("/ping", () => Results.Ok("pong"));
//app.MapControllers();

//// Son veriler
//app.MapGet("/gold/latest", (GoldPriceService svc) =>
//{
//    var list = svc.LatestOrEmpty();
//    return Results.Ok(list);
//});

//app.MapPost("/gold/refresh", async (GoldPriceService svc, CancellationToken ct) =>
//{
//    var list = await svc.RefreshAsync(ct);
//    return Results.Ok(list);
//});
//app.MapGet("/gold/fx-debug", async (GoldApiClient cli, CancellationToken ct) =>
//{
//    var (u, e, eu) = await cli.FetchFxAsync(ct);
//    return Results.Ok(new { usdtry = u, eurtry = e, eurusd = eu });
//});

//app.Run();