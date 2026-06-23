using System.Globalization;
using System.Security.Claims;
using Iyzipay;
using Iyzipay.Model;
using Iyzipay.Request;
using KUYUMCU.Price_Service.Services;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class SubscriptionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IConfiguration _cfg;
    private readonly IBranchSubscriptionService _subscriptionService;

    public SubscriptionsController(
        AppDbContext db,
        ITenantContext tenant,
        IConfiguration cfg,
        IBranchSubscriptionService subscriptionService)
    {
        _db = db;
        _tenant = tenant;
        _cfg = cfg;
        _subscriptionService = subscriptionService;
    }

    [HttpGet("branches")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> GetBranchSubscriptions(CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var branches = await _db.Branches
            .AsNoTracking()
            .Where(x => x.TenantId == tid && !x.IsDeleted)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.IsActive })
            .ToListAsync(ct);

        var latestByBranch = await _db.BranchSubscriptions
            .AsNoTracking()
            .Where(x => x.TenantId == tid && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var map = latestByBranch
            .GroupBy(x => x.BranchId)
            .ToDictionary(g => g.Key, g => g.First());

        var rows = new List<object>(branches.Count);
        foreach (var branch in branches)
        {
            map.TryGetValue(branch.Id, out var sub);
            var access = await _subscriptionService.GetAccessAsync(tid, branch.Id, ct);
            rows.Add(new
            {
                branchId = branch.Id,
                branchName = branch.Name,
                branchIsActive = branch.IsActive,
                subscription = sub is null ? null : new
                {
                    sub.Id,
                    periodType = sub.PeriodType.ToString(),
                    packageType = sub.PackageType.ToString(),
                    status = sub.Status.ToString(),
                    sub.IsLifetime,
                    sub.Price,
                    sub.Currency,
                    sub.StartsAtUtc,
                    sub.EndsAtUtc,
                    sub.IncludesEInvoice,
                    sub.IncludesAiAssistant,
                    sub.IyzipayConversationId,
                    sub.CreatedAt
                },
                access
            });
        }

        return Ok(rows);
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent([FromQuery] Guid? branchId, CancellationToken ct)
    {
        var bid = branchId ?? _tenant.BranchId;
        if (!bid.HasValue || bid.Value == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var access = await _subscriptionService.GetAccessAsync(_tenant.TenantId, bid.Value, ct);
        return Ok(access);
    }

    [HttpPost("start-payment")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> StartPayment([FromBody] StartSubscriptionPaymentReq req, CancellationToken ct)
    {
        if (req.BranchId == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var tid = _tenant.TenantId;
        var branch = await _db.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == req.BranchId && !x.IsDeleted, ct);
        if (branch is null)
            return NotFound(new { error = "Şube bulunamadı." });

        if (!TryMapPlan(req.PlanCode, out var plan))
            return BadRequest(new { error = "Geçersiz plan kodu." });

        if (!TryGetIyzipayOptions(out var options, out var optionsError))
            return BadRequest(new { error = optionsError });

        var price = ResolvePrice(req.PlanCode, plan.DefaultPrice);
        var conversationId = Guid.NewGuid().ToString("N");
        var callbackBase = (_cfg["Subscriptions:Iyzico:CallbackUrl"] ?? "").Trim();
        var callbackUrl = string.IsNullOrWhiteSpace(callbackBase)
            ? $"{Request.Scheme}://{Request.Host}/api/subscriptions/iyzico/callback"
            : callbackBase.TrimEnd('/');
        callbackUrl += $"?tenantId={tid}&branchId={req.BranchId}&conversationId={conversationId}";

        var pending = new BranchSubscription
        {
            TenantId = tid,
            BranchId = req.BranchId,
            PeriodType = plan.PeriodType,
            PackageType = plan.PackageType,
            Status = SubscriptionStatus.PendingPayment,
            IsLifetime = plan.IsLifetime,
            IncludesEInvoice = plan.IncludesEInvoice,
            IncludesAiAssistant = plan.IncludesAiAssistant,
            Price = price,
            Currency = "TRY",
            IyzipayConversationId = conversationId,
            Note = $"Plan: {req.PlanCode}"
        };
        _db.BranchSubscriptions.Add(pending);
        await _db.SaveChangesAsync(ct);

        var paymentReq = BuildCheckoutRequest(req, branch.Name, callbackUrl, conversationId, price);
        var checkout = await CheckoutFormInitialize.Create(paymentReq, options);
        if (!string.Equals(checkout.Status, "success", StringComparison.OrdinalIgnoreCase))
        {
            pending.Status = SubscriptionStatus.Failed;
            pending.IyzipayStatus = checkout.Status;
            pending.IyzipayRawResponse = checkout.ErrorMessage;
            await _db.SaveChangesAsync(ct);
            return BadRequest(new
            {
                error = "İyzico ödeme başlatılamadı.",
                detail = checkout.ErrorMessage,
                errorCode = checkout.ErrorCode
            });
        }

        pending.IyzipayStatus = checkout.Status;
        pending.IyzipayToken = checkout.Token;
        pending.IyzipayRawResponse = checkout.CheckoutFormContent;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            subscriptionId = pending.Id,
            conversationId,
            token = checkout.Token,
            paymentPageUrl = checkout.PaymentPageUrl,
            checkoutFormContent = checkout.CheckoutFormContent
        });
    }

    [AllowAnonymous]
    [HttpPost("iyzico/callback")]
    public async Task<IActionResult> IyzipayCallback(
        [FromQuery] Guid tenantId,
        [FromQuery] Guid branchId,
        [FromQuery] string conversationId,
        CancellationToken ct)
    {
        if (tenantId == Guid.Empty || branchId == Guid.Empty || string.IsNullOrWhiteSpace(conversationId))
            return BadRequest("Eksik callback parametresi.");

        var token = Request.HasFormContentType
            ? (Request.Form["token"].FirstOrDefault() ?? "")
            : (Request.Query["token"].FirstOrDefault() ?? "");
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest("Token bulunamadı.");

        if (!TryGetIyzipayOptions(out var options, out var optionsError))
            return BadRequest(optionsError);

        var retrieveReq = new RetrieveCheckoutFormRequest
        {
            Locale = Locale.TR.ToString(),
            ConversationId = conversationId,
            Token = token
        };
        var checkout = await CheckoutForm.Retrieve(retrieveReq, options);

        var subscription = await _db.BranchSubscriptions
            .Where(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.IyzipayConversationId == conversationId &&
                !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (subscription is null)
            return NotFound("Abonelik kaydı bulunamadı.");

        subscription.IyzipayStatus = checkout.Status;
        subscription.IyzipayToken = token;
        subscription.IyzipayPaymentId = checkout.PaymentId;
        subscription.IyzipayRawResponse = checkout.PaymentStatus;
        subscription.LastCheckedAtUtc = DateTime.UtcNow;

        if (string.Equals(checkout.Status, "success", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(checkout.PaymentStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            await ExpireOtherActiveSubscriptionsAsync(tenantId, branchId, subscription.Id, ct);
            ActivateSubscription(subscription, DateTime.UtcNow);
        }
        else
        {
            subscription.Status = SubscriptionStatus.Failed;
        }

        await _db.SaveChangesAsync(ct);
        return Content(
            string.Equals(subscription.Status.ToString(), SubscriptionStatus.Active.ToString(), StringComparison.OrdinalIgnoreCase)
                ? "Odeme alindi, abonelik aktiflestirildi."
                : "Odeme dogrulanamadi.",
            "text/plain");
    }

    private async Task ExpireOtherActiveSubscriptionsAsync(Guid tenantId, Guid branchId, Guid exceptId, CancellationToken ct)
    {
        var rows = await _db.BranchSubscriptions
            .Where(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.Id != exceptId &&
                x.Status == SubscriptionStatus.Active &&
                !x.IsDeleted)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = SubscriptionStatus.Expired;
        }
    }

    private static void ActivateSubscription(BranchSubscription sub, DateTime utcNow)
    {
        sub.Status = SubscriptionStatus.Active;
        sub.LastPaymentAtUtc = utcNow;
        sub.StartsAtUtc = utcNow;
        if (sub.IsLifetime)
        {
            sub.EndsAtUtc = null;
            return;
        }

        sub.EndsAtUtc = sub.PeriodType switch
        {
            SubscriptionPeriodType.Yearly => utcNow.AddYears(1),
            SubscriptionPeriodType.Monthly => utcNow.AddMonths(1),
            _ => utcNow.AddYears(1)
        };
    }

    private CreateCheckoutFormInitializeRequest BuildCheckoutRequest(
        StartSubscriptionPaymentReq req,
        string branchName,
        string callbackUrl,
        string conversationId,
        decimal price)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? Guid.NewGuid().ToString("N");
        var username = User.FindFirstValue(ClaimTypes.Name) ?? "kullanici";
        var p = price.ToString("0.00", CultureInfo.InvariantCulture);
        return new CreateCheckoutFormInitializeRequest
        {
            Locale = Locale.TR.ToString(),
            ConversationId = conversationId,
            Price = p,
            PaidPrice = p,
            Currency = Currency.TRY.ToString(),
            BasketId = req.BranchId.ToString("N"),
            PaymentGroup = PaymentGroup.PRODUCT.ToString(),
            CallbackUrl = callbackUrl,
            Buyer = new Buyer
            {
                Id = userId,
                Name = username,
                Surname = "User",
                GsmNumber = "+905000000000",
                Email = "abonelik@kuyumcupro.local",
                IdentityNumber = "11111111111",
                LastLoginDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                RegistrationDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                RegistrationAddress = "Merkez",
                Ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1",
                City = "Istanbul",
                Country = "Turkey",
                ZipCode = "34000"
            },
            ShippingAddress = new Address
            {
                ContactName = branchName,
                City = "Istanbul",
                Country = "Turkey",
                Description = "Sube abonelik odemesi",
                ZipCode = "34000"
            },
            BillingAddress = new Address
            {
                ContactName = branchName,
                City = "Istanbul",
                Country = "Turkey",
                Description = "Sube abonelik odemesi",
                ZipCode = "34000"
            },
            BasketItems = new List<BasketItem>
            {
                new()
                {
                    Id = req.BranchId.ToString("N"),
                    Name = $"Abonelik - {req.PlanCode}",
                    Category1 = "Subscription",
                    ItemType = BasketItemType.VIRTUAL.ToString(),
                    Price = p
                }
            }
        };
    }

    private bool TryGetIyzipayOptions(out Options options, out string error)
    {
        var apiKey = (_cfg["Subscriptions:Iyzico:ApiKey"] ?? "").Trim();
        var secretKey = (_cfg["Subscriptions:Iyzico:SecretKey"] ?? "").Trim();
        var baseUrl = (_cfg["Subscriptions:Iyzico:BaseUrl"] ?? "https://sandbox-api.iyzipay.com").Trim();
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            options = new Options();
            error = "İyzico ayarları eksik. Subscriptions:Iyzico:ApiKey ve SecretKey doldurulmalıdır.";
            return false;
        }

        options = new Options
        {
            ApiKey = apiKey,
            SecretKey = secretKey,
            BaseUrl = baseUrl
        };
        error = "";
        return true;
    }

    private decimal ResolvePrice(string planCode, decimal fallback)
    {
        var configPrice = _cfg.GetValue<decimal?>($"Subscriptions:Plans:{planCode}:Price");
        if (configPrice.HasValue && configPrice.Value > 0)
            return decimal.Round(configPrice.Value, 2, MidpointRounding.AwayFromZero);
        return decimal.Round(fallback, 2, MidpointRounding.AwayFromZero);
    }

    private static bool TryMapPlan(string? planCode, out SubscriptionPlanDefinition plan)
    {
        var code = (planCode ?? "").Trim().ToLowerInvariant();
        switch (code)
        {
            case "turnkey-full":
                plan = new SubscriptionPlanDefinition(SubscriptionPeriodType.Turnkey, SubscriptionPackageType.Full, true, true, true, 250000m);
                return true;
            case "yearly-full":
                plan = new SubscriptionPlanDefinition(SubscriptionPeriodType.Yearly, SubscriptionPackageType.Full, false, true, true, 35000m);
                return true;
            case "yearly-standard":
                plan = new SubscriptionPlanDefinition(SubscriptionPeriodType.Yearly, SubscriptionPackageType.Standard, false, false, false, 24000m);
                return true;
            case "monthly-full":
                plan = new SubscriptionPlanDefinition(SubscriptionPeriodType.Monthly, SubscriptionPackageType.Full, false, true, true, 3500m);
                return true;
            case "monthly-standard":
                plan = new SubscriptionPlanDefinition(SubscriptionPeriodType.Monthly, SubscriptionPackageType.Standard, false, false, false, 2400m);
                return true;
            default:
                plan = default;
                return false;
        }
    }

    public sealed record StartSubscriptionPaymentReq(Guid BranchId, string PlanCode);

    private readonly record struct SubscriptionPlanDefinition(
        SubscriptionPeriodType PeriodType,
        SubscriptionPackageType PackageType,
        bool IsLifetime,
        bool IncludesEInvoice,
        bool IncludesAiAssistant,
        decimal DefaultPrice);
}
