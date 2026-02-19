using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Kuyumcu.PriceService.Services
{
    public sealed class GoldPriceService
    {
        private readonly GoldApiClient _client;
        private readonly PriceCache _cache;
        private readonly IConfiguration _cfg;

        public GoldPriceService(GoldApiClient client, PriceCache cache, IConfiguration cfg)
        {
            _client = client;
            _cache = cache;
            _cfg = cfg;
        }

        public IReadOnlyList<QuoteDto> LatestOrEmpty() => _cache.GetAll();

        public async Task<IReadOnlyList<QuoteDto>> RefreshAsync(CancellationToken ct = default)
        {
            // 1) Kaynaklardan oku
            var (usdtry, eurtry, eurusd) = await _client.FetchFxAsync(ct);

            var xau = await _client.FetchMetalAsync("XAU/USD", ct); // Ons altın USD
            var xag = await _client.FetchMetalAsync("XAG/USD", ct); // Ons gümüş USD

            // 2) Metal mid fiyatları
            static decimal? Mid(JsonElement? el, string bid, string ask)
            {
                if (el is null) return null;
                if (!el.Value.TryGetProperty(bid, out var b)) return null;
                if (!el.Value.TryGetProperty(ask, out var a)) return null;
                return (b.GetDecimal() + a.GetDecimal()) / 2m;
            }

            static decimal Get(JsonElement el, string name) => el.GetProperty(name).GetDecimal();

            decimal? xauBid = xau is null ? null : Get(xau.Value, "bid");
            decimal? xauAsk = xau is null ? null : Get(xau.Value, "ask");
            decimal? xagMid = Mid(xag, "bid", "ask");

            decimal? xauMid = (xauBid.HasValue && xauAsk.HasValue)
                ? (xauBid.Value + xauAsk.Value) / 2m
                : null;

            // 3) Konfig — Upstream:GoldApi:PriceFeed
            var pf = _cfg.GetSection("Upstream:GoldApi:PriceFeed");

            decimal fxBps = pf.GetValue("Spreads:FxBps", 10m);
            decimal goldBps = pf.GetValue("Spreads:GoldGramBps", 25m);
            decimal silverBps = pf.GetValue("Spreads:SilverGramBps", 60m);
            decimal coinBps = pf.GetValue("Spreads:CoinBps", 80m);

            decimal k24 = pf.GetValue("GoldPurity:K24", 1.000m);
            decimal k22 = pf.GetValue("GoldPurity:K22", 0.916m);
            decimal k14 = pf.GetValue("GoldPurity:K14", 0.585m);

            static decimal Round2(decimal v) => Math.Round(v, 2);
            static decimal Round4(decimal v) => Math.Round(v, 4);
            decimal BpsUp(decimal mid, decimal bps) => Round4(mid * (1 + bps / 10_000m));
            decimal BpsDn(decimal mid, decimal bps) => Round4(mid * (1 - bps / 10_000m));

            const decimal OZ_TO_GRAM = 31.1034768m;
            const decimal OZ_TO_KG = 32.1507466m;

            var now = DateTime.UtcNow;
            var list = new List<QuoteDto>(capacity: 24);

            // ---------- 3 FX ----------
            if (usdtry is decimal u)
                list.Add(new QuoteDto { Code = "USD_TRY", Display = "USD/TRY", Bid = BpsDn(u, fxBps), Ask = BpsUp(u, fxBps), Ts = now });
            if (eurtry is decimal e)
                list.Add(new QuoteDto { Code = "EUR_TRY", Display = "EUR/TRY", Bid = BpsDn(e, fxBps), Ask = BpsUp(e, fxBps), Ts = now });
            if (eurusd is decimal eu)
                list.Add(new QuoteDto { Code = "EUR_USD", Display = "EUR/USD", Bid = BpsDn(eu, fxBps), Ask = BpsUp(eu, fxBps), Ts = now });

            // ---------- Ons & KG (USD/EUR) ----------
            if (xauBid is decimal xb && xauAsk is decimal xa)
                list.Add(new QuoteDto { Code = "XAU_OZ_USD", Display = "Altın Ons (USD)", Bid = Round2(xb), Ask = Round2(xa), Ts = now });

            if (xauBid is decimal xb2 && xauAsk is decimal xa2)
            {
                list.Add(new QuoteDto
                {
                    Code = "XAU_KG_USD",
                    Display = "Altın KG (USD)",
                    Bid = Round2(xb2 * OZ_TO_KG),
                    Ask = Round2(xa2 * OZ_TO_KG),
                    Ts = now
                });
            }

            if (xauBid is decimal xb3 && xauAsk is decimal xa3 && eurusd is decimal eu2 && eu2 != 0)
            {
                list.Add(new QuoteDto
                {
                    Code = "XAU_KG_EUR",
                    Display = "Altın KG (EUR)",
                    Bid = Round2((xb3 / eu2) * OZ_TO_KG),
                    Ask = Round2((xa3 / eu2) * OZ_TO_KG),
                    Ts = now
                });
            }

            // ---------- Gram 24/22/14 (TRY) ----------
            if (xauMid is decimal xm && usdtry is decimal utry)
            {
                var hasMid = (xm / OZ_TO_GRAM) * utry * k24; // 24k gram tabanı (TRY)
                var hasBid = BpsDn(hasMid, goldBps);
                var hasAsk = BpsUp(hasMid, goldBps);

                list.Add(new QuoteDto { Code = "G24_TRY", Display = "Gram 24K (TRY)", Bid = Round2(hasBid), Ask = Round2(hasAsk), Ts = now });
                list.Add(new QuoteDto { Code = "G22_TRY", Display = "Gram 22K (TRY)", Bid = Round2(hasBid * k22 / k24), Ask = Round2(hasAsk * k22 / k24), Ts = now });
                list.Add(new QuoteDto { Code = "G14_TRY", Display = "Gram 14K (TRY)", Bid = Round2(hasBid * k14 / k24), Ask = Round2(hasAsk * k14 / k24), Ts = now });

                // 24K gram (USD)
                var g24UsdMid = xm / OZ_TO_GRAM; // USD/gram
                list.Add(new QuoteDto { Code = "G24_USD", Display = "Gram 24K (USD)", Bid = Round2(BpsDn(g24UsdMid, goldBps)), Ask = Round2(BpsUp(g24UsdMid, goldBps)), Ts = now });
            }

            // ---------- Gümüş gram (TRY) ----------
            if (xagMid is decimal xg && usdtry is decimal utry2)
            {
                var silverMid = (xg / OZ_TO_GRAM) * utry2;
                list.Add(new QuoteDto
                {
                    Code = "XAG_GM_TRY",
                    Display = "Gümüş Gram (TRY)",
                    Bid = Round2(BpsDn(silverMid, silverBps)),
                    Ask = Round2(BpsUp(silverMid, silverBps)),
                    Ts = now
                });
            }

            // ---------- Ziynet/Cumhuriyet (8 kalem) ----------
            QuoteDto? Coin(string key, string code)
            {
                var weight = pf.GetValue<decimal?>($"CoinCatalog:{key}:WeightGr");
                var purityK = pf.GetValue<string>($"CoinCatalog:{key}:Purity");
                var premium = pf.GetValue<decimal?>($"CoinCatalog:{key}:PremiumTry");

                if (weight is null || premium is null) return null;
                if (xauMid is not decimal xm2 || usdtry is not decimal u2) return null;

                var purity = purityK switch
                {
                    "K24" => k24,
                    "K22" => k22,
                    "K14" => k14,
                    _ => k22
                };

                var hasGrMid = (xm2 / OZ_TO_GRAM) * u2 * k24; // 24K gram (TRY)
                var mid = hasGrMid * (purity / k24) * weight.Value + premium.Value;
                var bid = Round2(BpsDn(mid, coinBps));
                var ask = Round2(BpsUp(mid, coinBps));

                return new QuoteDto { Code = code, Display = code.Replace('_', ' '), Bid = bid, Ask = ask, Ts = now };
            }

            var coins = new (string Key, string Code)[]
            {
                ("YeniCeyrek","CEYREK_YENI"),
                ("EskiCeyrek","CEYREK_ESKI"),
                ("YeniYarim", "YARIM_YENI"),
                ("EskiYarim", "YARIM_ESKI"),
                ("YeniTam",   "TAM_YENI"),
                ("EskiTam",   "TAM_ESKI"),
                ("YeniAta",   "ATA_YENI"),
                ("EskiAta",   "ATA_ESKI"),
            };
            foreach (var (k, c) in coins)
            {
                var q = Coin(k, c);
                if (q is not null) list.Add(q);
            }

            // 5) Cache
            _cache.SetAll(list);
            return list;
        }
    }
}
