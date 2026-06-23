using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Kuyumcu.PriceService.Services;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;

namespace KUYUMCU.Price_Service.Controllers;

/// <summary>WinForms uyumlu kur/ziynet fiyatları: api/rates/gold → HasAltin, Ayar22, Ayar14, CeyrekSatis, vb.</summary>
[ApiController]
[Route("api/rates")]
[Authorize]
public sealed class RatesController : ControllerBase
{
    private static readonly HashSet<string> HaremParityCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "XAU_KG_USD",
        "XAU_KG_EUR"
    };

    private readonly GoldPriceService _svc;
    private readonly HaremApiClient _harem;
    private readonly AppDbContext _db;

    public RatesController(GoldPriceService svc, HaremApiClient harem, AppDbContext db)
    {
        _svc = svc;
        _harem = harem;
        _db = db;
    }

    /// <summary>Harem’e doğrudan istek; ham satır sayısı (Swagger ile ağ/anahtar kontrolü).</summary>
    [HttpGet("harem-probe")]
    [AllowAnonymous]
    public async Task<IActionResult> HaremProbe(CancellationToken ct)
    {
        var d = await _harem.ProbeRemoteAsync(ct);
        return Ok(d);
    }

    /// <summary>x-app-key yeterli; JWT zorunlu değil (satış ekranı kurları).</summary>
    [HttpGet("gold")]
    [AllowAnonymous]
    public async Task<IActionResult> Gold(CancellationToken ct)
    {
        var (list, _) = await GetAdjustedQuotesAsync(ct);
        decimal getAsk(string code) => list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Ask ?? 0m;
        decimal getBid(string code) => list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Bid ?? 0m;

        // Döviz: 1 USD = X TRY, 1 EUR = X TRY (Ask kullanılır)
        var usdTry = getAsk("USD_TRY");
        if (usdTry <= 0) usdTry = getBid("USD_TRY");
        var eurTry = getAsk("EUR_TRY");
        if (eurTry <= 0) eurTry = getBid("EUR_TRY");
        var gbpTry = getAsk("GBP_TRY");
        if (gbpTry <= 0) gbpTry = getBid("GBP_TRY");

        // Gram ayar (satış = Ask, alış = Bid)
        var hasAltin = getAsk("G24_TRY");
        var hasAltinAlis = getBid("G24_TRY"); // Has altın alış fiyatı (BirimFiyat formülde)
        var ayar22 = getAsk("G22_TRY");
        var ayar14 = getAsk("G14_TRY");

        // Ziynet anlık satış fiyatları (Ask = satış)
        var ceyrekSatis = getAsk("CEYREK_YENI");
        if (ceyrekSatis <= 0) ceyrekSatis = getAsk("CEYREK_ESKI");
        var yarimSatis = getAsk("YARIM_YENI");
        if (yarimSatis <= 0) yarimSatis = getAsk("YARIM_ESKI");
        var tamSatis = getAsk("TAM_YENI");
        if (tamSatis <= 0) tamSatis = getAsk("TAM_ESKI");
        var gram22Satis = ayar22;

        return Ok(new
        {
            UsdTry = usdTry,
            EurTry = eurTry,
            GbpTry = gbpTry,
            HasAltin = hasAltin,
            HasAltinAlis = hasAltinAlis,
            Ayar22 = ayar22,
            Ayar14 = ayar14,
            CeyrekSatis = ceyrekSatis,
            YarimSatis = yarimSatis,
            TamSatis = tamSatis,
            Gram22Satis = gram22Satis,
            quoteCount = list.Count,
            haremRawRows = _svc.LastHaremRowCount
        });
    }

    /// <summary>Tüm kurlar alış (Bid) ve satış (Ask) ile. SatisFormu paneli için.</summary>
    [HttpGet("all")]
    [AllowAnonymous]
    public async Task<IActionResult> All(CancellationToken ct)
    {
        var (list, settings) = await GetAdjustedQuotesAsync(ct);
        var order = new[]
        {
            "USD_TRY", "EUR_TRY", "GBP_TRY", "AUD_TRY", "CAD_TRY", "SAR_TRY", "EUR_GBP", "EUR_USD",
            "G24_TRY", "G22_TRY", "G14_TRY", "XAG_GM_TRY",
            "CEYREK_YENI", "CEYREK_ESKI", "YARIM_YENI", "YARIM_ESKI", "TAM_YENI", "TAM_ESKI",
            "ATA_YENI", "ATA_ESKI", "ATA5_YENI", "ATA5_ESKI", "GREMSE_YENI", "GREMSE_ESKI", "KULCE_ALTIN",
            "XAU_OZ_USD", "XAU_OZ_EUR", "XAU_KG_USD", "XAU_KG_EUR", "XAU_XAG_RATIO",
            "G24_USD", "XAG_OZ_USD", "GUM_OZ_USD", "XAG_GM_USD",
            "PLATIN_TRY", "PALADYUM_TRY", "XPT_OZ_USD", "XPD_OZ_USD"
        };
        var displayMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USD_TRY"] = "USD", ["EUR_TRY"] = "EUR", ["GBP_TRY"] = "GBP", ["AUD_TRY"] = "AUD", ["CAD_TRY"] = "CAD", ["SAR_TRY"] = "SAR",
            ["EUR_GBP"] = "EUR/GBP", ["EUR_USD"] = "EUR/USD",
            ["G24_TRY"] = "Gram Altın (Has)", ["G22_TRY"] = "22 Ayar", ["G14_TRY"] = "14 Ayar",
            ["XAG_GM_TRY"] = "Gümüş Gram TRY", ["CEYREK_YENI"] = "Yeni Çeyrek", ["CEYREK_ESKI"] = "Eski Çeyrek",
            ["YARIM_YENI"] = "Yeni Yarım", ["YARIM_ESKI"] = "Eski Yarım", ["TAM_YENI"] = "Yeni Tam", ["TAM_ESKI"] = "Eski Tam",
            ["ATA_YENI"] = "Yeni Ata", ["ATA_ESKI"] = "Eski Ata", ["ATA5_YENI"] = "Yeni Ata5", ["ATA5_ESKI"] = "Eski Ata5",
            ["GREMSE_YENI"] = "Yeni Gremse", ["GREMSE_ESKI"] = "Eski Gremse", ["KULCE_ALTIN"] = "Külçe Altın",
            ["XAU_OZ_USD"] = "Altın Ons USD", ["XAU_OZ_EUR"] = "Altın Ons EUR", ["XAU_KG_USD"] = "Altın KG USD", ["XAU_KG_EUR"] = "Altın KG EUR",
            ["XAU_XAG_RATIO"] = "Au/Ag Oran", ["XAG_OZ_USD"] = "Gümüş Ons USD", ["GUM_OZ_USD"] = "Gümüş Ons (GUM)", ["XAG_GM_USD"] = "Gümüş Gram USD",
            ["G24_USD"] = "Gram Altın USD", ["PLATIN_TRY"] = "Platin TL", ["PALADYUM_TRY"] = "Paladyum TL",
            ["XPT_OZ_USD"] = "Platin Ons USD", ["XPD_OZ_USD"] = "Paladyum Ons USD"
        };
        var result = order
            .Select(code => list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
            .Where(x => x != null && (x.Bid > 0 || x.Ask > 0))
            .Select(x => new
            {
                Code = x!.Code,
                Display = ResolveDisplay(x, displayMap, settings),
                Bid = x.Bid,
                Ask = x.Ask,
                IsVisible = ResolveVisibility(x.Code, settings)
            })
            .ToList();
        var rest = list.Where(x => !order.Contains(x.Code, StringComparer.OrdinalIgnoreCase) && (x.Bid > 0 || x.Ask > 0))
            .Select(x => new
            {
                x.Code,
                Display = ResolveDisplay(x, displayMap, settings),
                x.Bid,
                x.Ask,
                IsVisible = ResolveVisibility(x.Code, settings)
            });
        return Ok(result.Concat(rest));
    }

    /// <summary>HaremAPI’den kurları yenile (WPF «Kur Yenile»).</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var list = await _svc.RefreshAsync(ct);
        return Ok(new { refreshed = list.Count });
    }

    private async Task<(List<QuoteDto> Quotes, Dictionary<string, RateDisplaySetting> Settings)> GetAdjustedQuotesAsync(CancellationToken ct)
    {
        // Harem ekranıyla farkı azaltmak için rates endpoint'lerinde kısa TTL ile canlıya yakın cache kullan.
        var list = await _svc.LatestOrRefreshIfOlderThanAsync(maxAgeSeconds: 8, ct);

        var tenantId = GetTenantIdOrEmpty();
        var branchId = GetBranchIdOrEmpty();
        var settingsList = await _db.RateDisplaySettings
            .AsNoTracking()
            .Where(x => !x.IsDeleted
                        && (tenantId == Guid.Empty || x.TenantId == tenantId)
                        && (branchId == Guid.Empty || x.BranchId == branchId))
            .ToListAsync(ct);
        var settings = settingsList.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        var adjusted = list
            .Select(x =>
            {
                settings.TryGetValue(x.Code, out var setting);
                var isParityCode = HaremParityCodes.Contains(x.Code ?? "");
                var bidOffset = isParityCode ? 0m : (setting?.BidTlOffset ?? setting?.TlOffset ?? 0m);
                var askOffset = isParityCode ? 0m : (setting?.AskTlOffset ?? setting?.TlOffset ?? 0m);
                return new QuoteDto
                {
                    Code = x.Code,
                    Display = x.Display,
                    Bid = RoundRate(x.Bid + bidOffset),
                    Ask = RoundRate(x.Ask + askOffset),
                    Ts = x.Ts
                };
            })
            .ToList();

        return (adjusted, settings);
    }

    private static decimal RoundRate(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static bool ResolveVisibility(string code, IReadOnlyDictionary<string, RateDisplaySetting> settings)
        => !settings.TryGetValue(code, out var setting) || setting.IsVisible;

    private static string ResolveDisplay(QuoteDto quote, IReadOnlyDictionary<string, string> displayMap, IReadOnlyDictionary<string, RateDisplaySetting> settings)
    {
        if (settings.TryGetValue(quote.Code, out var setting) && !string.IsNullOrWhiteSpace(setting.CustomDisplay))
            return setting.CustomDisplay!;
        return displayMap.TryGetValue(quote.Code, out var mapped) ? mapped : quote.Display;
    }

    private Guid GetTenantIdOrEmpty()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        return Guid.Empty;
    }

    private Guid GetBranchIdOrEmpty()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Branch-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        return Guid.Empty;
    }
}
