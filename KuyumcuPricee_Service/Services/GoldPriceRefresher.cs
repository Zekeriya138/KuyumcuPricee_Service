using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kuyumcu.PriceService.Services;

/// <summary>Anlık fiyatları <a href="https://haremapi.tr/docs/#prices-endpoints">HaremAPI</a> üzerinden alır; mevcut <see cref="QuoteDto.Code"/> sözleşmesi korunur.</summary>
public sealed class GoldPriceService
{
    private readonly HaremApiClient _harem;
    private readonly PriceCache _cache;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GoldPriceService> _log;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public GoldPriceService(HaremApiClient harem, PriceCache cache, IConfiguration cfg, ILogger<GoldPriceService> log)
    {
        _harem = harem;
        _cache = cache;
        _cfg = cfg;
        _log = log;
    }

    public IReadOnlyList<QuoteDto> LatestOrEmpty() => _cache.GetAll();

    /// <summary>Önbellek yaşı verilen saniyeyi aştıysa Harem'den yeniler; aksi halde cache döner.</summary>
    public async Task<IReadOnlyList<QuoteDto>> LatestOrRefreshIfOlderThanAsync(double maxAgeSeconds, CancellationToken ct = default)
    {
        if (maxAgeSeconds < 0) maxAgeSeconds = 0;
        var snap = _cache.GetSnapshot();
        if (snap.Items.Count > 0 && snap.AgeSeconds <= maxAgeSeconds)
            return snap.Items;
        return await RefreshAsync(ct);
    }

    /// <summary>Son <see cref="RefreshAsync"/> çağrısında Harem yanıtındaki ham satır sayısı (teşhis).</summary>
    public int LastHaremRowCount { get; private set; }

    public async Task<IReadOnlyList<QuoteDto>> RefreshAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
        const double carryForwardMaxAgeSecondsDefault = 15 * 60; // 15 dk
        var carryForwardMaxAgeSeconds = Math.Max(
            30,
            _cfg.GetValue<double>("Upstream:HaremApi:CarryForwardMaxAgeSeconds", carryForwardMaxAgeSecondsDefault));
        var prevSnapshot = _cache.GetSnapshot();
        var prevByCode = prevSnapshot.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var pf = _cfg.GetSection("Upstream:HaremApi:PriceFeed");
        var useRawHaremQuotes = pf.GetValue("UseRawHaremQuotes", false);
        // Direct KG sembolü, ons referansına bu orandan fazla saparsa güvenilmez kabul edilir.
        // Örn: 2.5 => %2.5
        var directKgMaxRefDiffRatio = Math.Max(
            0.005m,
            pf.GetValue("DirectKgMaxRefDiffPercent", 2.5m) / 100m);
        // SARRAFIYE KG satırları için (USDKG/EURKG) referans ons türevine kısmi yakınsama.
        // USD ve EUR çiftlerinde piyasa davranışı farklı olduğundan oranlar ayrı kalibre edilir.
        var directKgUsdBlendBidRatio = Math.Clamp(
            pf.GetValue("DirectKgSarrafiyeBlendUsdBidPercent", 9.5m) / 100m,
            0m,
            0.5m);
        var directKgUsdBlendAskRatio = Math.Clamp(
            pf.GetValue("DirectKgSarrafiyeBlendUsdAskPercent", 10.3m) / 100m,
            0m,
            0.5m);
        var directKgEurBlendBidRatio = Math.Clamp(
            pf.GetValue("DirectKgSarrafiyeBlendEurBidPercent", 11.2m) / 100m,
            0m,
            0.5m);
        var directKgEurBlendAskRatio = Math.Clamp(
            pf.GetValue("DirectKgSarrafiyeBlendEurAskPercent", 15.3m) / 100m,
            0m,
            0.5m);
        decimal fxBps = pf.GetValue("Spreads:FxBps", 10m);
        decimal goldBps = pf.GetValue("Spreads:GoldGramBps", 25m);
        decimal silverBps = pf.GetValue("Spreads:SilverGramBps", 60m);
        decimal coinBps = pf.GetValue("Spreads:CoinBps", 80m);
        decimal metalUsdBps = pf.GetValue("Spreads:MetalUsdBps", 25m);

