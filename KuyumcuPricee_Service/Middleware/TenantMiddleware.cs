using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace kuyumcu_infrastructure.Tenancy
{
    /// <summary>
    /// Her istek için TenantContext’i doldurur:
    /// 1) JWT claim (tenant_id / branch_id)
    /// 2) Header (X-Tenant-Id / X-Branch-Id)
    /// 3) (opsiyonel) appsettings Tenancy:DefaultTenantId fallback
    /// </summary>
    public sealed class TenantMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _config;

        public TenantMiddleware(RequestDelegate next, IConfiguration config)
        {
            _next = next;
            _config = config;
        }

        public async Task Invoke(HttpContext ctx, ITenantContext tenant)
        {
            // HttpContext yoksa (çok nadir) middleware'i pas geç
            if (ctx == null)
            {
                await _next(ctx);
                return;
            }

            // 1) JWT Claims
            var user = ctx.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                tenant.TenantId = GetGuidClaim(user, "tenant_id", "tenantId", "tid") ?? tenant.TenantId;
                tenant.BranchId = GetGuidClaim(user, "branch_id", "branchId", "bid") ?? tenant.BranchId;
            }

            // 2) Header
            if (tenant.TenantId == Guid.Empty &&
                ctx.Request.Headers.TryGetValue("X-Tenant-Id", out var tHdr) &&
                Guid.TryParse(tHdr.ToString(), out var tGuid))
            {
                tenant.TenantId = tGuid;
            }

            // 3) Fallback: appsettings Tenancy:DefaultTenantId
            if (tenant.TenantId == Guid.Empty)
            {
                var allowFallback = string.Equals(
                    _config["Tenancy:AllowDefaultTenantFallback"],
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                if (allowFallback)
                {
                    var defaultId = _config["Tenancy:DefaultTenantId"];
                    if (Guid.TryParse(defaultId, out var defaultGuid))
                        tenant.TenantId = defaultGuid;
                }
            }

            if (tenant.BranchId is null &&
                ctx.Request.Headers.TryGetValue("X-Branch-Id", out var bHdr) &&
                Guid.TryParse(bHdr.ToString(), out var bGuid))
            {
                tenant.BranchId = bGuid;
            }

            // Tenant yoksa isteği reddet (swagger/ping/auth hariç).
            if (tenant.TenantId == Guid.Empty && RequiresTenant(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Tenant bilgisi bulunamadı. JWT claim (tenant_id) veya X-Tenant-Id header zorunludur."
                });
                return;
            }

            await _next(ctx);
        }

        private static bool RequiresTenant(HttpContext ctx)
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.Equals("/ping", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase)) return false;
            if (HttpMethods.IsOptions(ctx.Request.Method)) return false;
            return true;
        }

        private static Guid? GetGuidClaim(ClaimsPrincipal user, params string[] names)
        {
            foreach (var n in names)
            {
                var v = user.FindFirstValue(n);
                if (Guid.TryParse(v, out var g)) return g;
            }
            return null;
        }
    }
}


//using Microsoft.AspNetCore.Http; // HttpContext ve RequestDelegate için eklendi
//using System.Security.Claims;
//using System; // Guid için eklendi
//using System.Linq; // LINQ için (Find, FirstOrDefault)

//namespace kuyumcu_infrastructure.Tenancy
//{
//    // TenantContext sınıfı: ITenantContext arayüzünü uygulayan somut sınıf
//    // Bu, ITenantContext olarak kaydedilen servisin gerçek implementasyonudur.
//    public interface ITenantContext
//    {
//        Guid TenantId { get; set; }
//        Guid? BranchId { get; set; } // Şube (Branch) ID'si de eklendi
//    }

//    // Uygulama içinde kullanılacak somut TenantContext
//    public class TenantContext : ITenantContext
//    {
//        public Guid TenantId { get; set; } = Guid.Empty;
//        public Guid? BranchId { get; set; } = null;
//    }

//    /// <summary>
//    /// Her istek için TenantContext’i doldurur:
//    /// 1) JWT claim (tenant_id / branch_id)
//    /// 2) Header (X-Tenant-Id / X-Branch-Id)
//    /// </summary>
//    public sealed class TenantMiddleware
//    {
//        private readonly RequestDelegate _next;

