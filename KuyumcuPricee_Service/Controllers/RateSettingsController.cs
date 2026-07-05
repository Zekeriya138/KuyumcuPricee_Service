using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Kuyumcu.PriceService.Services;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class RateSettingsController : ControllerBase
{
    private static readonly HashSet<string> HaremParityCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "XAU_KG_USD",
        "XAU_KG_EUR"
    };

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly GoldPriceService _goldPriceService;

    public RateSettingsController(AppDbContext db, ITenantContext tenant, GoldPriceService goldPriceService)
    {
        _db = db;
        _tenant = tenant;
        _goldPriceService = goldPriceService;
    }

    public sealed record RateSettingRowDto(
        string Code,
        string Display,
        bool IsVisible,
        decimal BidTlOffset,
        decimal AskTlOffset,
        string? CustomDisplay,
        decimal CurrentBid,
        decimal CurrentAsk
    );

    public sealed record SaveRateSettingReq(
        string Code,
        bool IsVisible,
        decimal BidTlOffset,
        decimal AskTlOffset,
        string? CustomDisplay
    );

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;
        IReadOnlyList<QuoteDto> rates;
        try
        {
            // Kur Ayarları ekranında kullanıcı "anlık" değer beklediği için
            // her istekte kaynaktan tazeleme deniyoruz.
            rates = await _goldPriceService.RefreshAsync(ct);
        }
        catch
        {
            // Dış kaynak geçici hata verirse ekran boş kalmasın; son önbelleği göster.
            rates = await _goldPriceService.LatestOrRefreshIfOlderThanAsync(maxAgeSeconds: 8, ct);
        }

        var savedList = await _db.RateDisplaySettings
            .AsNoTracking()
            .Where(x => x.BranchId == branchId && !x.IsDeleted)
            .ToListAsync(ct);
        var saved = savedList.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        var rows = rates
            .OrderBy(x => x.Display)
            .Select(x =>
            {
                saved.TryGetValue(x.Code, out var setting);
                var isParityCode = HaremParityCodes.Contains(x.Code ?? "");
                var bidOffset = isParityCode ? 0m : (setting?.BidTlOffset ?? setting?.TlOffset ?? 0m);
                var askOffset = isParityCode ? 0m : (setting?.AskTlOffset ?? setting?.TlOffset ?? 0m);
                var display = string.IsNullOrWhiteSpace(setting?.CustomDisplay) ? x.Display : setting!.CustomDisplay!;
                return new RateSettingRowDto(
                    x.Code,
                    display,
                    setting?.IsVisible ?? true,
                    bidOffset,
                    askOffset,
                    setting?.CustomDisplay,
                    Math.Round(x.Bid + bidOffset, 4, MidpointRounding.AwayFromZero),
                    Math.Round(x.Ask + askOffset, 4, MidpointRounding.AwayFromZero)
                );
            })
            .ToList();

        return Ok(rows);
    }

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] List<SaveRateSettingReq>? req, CancellationToken ct)
    {
        if (!CanManageRates())
            return Forbid();
        if (!TryGetRequiredBranchId(out var branchId, out var branchErr)) return branchErr!;
        req ??= new List<SaveRateSettingReq>();
        var codes = req
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .Select(x => x.Code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Global filtre IsDeleted=true satırları gizler; aynı (TenantId, BranchId, Code) için silinmiş satır varken
        // yeniden INSERT benzersiz indeksi ihlal eder. Kiracı + şubeye göre yükle, gerekirse canlandır.
        var codeSet = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        var existing = (await _db.RateDisplaySettings
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == _tenant.TenantId && x.BranchId == branchId)
                .ToListAsync(ct))
            .Where(x => codeSet.Contains(x.Code))
            .ToList();

        foreach (var row in req.Where(x => !string.IsNullOrWhiteSpace(x.Code)))
        {
            var code = row.Code.Trim();
            var entity = existing.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            if (entity is null)
            {
                entity = new RateDisplaySetting
                {
                    TenantId = _tenant.TenantId,
                    BranchId = branchId,
                    Code = code
                };
                _db.RateDisplaySettings.Add(entity);
                existing.Add(entity);
            }

            entity.IsVisible = row.IsVisible;
            if (HaremParityCodes.Contains(code))
            {
                // KG USD/EUR satırları Harem ham verisiyle birebir kalmalı.
                entity.BidTlOffset = 0m;
                entity.AskTlOffset = 0m;
                entity.TlOffset = 0m;
            }
            else
            {
                entity.BidTlOffset = Math.Round(row.BidTlOffset, 4, MidpointRounding.AwayFromZero);
                entity.AskTlOffset = Math.Round(row.AskTlOffset, 4, MidpointRounding.AwayFromZero);
                // Geriye uyumluluk: eski tek offset alanını da ask ile paralel tut.
                entity.TlOffset = entity.AskTlOffset;
            }
            entity.CustomDisplay = NormalizeNullable(row.CustomDisplay);
            entity.IsDeleted = false;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { saved = req.Count });
    }

    private bool TryGetRequiredBranchId(out Guid branchId, out IActionResult? error)
    {
        branchId = _tenant.BranchId ?? Guid.Empty;
        if (branchId == Guid.Empty &&
            Request.Headers.TryGetValue("X-Branch-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            branchId = fromHdr;
        if (branchId == Guid.Empty)
        {
            var claim = User?.Claims?.FirstOrDefault(c =>
                c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claim, out var fromClaim) && fromClaim != Guid.Empty)
                branchId = fromClaim;
        }
        if (branchId == Guid.Empty)
        {
            error = BadRequest(new { error = "Şube bilgisi eksik. Girişte şube seçin (JWT branch_id)." });
            return false;
        }
        error = null;
        return true;
    }

    private static string? NormalizeNullable(string? value)
    {
        var cleaned = (value ?? "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private bool CanManageRates()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_manage_rates");
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }
}
