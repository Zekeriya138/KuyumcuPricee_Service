using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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
using kuyumcu_infrastructure.Tenancy;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ****************************** 1. SERVÝS TANIMLAMALARI ******************************

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// DbContext
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(cfg.GetConnectionString("SqlServer")));

// Http + CORS
builder.Services.AddHttpClient();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));


// ---------------- GOLD API Feed ----------------
builder.Services.AddSingleton<PriceCache>();
builder.Services.AddHttpClient<GoldApiClient>();
builder.Services.AddSingleton<GoldPriceService>();
builder.Services.AddHostedService<GoldPriceBackgroundRefresher>();


// ... (Satýr 75 civarý)

// ---------------- TENANT MANAGEMENT (GÜNCELLENDÝ) ----------------

// ---------------- TENANT MANAGEMENT (DÜZELTÝLDÝ) ----------------
builder.Services.AddHttpContextAccessor();

// Tek bir TenantContext oluþtur (Scoped)
builder.Services.AddScoped<TenantContext>();

// ITenantContext istendiðinde ayný instance dönsün
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
// ---------------- END TENANT MANAGEMENT ----------------

//builder.Services.AddHttpContextAccessor();

//// Adým 1: Somut TenantContext sýnýfýný kaydet ve tüm lojiði buraya taþý
//// Bu, ITenantContext'i DI'dan isteyen her þeyin doðru yapýlandýrmayý almasýný saðlar.
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

//// Adým 2: ITenantContext arayüzü istendiðinde, somut sýnýfý döndür.
//// Bu, TenantContext'in Fabrika Lojiði ile oluþturulduðunu garanti eder.
//builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

// ---------------- END TENANT MANAGEMENT ----------------


// App services (Artýk ITenantContext'i güvenle çözebilirler)
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ICustomerService, CustomerService>();
builder.Services.AddScoped<IStockService, StockService>(); // Stock 

// Controllers
builder.Services.AddControllers();


// Yetkilendirme Politikalarý
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("BranchAdmin", p => p.RequireRole("Owner", "Manager"));
});


// JWT Authentication Þemasý
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!))
        };
    });
builder.Services.AddAuthorization();


// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // API Key Tanýmý
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Name = cfg["Auth:HeaderName"] ?? "x-app-key",
        Type = SecuritySchemeType.ApiKey,
        Description = "Uygulama anahtarý"
    });
    // JWT Bearer Tanýmý
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Bearer {token}"
    });
    // Güvenlik Gereksinimleri
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="ApiKey"}}, Array.Empty<string>() },
        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="Bearer"}}, Array.Empty<string>() }
    });
});

var app = builder.Build();

// ****************************** 2. REQUEST PIPELINE ******************************

// DB migrate + seed (Tek bir noktada toplandý)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await DbInitializer.EnsureSeedAsync(db, configuration);
}

// CORS + Swagger
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();

// 1. Custom API-Key middleware (JWT'den önce çalýþýr)
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path.Value ?? "";
    // Swagger, /api/auth ve /ping endpoint'lerini serbest býrak
    if (p.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
        p.Equals("/ping", StringComparison.OrdinalIgnoreCase))
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


// Standart ASP.NET Core sýralamasý:
// 2. Authentication (JWT Token'ý doðrular)
app.UseAuthentication();

// 3. Tenant Middleware (JWT’den User.Claims dolduktan SONRA tenant’ý doldurur)
app.UseMiddleware<kuyumcu_infrastructure.Tenancy.TenantMiddleware>();

// 4. Authorization (Claims'e göre [Authorize] attribute'lerini kontrol eder)
app.UseAuthorization();