//        public TenantMiddleware(RequestDelegate next) => _next = next;

//        // KRİTİK DÜZELTME: Invoke metodu imzasındaki somut sınıf (TenantContext)
//        // arayüzü (ITenantContext) ile değiştirildi.
//        // Bu, Program.cs'teki AddScoped kaydıyla uyumluluğu sağlar.
//        public async Task Invoke(HttpContext ctx, ITenantContext tenant)
//        {
//            // 1) JWT claims
//            var user = ctx.User;
//            if (user?.Identity?.IsAuthenticated == true)
//            {
//                // birden fazla olası claim adı destekleyelim
//                tenant.TenantId = GetGuidClaim(user,
//                    "tenant_id", "tenantId", "tid") ?? tenant.TenantId;

//                tenant.BranchId = GetGuidClaim(user,
//                    "branch_id", "branchId", "bid") ?? tenant.BranchId;
//            }

//            // 2) Header override (isteğe bağlı)
//            // Eğer JWT'den alınamadıysa (TenantId halen Guid.Empty ise) veya Header'da override varsa
//            if (tenant.TenantId == Guid.Empty &&
//                ctx.Request.Headers.TryGetValue("X-Tenant-Id", out var tHdr) &&
//                Guid.TryParse(tHdr.ToString(), out var tGuid))
//            {
//                tenant.TenantId = tGuid;
//            }

//            if (tenant.BranchId is null &&
//                ctx.Request.Headers.TryGetValue("X-Branch-Id", out var bHdr) &&
//                Guid.TryParse(bHdr.ToString(), out var bGuid))
//            {
//                tenant.BranchId = bGuid;
//            }

//            await _next(ctx);
//        }

//        private static Guid? GetGuidClaim(ClaimsPrincipal user, params string[] names)
//        {
//            foreach (var n in names)
//            {
//                var v = user.FindFirstValue(n);
//                if (Guid.TryParse(v, out var g)) return g;
//            }
//            return null;
//        }
//    }
//}
//----------------------------------------------------------------------------------------------------------

//using System.Net.Http;
//using System.Security.Claims;


//namespace kuyumcu_infrastructure.Tenancy
//{
//    /// <summary>
//    /// Her istek için TenantContext’i doldurur:
//    /// 1) JWT claim (tenant_id / branch_id)
//    /// 2) Header (X-Tenant-Id / X-Branch-Id)
//    /// </summary>
//    public sealed class TenantMiddleware
//    {
//        private readonly RequestDelegate _next;

//        public TenantMiddleware(RequestDelegate next) => _next = next;

//        public async Task Invoke(HttpContext ctx, TenantContext tenant)
//        {
//            // 1) JWT claims
//            var user = ctx.User;
//            if (user?.Identity?.IsAuthenticated == true)
//            {
//                // birden fazla olası claim adı destekleyelim
//                tenant.TenantId = GetGuidClaim(user,
//                    "tenant_id", "tenantId", "tid") ?? tenant.TenantId;

//                tenant.BranchId = GetGuidClaim(user,
//                    "branch_id", "branchId", "bid") ?? tenant.BranchId;
//            }

//            // 2) Header override (isteğe bağlı)
//            if (tenant.TenantId == Guid.Empty &&
//                ctx.Request.Headers.TryGetValue("X-Tenant-Id", out var tHdr) &&
//                Guid.TryParse(tHdr.ToString(), out var tGuid))
//            {
//                tenant.TenantId = tGuid;
//            }

//            if (tenant.BranchId is null &&
//                ctx.Request.Headers.TryGetValue("X-Branch-Id", out var bHdr) &&
//                Guid.TryParse(bHdr.ToString(), out var bGuid))
//            {
//                tenant.BranchId = bGuid;
//            }

//            await _next(ctx);
//        }

//        private static Guid? GetGuidClaim(ClaimsPrincipal user, params string[] names)
//        {
//            foreach (var n in names)
//            {
//                var v = user.FindFirstValue(n);
//                if (Guid.TryParse(v, out var g)) return g;
//            }
//            return null;
//        }
//    }
//}
