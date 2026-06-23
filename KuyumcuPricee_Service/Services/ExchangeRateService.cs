using Kuyumcu.PriceService.Services;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

/// <summary>İşlem anı birim->TL kur sağlayıcısı.</summary>
public sealed class ExchangeRateService
{
    private readonly GoldPriceService _gold;
    private readonly AppDbContext _db;

    public ExchangeRateService(GoldPriceService gold, AppDbContext db)
    {
        _gold = gold;
        _db = db;
    }

    public IReadOnlyDictionary<string, decimal> GetUnitToTlRates()
    {
        var list = _gold.LatestOrEmpty();

        decimal Ask(string code) =>
            list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Ask ?? 0m;
        decimal Bid(string code) =>
            list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Bid ?? 0m;

        var usd = Ask("USD_TRY");
        if (usd <= 0) usd = Bid("USD_TRY");
        var eur = Ask("EUR_TRY");
        if (eur <= 0) eur = Bid("EUR_TRY");
        var has = Bid("G24_TRY");
        if (has <= 0) has = Ask("G24_TRY");
        var gumus = Ask("XAG_GM_TRY");
        if (gumus <= 0) gumus = Bid("XAG_GM_TRY");

        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["TL"] = 1m,
            ["USD"] = usd > 0 ? usd : 1m,
            ["EUR"] = eur > 0 ? eur : 1m,
            ["HAS"] = has > 0 ? has : 1m,
            ["GUMUS"] = gumus > 0 ? gumus : 1m
        };
    }

    /// <summary>
    /// Fatura ayrıştırmasında kullanılacak karat gram satış fiyatını (TL) döner.
    /// Önce doğrudan karat kodu (G22_TRY, G14_TRY, G24_TRY) denenir;
    /// yoksa HAS/KÜLÇE referansından milyem ile türetilir.
    /// </summary>
    public decimal GetKaratGramSellPrice(string? karatText, string? referenceSource = null)
    {
        var list = _gold.LatestOrEmpty();

        decimal PickSell(string code)
        {
            var row = list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            if (row is null) return 0m;
            return row.Ask > 0 ? row.Ask : row.Bid;
        }

        var karat = (karatText ?? string.Empty).Trim().ToUpperInvariant();
        if (karat.Contains("22") || karat.Contains("916"))
        {
            var g22 = PickSell("G22_TRY");
            if (g22 > 0) return g22;
        }
        if (karat.Contains("14") || karat.Contains("585"))
        {
            var g14 = PickSell("G14_TRY");
            if (g14 > 0) return g14;
        }
        if (karat.Contains("24") || karat.Contains("999") || karat.Contains("HAS"))
        {
            var g24 = PickSell("G24_TRY");
            if (g24 > 0) return g24;
        }

        var source = (referenceSource ?? "HAS").Trim().ToUpperInvariant();
        var basePrice = source switch
        {
            "KULCE" or "KULCE_ALTIN" => PickSell("KULCE_ALTIN"),
            _ => PickSell("G24_TRY")
        };
        if (basePrice <= 0)
            basePrice = PickSell("G24_TRY");
        if (basePrice <= 0)
            return 0m;

        var milyem = JewelrySpecialBaseCalculator.MilyemFromKarat(karatText);
        if (milyem <= 0m) milyem = 1m;
        return Math.Round(basePrice * milyem, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Home ekranı ile aynı kaynaktan, şube/tenant ayarları uygulanmış satış kurlarını döner.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal>> GetAdjustedSellRatesByCodeAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        var list = await _gold.LatestOrRefreshIfOlderThanAsync(maxAgeSeconds: 8, ct);
        var settings = await _db.RateDisplaySettings
            .AsNoTracking()
            .Where(x => !x.IsDeleted &&
                        (tenantId == Guid.Empty || x.TenantId == tenantId) &&
                        (branchId == Guid.Empty || x.BranchId == branchId))
            .ToListAsync(ct);
        var settingMap = settings.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var quote in list)
        {
            settingMap.TryGetValue(quote.Code, out var setting);
            var askOffset = setting?.AskTlOffset ?? setting?.TlOffset ?? 0m;
            var bidOffset = setting?.BidTlOffset ?? setting?.TlOffset ?? 0m;

            var askAdjusted = Math.Round(quote.Ask + askOffset, 4, MidpointRounding.AwayFromZero);
            var bidAdjusted = Math.Round(quote.Bid + bidOffset, 4, MidpointRounding.AwayFromZero);
            var sell = askAdjusted > 0m ? askAdjusted : bidAdjusted;
            if (sell > 0m)
                map[quote.Code] = sell;
        }

        return map;
    }

    public decimal GetKaratGramSellPrice(
        string? karatText,
        string? referenceSource,
        IReadOnlyDictionary<string, decimal>? adjustedSellRatesByCode)
    {
        decimal PickSell(string code)
        {
            if (adjustedSellRatesByCode is not null &&
                adjustedSellRatesByCode.TryGetValue(code, out var adjusted) &&
                adjusted > 0m)
            {
                return adjusted;
            }

            var row = _gold.LatestOrEmpty()
                .FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            if (row is null) return 0m;
            return row.Ask > 0 ? row.Ask : row.Bid;
        }

        var karat = (karatText ?? string.Empty).Trim().ToUpperInvariant();
        if (karat.Contains("22") || karat.Contains("916"))
        {
            var g22 = PickSell("G22_TRY");
            if (g22 > 0) return g22;
        }
        if (karat.Contains("14") || karat.Contains("585"))
        {
            var g14 = PickSell("G14_TRY");
            if (g14 > 0) return g14;
        }
        if (karat.Contains("24") || karat.Contains("999") || karat.Contains("HAS"))
        {
            var g24 = PickSell("G24_TRY");
            if (g24 > 0) return g24;
        }

        var source = (referenceSource ?? "HAS").Trim().ToUpperInvariant();
        var basePrice = source switch
        {
            "KULCE" or "KULCE_ALTIN" => PickSell("KULCE_ALTIN"),
            _ => PickSell("G24_TRY")
        };
        if (basePrice <= 0)
            basePrice = PickSell("G24_TRY");
        if (basePrice <= 0)
            return 0m;

        var milyem = JewelrySpecialBaseCalculator.MilyemFromKarat(karatText);
        if (milyem <= 0m) milyem = 1m;
        return Math.Round(basePrice * milyem, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// PAYMENT için satış (ask), COLLECTION için alış (bid) kur seti döner.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> GetUnitToTlRatesByTxType(string? txType)
    {
        var t = (txType ?? "").Trim().ToUpperInvariant();
        var useSellRates = t is "PAYMENT" or "ODEME";
        var list = _gold.LatestOrEmpty();

        decimal Pick(string code)
        {
            var row = list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            if (row == null) return 0m;
            var primary = useSellRates ? row.Ask : row.Bid;
            var fallback = useSellRates ? row.Bid : row.Ask;
            return primary > 0 ? primary : fallback;
        }

        var usd = Pick("USD_TRY");
        var eur = Pick("EUR_TRY");
        var gbp = Pick("GBP_TRY");
        var has = Pick("G24_TRY");
        var gumus = Pick("XAG_GM_TRY");

        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["TL"] = 1m,
            ["USD"] = usd > 0 ? usd : 1m,
            ["EUR"] = eur > 0 ? eur : 1m,
            ["GBP"] = gbp > 0 ? gbp : 1m,
            ["HAS"] = has > 0 ? has : 1m,
            ["GUMUS"] = gumus > 0 ? gumus : 1m
        };
    }

    /// <summary>
    /// Dönüşümde kaynak birim için alış (bid) kurlarını döner.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> GetUnitToTlBuyRates()
    {
        var list = _gold.LatestOrEmpty();
        return BuildRates(list, pickAsk: false);
    }

    /// <summary>
    /// Dönüşümde hedef birim için satış (ask) kurlarını döner.
    /// </summary>
    public IReadOnlyDictionary<string, decimal> GetUnitToTlSellRates()
    {
        var list = _gold.LatestOrEmpty();
        return BuildRates(list, pickAsk: true);
    }

    public decimal GetQuoteBidByCode(string code) => GetQuoteSideByCode(code, pickAsk: false);

    public decimal GetQuoteAskByCode(string code) => GetQuoteSideByCode(code, pickAsk: true);

    private decimal GetQuoteSideByCode(string code, bool pickAsk)
    {
        var list = _gold.LatestOrEmpty();
        var row = list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        if (row is null) return 0m;
        var primary = pickAsk ? row.Ask : row.Bid;
        var fallback = pickAsk ? row.Bid : row.Ask;
        return primary > 0m ? primary : fallback;
    }

    private static IReadOnlyDictionary<string, decimal> BuildRates(IReadOnlyCollection<QuoteDto> list, bool pickAsk)
    {
        decimal Pick(string code)
        {
            var row = list.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            if (row == null) return 0m;
            var primary = pickAsk ? row.Ask : row.Bid;
            var fallback = pickAsk ? row.Bid : row.Ask;
            return primary > 0 ? primary : fallback;
        }

        var usd = Pick("USD_TRY");
        var eur = Pick("EUR_TRY");
        var gbp = Pick("GBP_TRY");
        var has = Pick("G24_TRY");
        var gumus = Pick("XAG_GM_TRY");

        return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["TL"] = 1m,
            ["USD"] = usd > 0 ? usd : 1m,
            ["EUR"] = eur > 0 ? eur : 1m,
            ["GBP"] = gbp > 0 ? gbp : 1m,
            ["HAS"] = has > 0 ? has : 1m,
            ["GUMUS"] = gumus > 0 ? gumus : 1m
        };
    }
}