app.MapGet("/ping", () => Results.Ok("pong"));
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
app.MapGet("/gold/fx-debug", async (GoldApiClient cli, CancellationToken ct) =>
{
    var (u, e, eu) = await cli.FetchFxAsync(ct);
    return Results.Ok(new { usdtry = u, eurtry = e, eurusd = eu });
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
//using Microsoft.AspNetCore.Builder; // WebApplicationBuilder için
//using Microsoft.AspNetCore.Hosting; // Hosting Environment için

//// domain/app/infra
//using kuyumcu_infrastructure.Persistence;
//using kuyumcu_infrastructure.Services;
//using kuyumcu_application.Abstractions;
//using Kuyumcu.PriceService.Services;
//using Kuyumcu.PriceService.Models;
//using kuyumcu_infrastructure.Tenancy;

//var builder = WebApplication.CreateBuilder(args);
//var cfg = builder.Configuration;

//// ****************************** 1. SERVÝS TANIMLAMALARI ******************************

//// Logging
//builder.Logging.ClearProviders();
//builder.Logging.AddConsole();

//// DbContext
//builder.Services.AddDbContext<AppDbContext>(opt =>
//    opt.UseSqlServer(cfg.GetConnectionString("SqlServer")));

//// App services
//builder.Services.AddScoped<IAuthService, AuthService>();
//builder.Services.AddScoped<ICustomerService, CustomerService>();
//// Kuyumcu.PriceService.Models namespace'indeki PriceCache ve GoldApiClient'i kullanmak için
//// Sadece bir tanesi kaldý.
//// builder.Services.AddScoped<kuyumcu_infrastructure.Tenancy.TenantContext>(); // Tekrarlýydý, kaldýrýldý

//// Http + CORS
//builder.Services.AddHttpClient();
//builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

//// ---------------- GoldAPI Feed ----------------
//// Tekrarlý kayýtlar temizlendi.
//builder.Services.AddSingleton<PriceCache>();
//builder.Services.AddHttpClient<GoldApiClient>();
//builder.Services.AddSingleton<GoldPriceService>();
//builder.Services.AddHostedService<GoldPriceBackgroundRefresher>();
//// builder.Services.AddScoped<TenantContext>(); // Tekrarlýydý, kaldýrýldý

//// Yetkilendirme Politikalarý
//builder.Services.AddAuthorization(opts =>
//{
//    opts.AddPolicy("BranchAdmin", p => p.RequireRole("Owner", "Manager"));
//});

//// Controllers
//builder.Services.AddControllers();

//// Stock 
//builder.Services.AddScoped<IStockService, StockService>();

//// JWT Authentication Þemasý
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
//builder.Services.AddAuthorization(); // Authorization hizmetini etkinleþtir

//// Tenant Management
//builder.Services.AddHttpContextAccessor();
//// Tenant Context'in JWT veya Header'dan ID okumasýný saðla
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

//    // Tasarým zamaný veya boþ durum için fallback
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
//    // API Key Tanýmý
//    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
//    {
//        In = ParameterLocation.Header,
//        Name = cfg["Auth:HeaderName"] ?? "x-app-key",
//        Type = SecuritySchemeType.ApiKey,
//        Description = "Uygulama anahtarý"
//    });
//    // JWT Bearer Tanýmý
//    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
//    {
//        Name = "Authorization",
//        Type = SecuritySchemeType.Http,
//        Scheme = "bearer",
//        BearerFormat = "JWT",
//        In = ParameterLocation.Header,
//        Description = "Bearer {token}"
//    });
//    // Güvenlik Gereksinimleri
//    c.AddSecurityRequirement(new OpenApiSecurityRequirement
//    {
//        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="ApiKey"}}, Array.Empty<string>() },
//        { new OpenApiSecurityScheme{Reference=new OpenApiReference{Type=ReferenceType.SecurityScheme,Id="Bearer"}}, Array.Empty<string>() }
//    });
//});

//var app = builder.Build();

//// ****************************** 2. REQUEST PIPELINE ******************************

//// DB migrate + seed (Tek bir noktada toplandý)
//using (var scope = app.Services.CreateScope())
//{
//    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
//    // DbInitializer'ýn namespace'i ile çaðrýlmasý (CS0103 hatasýný çözer)
//    await DbInitializer.EnsureSeedAsync(db, configuration);
//}

//// CORS + Swagger
//app.UseCors();
//app.UseSwagger();
//app.UseSwaggerUI();

//// 1. Custom API-Key middleware (JWT'den önce çalýþýr)
//app.Use(async (ctx, next) =>
//{
//    var p = ctx.Request.Path.Value ?? "";
//    // Swagger, /api/auth ve /ping endpoint'lerini serbest býrak
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
//        // ctx.Response.WriteAsync'in doðru þekilde çaðrýlmasý
//        await ctx.Response.WriteAsync("Unauthorized: Missing or invalid App Key");
//        return;
//    }

//    await next();
//});


//// Standart ASP.NET Core sýralamasý:
//// 2. Authentication (JWT Token'ý doðrular)
//app.UseAuthentication();
//// Not: Duplicate UseAuthentication çaðrýsý kaldýrýldý.

//// 3. Tenant Middleware (JWT’den User.Claims dolduktan SONRA tenant’ý doldurur)
//app.UseMiddleware<kuyumcu_infrastructure.Tenancy.TenantMiddleware>();

//// 4. Authorization (Claims'e göre [Authorize] attribute'lerini kontrol eder)
//// Not: Duplicate UseAuthorization çaðrýsý kaldýrýldý.
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