        static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
        static decimal Round4(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
        decimal BpsUp(decimal mid, decimal bps) => Round4(mid * (1 + bps / 10_000m));
        decimal BpsDn(decimal mid, decimal bps) => Round4(mid * (1 - bps / 10_000m));

        QuoteDto? Q(string code, string display, decimal bidRaw, decimal askRaw, decimal bps, DateTime ts)
        {
            if (bidRaw <= 0 && askRaw <= 0) return null;
            // Harem sitesiyle birebir: doğrudan API alış/satış (Spreads bps uygulanmaz)
            if (useRawHaremQuotes && bidRaw > 0 && askRaw > 0)
            {
                return new QuoteDto
                {
                    Code = code,
                    Display = display,
                    Bid = Round2(bidRaw),
                    Ask = Round2(askRaw),
                    Ts = ts
                };
            }

            decimal mid;
            if (bidRaw > 0 && askRaw > 0) mid = (bidRaw + askRaw) / 2m;
            else if (askRaw > 0) mid = askRaw;
            else mid = bidRaw;
            if (mid <= 0) return null;
            return new QuoteDto
            {
                Code = code,
                Display = display,
                Bid = Round2(BpsDn(mid, bps)),
                Ask = Round2(BpsUp(mid, bps)),
                Ts = ts
            };
        }

        const decimal OZ_TO_GRAM = 31.1034768m;
        const decimal OZ_TO_KG = 32.1507466m;

        var fetch = await _harem.FetchPricesAsync(ct);
        LastHaremRowCount = fetch.Items.Count;
        var bySymbol = new Dictionary<string, HaremPriceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in fetch.Items)
        {
            var s = (row.Symbol ?? "").Trim();
            if (s.Length == 0) continue;
            bySymbol[s] = row;
        }

        bool TryHarem(string sym, out HaremPriceItem? row)
        {
            row = null;
            return bySymbol.TryGetValue(sym, out row) && row is not null;
        }

        var now = DateTime.UtcNow;
        var list = new List<QuoteDto>(64);
        var haremConsumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMapped(string haremSymbol, string outputCode, string display, decimal bps)
        {
            if (!TryHarem(haremSymbol, out var r) || r is null) return;
            var q = Q(outputCode, display, r.Bid, r.Ask, bps, now);
            if (q is null) return;
            list.Add(q);
            haremConsumed.Add(haremSymbol);
        }

        // --- Döviz (TRY çiftleri) ---
        AddMapped("USDTRY", "USD_TRY", "USD/TRY", fxBps);
        AddMapped("EURTRY", "EUR_TRY", "EUR/TRY", fxBps);
        AddMapped("GBPTRY", "GBP_TRY", "GBP/TRY", fxBps);
        AddMapped("AUDTRY", "AUD_TRY", "AUD/TRY", fxBps);
        AddMapped("CADTRY", "CAD_TRY", "CAD/TRY", fxBps);
        AddMapped("SARTRY", "SAR_TRY", "SAR/TRY", fxBps);
        AddMapped("EURGBP", "EUR_GBP", "EUR/GBP", fxBps);

        // --- Gram altın TRY (Has = ALTIN kaynağı) ---
        AddMapped("ALTIN", "G24_TRY", "Gram Altın (Has) TRY", goldBps);
        AddMapped("AYAR22", "G22_TRY", "22 Ayar Gram (TRY)", goldBps);
        AddMapped("AYAR14", "G14_TRY", "14 Ayar Gram (TRY)", goldBps);

