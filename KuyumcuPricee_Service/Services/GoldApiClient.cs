using System.Text.Json;

namespace Kuyumcu.PriceService.Services
{
    public sealed class GoldApiClient
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _cfg;

        public GoldApiClient(HttpClient http, IConfiguration cfg)
        {
            _http = http;
            _cfg = cfg;
        }

        // GOLDAPI: tek sembol (örn "XAU/USD" veya "XAG/USD")
        public async Task<JsonElement?> FetchMetalAsync(string symbol, CancellationToken ct = default)
        {
            var baseUrl = _cfg["Upstream:GoldApi:BaseUrl"] ?? throw new InvalidOperationException("Upstream:GoldApi:BaseUrl yok");
            var apiKey = _cfg["Upstream:GoldApi:ApiKey"] ?? throw new InvalidOperationException("Upstream:GoldApi:ApiKey yok");

            var url = $"{baseUrl.TrimEnd('/')}/{symbol}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Remove("x-access-token");
            req.Headers.Add("x-access-token", apiKey);

            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.Clone();
        }

        // Döviz için exchangerate.host (TRY bazlı)
        public async Task<(decimal? usdtry, decimal? eurtry, decimal? eurusd)> FetchFxAsync(CancellationToken ct = default)
        {
            // 3 ayrı kaynak dene (sırayla)
            var urls = new[]
            {
        // 1) exchangerate.host – TRY bazlı
        "https://api.exchangerate.host/latest?base=TRY&symbols=USD,EUR",
        // 2) exchangerate.host – USD bazlı (kurumsal ağlarda TRY bazlı bazen 400 döner)
        "https://api.exchangerate.host/latest?base=USD&symbols=TRY,EUR",
        // 3) frankfurter – TRY->USD,EUR
        "https://api.frankfurter.app/latest?from=TRY&to=USD,EUR"
    };

            foreach (var fxUrl in urls)
            {
                try
                {
                    using var resp = await _http.GetAsync(fxUrl, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    // Ortak alanlar
                    string? baseCode = doc.RootElement.TryGetProperty("base", out var b) ? b.GetString()
                                     : doc.RootElement.TryGetProperty("base_code", out var bc) ? bc.GetString()
                                     : null;

                    if (!doc.RootElement.TryGetProperty("rates", out var rates))
                        continue;

                    decimal? get(string c) => rates.TryGetProperty(c, out var v) ? v.GetDecimal() : (decimal?)null;

                    decimal? usdtry = null, eurtry = null, eurusd = null;

                    if (string.Equals(baseCode, "TRY", StringComparison.OrdinalIgnoreCase))
                    {
                        var usd = get("USD"); var eur = get("EUR");
                        usdtry = usd is > 0 ? 1m / usd.Value : null;
                        eurtry = eur is > 0 ? 1m / eur.Value : null;
                    }
                    else if (string.Equals(baseCode, "USD", StringComparison.OrdinalIgnoreCase))
                    {
                        var tryRate = get("TRY"); var eur = get("EUR");
                        usdtry = tryRate; // USD bazlı geldi; USD->TRY doğrudan
                        if (usdtry is decimal u && eur is decimal e && u != 0)
                        {
                            // EUR/USD = e, EUR/TRY = (EUR/USD) * (USD/TRY)
                            eurusd = Math.Round(e, 6);
                            eurtry = Math.Round(e * u, 4);
                        }
                    }
                    else
                    {
                        // frankfurter TRY->USD,EUR biçimi gönderir
                        var usd = get("USD"); var eur = get("EUR");
                        usdtry = usd is > 0 ? 1m / usd.Value : null;
                        eurtry = eur is > 0 ? 1m / eur.Value : null;
                    }

                    if (usdtry is decimal uu && eurtry is decimal ee && eurusd is null && uu != 0)
                        eurusd = Math.Round(ee / uu, 6);

                    if (usdtry is not null || eurtry is not null)
                        return (usdtry, eurtry, eurusd);
                }
                catch
                {
                    // bir sonraki kaynağa geç
                }
            }

            // Hepsi başarısızsa null’lar
            return (null, null, null);
        }

    }
}
