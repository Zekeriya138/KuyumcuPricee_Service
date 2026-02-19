// Kuyumcu.PriceService/Controllers/PricesController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Kuyumcu.PriceService.Services;

namespace Kuyumcu.PriceService.Controllers;

[ApiController]
[Route("prices")]
[Authorize] // mevcut JWT koruması
public sealed class PricesController : ControllerBase
{
    private readonly GoldPriceService _svc;
    private readonly PriceCache _cache;

    public PricesController(GoldPriceService svc, PriceCache cache)
    {
        _svc = svc;
        _cache = cache;
    }

    // Mevcut endpoint: tüm liste (eski davranış)
    [HttpGet("latest")]
    public IActionResult Latest([FromQuery] string? codes = null, [FromQuery] bool withMeta = false)
    {
        if (string.IsNullOrWhiteSpace(codes))
        {
            if (!withMeta) return Ok(_cache.GetAll());

            var (items, ts, age) = _cache.GetSnapshot();
            return Ok(new { lastUpdatedUtc = ts, ageSeconds = Math.Round(age, 1), items });
        }

        var list = _cache.GetByCodes(codes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!withMeta) return Ok(list);

        var (_, ts2, age2) = _cache.GetSnapshot();
        return Ok(new { lastUpdatedUtc = ts2, ageSeconds = Math.Round(age2, 1), items = list });
    }

    // Manuel yenile (mevcut davranış korunur)
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var list = await _svc.RefreshAsync(ct);
        return Ok(list);
    }

    // Sadece durum/metrik
    [HttpGet("meta")]
    public IActionResult Meta()
    {
        var (_, ts, age) = _cache.GetSnapshot();
        return Ok(new { lastUpdatedUtc = ts, ageSeconds = Math.Round(age, 1) });
    }
}