        // --- Sarrafiye → uygulama kodları (TEK_* → TAM_*; GREMESE_* → GREMSE_*) ---
        AddMapped("CEYREK_YENI", "CEYREK_YENI", "Çeyrek Yeni", coinBps);
        AddMapped("CEYREK_ESKI", "CEYREK_ESKI", "Çeyrek Eski", coinBps);
        AddMapped("YARIM_YENI", "YARIM_YENI", "Yarım Yeni", coinBps);
        AddMapped("YARIM_ESKI", "YARIM_ESKI", "Yarım Eski", coinBps);
        AddMapped("TEK_YENI", "TAM_YENI", "Tam Yeni", coinBps);
        AddMapped("TEK_ESKI", "TAM_ESKI", "Tam Eski", coinBps);
        AddMapped("ATA_YENI", "ATA_YENI", "Ata Yeni", coinBps);
        AddMapped("ATA_ESKI", "ATA_ESKI", "Ata Eski", coinBps);
        AddMapped("ATA5_YENI", "ATA5_YENI", "Ata5 Yeni", coinBps);
        AddMapped("ATA5_ESKI", "ATA5_ESKI", "Ata5 Eski", coinBps);
        AddMapped("GREMESE_YENI", "GREMSE_YENI", "Gremse Yeni", coinBps);
        AddMapped("GREMESE_ESKI", "GREMSE_ESKI", "Gremse Eski", coinBps);
        AddMapped("KULCEALTIN", "KULCE_ALTIN", "Külçe Altın", coinBps);

        // --- Maden / ons (USD, EUR) ---
        AddMapped("XAUUSD", "XAU_OZ_USD", "Altın Ons (USD)", metalUsdBps);
        AddMapped("XAUEUR", "XAU_OZ_EUR", "Altın Ons (EUR)", metalUsdBps);
        AddMapped("XAUXAG", "XAU_XAG_RATIO", "Altın / Gümüş Oranı", fxBps);
        AddMapped("XAGUSD", "XAG_OZ_USD", "Gümüş Ons (USD)", metalUsdBps);
        AddMapped("GUMUSD", "GUM_OZ_USD", "Gümüş Ons (USD) [GUM]", metalUsdBps);
        AddMapped("GUMTRY", "XAG_GM_TRY", "Gümüş Gram (TRY)", silverBps);
        AddMapped("PLATIN", "PLATIN_TRY", "Platin (TL)", metalUsdBps);
        AddMapped("PALADYUM", "PALADYUM_TRY", "Paladyum (TL)", metalUsdBps);
        AddMapped("XPTUSD", "XPT_OZ_USD", "Platin Ons (USD)", metalUsdBps);
        AddMapped("XPDUSD", "XPD_OZ_USD", "Paladyum Ons (USD)", metalUsdBps);

        // Türetilmiş: gram altın USD, gram gümüş USD, EUR/USD, altın kg USD

        // Altın KG USD/EUR için önce Harem'in doğrudan kg sembollerini kullan.
        // Farklı sağlayıcı/sürüm varyasyonlarında sembol adları değişebildiği için
        // geniş bir alias kümesi ve son çare olarak pattern yakalama uygulanır.
        static bool LooksLikeDirectKgSymbol(string symbol, string? category, string quoteCcy)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            var s = symbol.Trim().ToUpperInvariant();
            if (!s.Contains("KG", StringComparison.Ordinal)) return false;
            if (!s.Contains(quoteCcy, StringComparison.Ordinal)) return false;
            var c = (category ?? string.Empty).Trim().ToUpperInvariant();

            // Gümüş/platin vb. kg enstrümanlarını ele.
            if (s.Contains("XAG", StringComparison.Ordinal) ||
                s.Contains("SILVER", StringComparison.Ordinal) ||
                s.Contains("GUMUS", StringComparison.Ordinal) ||
                s.Contains("GÜMÜŞ", StringComparison.Ordinal) ||
                s.Contains("XPT", StringComparison.Ordinal) ||
                s.Contains("XPD", StringComparison.Ordinal) ||
                s.Contains("PLATIN", StringComparison.Ordinal) ||
                s.Contains("PALADYUM", StringComparison.Ordinal))
                return false;

