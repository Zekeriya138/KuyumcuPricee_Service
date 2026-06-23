using KUYUMCU.Price_Service.Services;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Middleware;

public sealed class BranchSubscriptionMiddleware
{
    private readonly RequestDelegate _next;

    public BranchSubscriptionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(
        HttpContext ctx,
        ITenantContext tenant,
        IBranchSubscriptionService subscriptionService,
        IConfiguration cfg)
    {
        var enforcementEnabled = cfg.GetValue<bool?>("Subscriptions:EnforcementEnabled") ?? true;
        if (!enforcementEnabled)
        {
            await _next(ctx);
            return;
        }

        if (!RequiresSubscriptionCheck(ctx))
        {
            await _next(ctx);
            return;
        }

        var tenantId = tenant.TenantId;
        var branchId = tenant.BranchId;
        if (tenantId == Guid.Empty || !branchId.HasValue || branchId.Value == Guid.Empty)
        {
            await _next(ctx);
            return;
        }

        var access = await subscriptionService.GetAccessAsync(tenantId, branchId.Value, ctx.RequestAborted);
        if (!access.IsActive)
        {
            ctx.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Seçili şube için aktif abonelik bulunamadı. Lütfen aboneliği yenileyin.",
                code = "subscription_required",
                expiresAtUtc = access.EndsAtUtc,
                plan = access.PlanLabel
            });
            return;
        }

        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/einvoice", StringComparison.OrdinalIgnoreCase) && !access.IncludesEInvoice)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Bu pakette E-Fatura modülü bulunmuyor.",
                code = "feature_not_in_plan"
            });
            return;
        }

        if (path.StartsWith("/api/ai", StringComparison.OrdinalIgnoreCase) && !access.IncludesAiAssistant)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Bu pakette Kuyumcu AI Asistan modülü bulunmuyor.",
                code = "feature_not_in_plan"
            });
            return;
        }

        await _next(ctx);
    }

    private static bool RequiresSubscriptionCheck(HttpContext ctx)
    {
        if (HttpMethods.IsOptions(ctx.Request.Method))
            return false;
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Equals("/ping", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("/auth", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("/api/users", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("/api/branches", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("/api/subscriptions", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.StartsWith("/api/einvoice/webhook", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