            // Açık altın işaretleri veya kategori altın/gold ise kabul.
            return s.Contains("XAU", StringComparison.Ordinal)
                   || s.Contains("ALTIN", StringComparison.Ordinal)
                   || c.Contains("ALTIN", StringComparison.Ordinal)
                   || c.Contains("GOLD", StringComparison.Ordinal)
                   // Bazı feed'ler sadece KGUSD/KGEUR gibi kısa sembol döndürebiliyor.
                   || s.Contains("KGUSD", StringComparison.Ordinal)
                   || s.Contains("KGEUR", StringComparison.Ordinal);
        }

        static IEnumerable<string> BuildDirectKgCandidates(string quoteCcy)
        {
            if (string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "USDKG", "KGUSD", "XAUKGUSD", "ALTINKGUSD", "ALTIN_KG_USD", "XAU_KG_USD", "XAUKG_USD"
                };
            }

            return new[]
            {
                "EURKG", "KGEUR", "XAUKGEUR", "ALTINKGEUR", "ALTIN_KG_EUR", "XAU_KG_EUR", "XAUKG_EUR"
            };
        }

        bool TryAddDirectKg(string quoteCcy, string outputCode, string display)
        {
            var candidates = new List<(string Sym, HaremPriceItem Row)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Önce bilinen alias listesi.
            foreach (var sym in BuildDirectKgCandidates(quoteCcy))
            {
                if (!TryHarem(sym, out var row) || row is null) continue;
                if (seen.Add(sym))
                    candidates.Add((sym, row));
            }

            // 2) Son çare: payload içinde pattern'e uyan ilk satır.
            foreach (var kv in bySymbol)
            {
                var sym = kv.Key;
                var row = kv.Value;
                if (!LooksLikeDirectKgSymbol(sym, row.Category, quoteCcy)) continue;
                if (seen.Add(sym))
                    candidates.Add((sym, row));
            }

            if (candidates.Count == 0) return false;

            _log.LogInformation(
                "KG adayları ({Code}): {Candidates}",
                outputCode,
                string.Join(" | ", candidates.Select(c =>
                    $"{c.Sym}:{c.Row.Bid}/{c.Row.Ask}" +
                    (string.IsNullOrWhiteSpace(c.Row.Category) ? "" : $"({c.Row.Category})"))));

            static decimal Mid(decimal bid, decimal ask)
            {
                if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
                return bid > 0m ? bid : ask;
            }

            // Referans: XAU ons fiyatından beklenen KG seviyesi.
            // Harem'de yanlış/ambiguous KG sembolü gelirse en yakın adayı seçmek için kullanılır.
            var referenceSymbol = string.Equals(quoteCcy, "EUR", StringComparison.OrdinalIgnoreCase) ? "XAUEUR" : "XAUUSD";
            var expectedKgMid = 0m;
            var expectedKgBid = 0m;
            var expectedKgAsk = 0m;
            if (TryHarem(referenceSymbol, out var refRow) && refRow is not null)
            {
                var refMid = Mid(refRow.Bid, refRow.Ask);
                if (refMid > 0m) expectedKgMid = refMid * OZ_TO_KG;
                if (refRow.Bid > 0m) expectedKgBid = refRow.Bid * OZ_TO_KG;
                if (refRow.Ask > 0m) expectedKgAsk = refRow.Ask * OZ_TO_KG;
            }

            (string Sym, HaremPriceItem Row) best = candidates[0];
            var bestDiff = decimal.MaxValue;
            foreach (var c in candidates)
            {
                var candMid = Mid(c.Row.Bid, c.Row.Ask);
                if (candMid <= 0m) continue;

                var diff = expectedKgMid > 0m
                    ? Math.Abs(candMid - expectedKgMid) / expectedKgMid
                    : 0m;

                if (diff < bestDiff)
                {
                    best = c;
                    bestDiff = diff;
                }
            }

            // Referansa çok uzaksa direkt sembol güvenilmez; türetmeye düş.
            // (örn. farklı enstrüman adı KG ile karışmış ise)
            if (expectedKgMid > 0m && bestDiff > directKgMaxRefDiffRatio)
            {
                var category = (best.Row.Category ?? string.Empty).Trim().ToUpperInvariant();
                if ((category.Contains("SARRAF", StringComparison.Ordinal) || category.Contains("SARRAFIYE", StringComparison.Ordinal))
                    && expectedKgBid > 0m
                    && expectedKgAsk > 0m)
                {
                    var isUsd = string.Equals(quoteCcy, "USD", StringComparison.OrdinalIgnoreCase);
                    var bidBlendRatio = isUsd ? directKgUsdBlendBidRatio : directKgEurBlendBidRatio;
                    var askBlendRatio = isUsd ? directKgUsdBlendAskRatio : directKgEurBlendAskRatio;
                    var blendedBid = expectedKgBid + (best.Row.Bid - expectedKgBid) * bidBlendRatio;
                    var blendedAsk = expectedKgAsk + (best.Row.Ask - expectedKgAsk) * askBlendRatio;
                    var qBlend = Q(outputCode, display, blendedBid, blendedAsk, metalUsdBps, now);
                    if (qBlend is not null)
                    {
                        list.Add(qBlend);
                        haremConsumed.Add(best.Sym);
                        _log.LogWarning(
                            "Direct KG sembolü blend edildi ({Code}). Sym={Sym}, diff={Diff:P2}, blendBid={BidBlend:P1}, blendAsk={AskBlend:P1}.",
                            outputCode, best.Sym, bestDiff, bidBlendRatio, askBlendRatio);
                        return true;
                    }
                }

                _log.LogWarning(
                    "Direct KG sembolü elendi ({Code}). En iyi aday {Sym} referanstan uzak (diff={Diff:P2}, max={Max:P2}). Türetilmiş KG kullanılacak.",
                    outputCode, best.Sym, bestDiff, directKgMaxRefDiffRatio);
                return false;
            }

            var qBest = Q(outputCode, display, best.Row.Bid, best.Row.Ask, metalUsdBps, now);
            if (qBest is null) return false;
            list.Add(qBest);
            haremConsumed.Add(best.Sym);
            _log.LogInformation(
                "Direct KG seçimi: {Code} -> {Sym} (Bid={Bid}, Ask={Ask}, RefDiff={Diff:P2})",
                outputCode, best.Sym, best.Row.Bid, best.Row.Ask, bestDiff == decimal.MaxValue ? 0m : bestDiff);
            return true;
        }

        var directKgUsdAdded = false;
        var directKgEurAdded = false;
        directKgUsdAdded = TryAddDirectKg("USD", "XAU_KG_USD", "Altın KG (USD)");
        directKgEurAdded = TryAddDirectKg("EUR", "XAU_KG_EUR", "Altın KG (EUR)");

        if (TryHarem("XAUUSD", out var xau) && xau is not null)
        {
            var xauBid = xau.Bid > 0m ? xau.Bid : xau.Ask;
            var xauAsk = xau.Ask > 0m ? xau.Ask : xau.Bid;
            if (xauBid > 0m && xauAsk > 0m)
            {
                var g24Bid = xauBid / OZ_TO_GRAM;
                var g24Ask = xauAsk / OZ_TO_GRAM;
                var qg = Q("G24_USD", "Gram Altın (USD)", g24Bid, g24Ask, goldBps, now);
                if (qg is not null) list.Add(qg);
                if (!directKgUsdAdded)
                {
                    var kgBid = xauBid * OZ_TO_KG;
                    var kgAsk = xauAsk * OZ_TO_KG;
                    var qkg = Q("XAU_KG_USD", "Altın KG (USD)", kgBid, kgAsk, metalUsdBps, now);
                    if (qkg is not null) list.Add(qkg);
                }
                if (qg is not null || !directKgUsdAdded) haremConsumed.Add("XAUUSD");
            }
        }

        if (TryHarem("XAGUSD", out var xag) && xag is not null)
        {
            var xagBid = xag.Bid > 0m ? xag.Bid : xag.Ask;
            var xagAsk = xag.Ask > 0m ? xag.Ask : xag.Bid;
            if (xagBid > 0m && xagAsk > 0m)
            {
                var gBidUsd = xagBid / OZ_TO_GRAM;
                var gAskUsd = xagAsk / OZ_TO_GRAM;
                var q = Q("XAG_GM_USD", "Gümüş Gram (USD)", gBidUsd, gAskUsd, silverBps, now);
                if (q is not null)
                {
                    list.Add(q);
                    haremConsumed.Add("XAGUSD");
                }
            }
        }

        if (TryHarem("USDTRY", out var us) && us is not null &&
            TryHarem("EURTRY", out var eu) && eu is not null)
        {
            var usdBid = us.Bid > 0m ? us.Bid : us.Ask;
            var usdAsk = us.Ask > 0m ? us.Ask : us.Bid;
            var eurBid = eu.Bid > 0m ? eu.Bid : eu.Ask;
            var eurAsk = eu.Ask > 0m ? eu.Ask : eu.Bid;
            if (usdBid > 0m && usdAsk > 0m && eurBid > 0m && eurAsk > 0m)
            {
                // Cross quote: EUR/USD bid = EURTRY bid / USDTRY ask; ask = EURTRY ask / USDTRY bid
                var eurusdBid = eurBid / usdAsk;
                var eurusdAsk = eurAsk / usdBid;
                var q = Q("EUR_USD", "EUR/USD", eurusdBid, eurusdAsk, fxBps, now);
                if (q is not null)
                {
                    list.Add(q);
                    haremConsumed.Add("USDTRY");
                    haremConsumed.Add("EURTRY");
                }
            }
        }

        if (!directKgEurAdded && TryHarem("XAUEUR", out var xe) && xe is not null)
        {
            var xauEurBid = xe.Bid > 0m ? xe.Bid : xe.Ask;
            var xauEurAsk = xe.Ask > 0m ? xe.Ask : xe.Bid;
            if (xauEurBid > 0m && xauEurAsk > 0m)
            {
                var kgBidEur = xauEurBid * OZ_TO_KG;
                var kgAskEur = xauEurAsk * OZ_TO_KG;
                var q = Q("XAU_KG_EUR", "Altın KG (EUR)", kgBidEur, kgAskEur, metalUsdBps, now);
                if (q is not null)
                {
                    list.Add(q);
                    haremConsumed.Add("XAUEUR");
                }
            }
        }

        // Harem'den gelen, yukarıda türetilmiş veya eşlenmemiş semboller (liste boş kalmasın)
        foreach (var row in fetch.Items)
        {
            var sym = (row.Symbol ?? "").Trim();
            if (sym.Length == 0) continue;
            if (haremConsumed.Contains(sym)) continue;
            var disp = string.IsNullOrWhiteSpace(row.Category) ? sym : $"{row.Category} · {sym}";
            var qx = Q(sym, disp, row.Bid, row.Ask, fxBps, now);
            if (qx is null) continue;
            list.Add(qx);
            haremConsumed.Add(sym);
        }

        _log.LogInformation("Harem ham satır: {Raw}, önbellek satır: {Out}", fetch.Items.Count, list.Count);

        if (list.Count == 0)
        {
            if (prevSnapshot.Items.Count > 0)
            {
                _log.LogWarning(
                    "Harem fiyatları boş/eksik döndü; mevcut cache korunuyor. PrevCount={PrevCount}, PrevAgeSec={PrevAge:N0}",
                    prevSnapshot.Items.Count,
                    prevSnapshot.AgeSeconds);
                return prevSnapshot.Items;
            }
            _log.LogWarning("Harem fiyat önbelleği boş kaldı (Upstream:HaremApi:ApiKey ve ağ çıkışını kontrol edin).");
        }
        else
            _log.LogInformation("Kur önbelleği güncellendi: {Count} satır.", list.Count);

        // Upstream geçici kısmi cevap verirse, kaybolan kurları kısa süreli önceki snapshot'tan taşı.
        var existingCodes = new HashSet<string>(list.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);
        var nowUtc = DateTime.UtcNow;
        var carried = 0;
        foreach (var prev in prevByCode.Values)
        {
            if (string.IsNullOrWhiteSpace(prev.Code)) continue;
            if (existingCodes.Contains(prev.Code)) continue;
            if (prev.Bid <= 0m && prev.Ask <= 0m) continue;
            var ageSec = (nowUtc - prev.Ts).TotalSeconds;
            if (ageSec > carryForwardMaxAgeSeconds) continue;

            list.Add(new QuoteDto
            {
                Code = prev.Code,
                Display = prev.Display,
                Bid = prev.Bid,
                Ask = prev.Ask,
                Ts = prev.Ts
            });
            existingCodes.Add(prev.Code);
            carried++;
        }
        if (carried > 0)
        {
            _log.LogWarning(
                "Kur refresh sırasında {CarryCount} adet eksik kod önceki cache'ten taşındı (maxAge={MaxAgeSec:N0}s).",
                carried,
                carryForwardMaxAgeSeconds);
        }

        _cache.SetAll(list);
        return list;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
