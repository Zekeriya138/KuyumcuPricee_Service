using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public interface IAiService
{
    Task<AiReplyResult> GetReplyAsync(string message, Guid? requestedTenantId, Guid? requestedBranchId, string? currentScreen, CancellationToken ct);
}

public sealed class AiReplyResult
{
    public string Reply { get; set; } = "";
    public AiActionResponse? Action { get; set; }
}

public sealed class AiActionResponse
{
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public int? TabIndex { get; set; }
    public string? TabName { get; set; }
}

public sealed class AiService : IAiService
{
    private enum ResponseTone
    {
        BusinessShort,
        TechnicalLong
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    public AiService(
        AppDbContext db,
        ITenantContext tenantContext,
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _db = db;
        _tenantContext = tenantContext;
        _httpContextAccessor = httpContextAccessor;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    public async Task<AiReplyResult> GetReplyAsync(string message, Guid? requestedTenantId, Guid? requestedBranchId, string? currentScreen, CancellationToken ct)
    {
        var tenantId = ResolveTenantId(requestedTenantId);
        var branchId = ResolveBranchId(requestedBranchId);

        var deterministicReply = await TryHandleDeterministicReplyAsync(message, tenantId, branchId, ct);
        if (!string.IsNullOrWhiteSpace(deterministicReply))
            return new AiReplyResult { Reply = deterministicReply };

        var navigationReply = TryHandleNavigationCommand(message, DetectTone(message));
        if (navigationReply is not null)
            return navigationReply;

        var contextPayload = await BuildContextPayloadAsync(tenantId, branchId, currentScreen, ct);
        var prompt = BuildPrompt(message, contextPayload);

        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return new AiReplyResult
            {
                Reply = "AI servisi icin OpenAI anahtari tanimli degil. Lutfen OpenAI:ApiKey ayarini yapin."
            };

        var endpoint = _configuration["OpenAI:Endpoint"]?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            endpoint = "https://api.openai.com/v1/chat/completions";

        var model = _configuration["OpenAI:Model"];
        if (string.IsNullOrWhiteSpace(model))
            model = "gpt-4o";

        var temperature = ResolveTemperature();

        var requestBody = new
        {
            model,
            temperature,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Sen bir kuyumcu otomasyon sistemi asistanisin. Kisa, teknik ve anlasilir cevap ver. Silme, guncelleme, para transferi gibi kritik islemleri asla otomatik yapma; sadece yonlendir."
                },
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var preview = responseText.Length > 500 ? responseText[..500] + "..." : responseText;
            throw new InvalidOperationException($"OpenAI istegi basarisiz: {(int)response.StatusCode} {response.ReasonPhrase}. {preview}");
        }

        var parsed = JsonSerializer.Deserialize<OpenAiChatCompletionResponse>(responseText, JsonOptions);
        var reply = parsed?.Choices?.FirstOrDefault()?.Message?.Content?.Trim();
        if (string.IsNullOrWhiteSpace(reply))
            return new AiReplyResult
            {
                Reply = "Su an anlamli bir AI cevabi olusturulamadi. Lutfen soruyu biraz daha netlestirin."
            };

        return new AiReplyResult { Reply = reply };
    }

    private Guid ResolveTenantId(Guid? requestedTenantId)
    {
        var activeTenantId = _tenantContext.TenantId;
        if (activeTenantId == Guid.Empty)
        {
            if (requestedTenantId.HasValue && requestedTenantId.Value != Guid.Empty)
                return requestedTenantId.Value;
            throw new InvalidOperationException("Tenant bilgisi bulunamadi.");
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != Guid.Empty && requestedTenantId.Value != activeTenantId)
            throw new UnauthorizedAccessException("Talep edilen tenant mevcut oturumla uyusmuyor.");

        return activeTenantId;
    }

    private Guid? ResolveBranchId(Guid? requestedBranchId)
    {
        var activeBranchId = _tenantContext.BranchId;
        if (activeBranchId.HasValue && activeBranchId.Value != Guid.Empty)
        {
            if (requestedBranchId.HasValue && requestedBranchId.Value != Guid.Empty && requestedBranchId.Value != activeBranchId.Value)
                throw new UnauthorizedAccessException("Talep edilen sube mevcut oturumla uyusmuyor.");
            return activeBranchId.Value;
        }

        if (requestedBranchId.HasValue && requestedBranchId.Value != Guid.Empty)
            return requestedBranchId.Value;

        if (_httpContextAccessor.HttpContext?.Request.Headers.TryGetValue("X-Branch-Id", out var hdr) == true &&
            Guid.TryParse(hdr.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private async Task<string?> TryHandleCategoryProfitReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksProfit = ContainsAny(q, "kar", "karlilik", "karli", "marj");
        var asksCategory = ContainsAny(q, "kategori", "urun grubu", "grup");
        if (!asksProfit || !asksCategory)
            return null;

        var monthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var asksMonthly = ContainsAny(q, "bu ay", "aylik", "ay", "bu ayki");
        var from = asksMonthly ? monthStart : DateTime.Now.AddDays(-30);

        var lines = await _db.SaleItems
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.Sale.BranchId == branchId.Value)
            .Where(x => x.Sale.CreatedAt >= from)
            .Select(x => new
            {
                x.ProductCode,
                x.Category,
                x.Quantity,
                x.LineTotal
            })
            .ToListAsync(ct);

        if (lines.Count == 0)
            return FormatStandardReply(
                "Raporlar",
                "Satis Analizi",
                "Secili zaman araliginda satis satiri bulunamadi.",
                "Tarih araligini genisletip tekrar sorgulayin.",
                "Yeni satislar geldikce kategori karlilik verisi otomatik guncellenir.",
                tone);

        var productCodes = lines.Select(x => x.ProductCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var costs = await _db.Products
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .Where(x => productCodes.Contains(x.ProductCode))
            .Select(x => new { x.ProductCode, UnitCost = x.Cost ?? 0m })
            .ToListAsync(ct);
        var costByCode = costs.ToDictionary(x => x.ProductCode, x => x.UnitCost, StringComparer.OrdinalIgnoreCase);

        var byCategory = lines
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Kategorisiz" : x.Category)
            .Select(g =>
            {
                var revenue = g.Sum(y => y.LineTotal);
                var estCost = g.Sum(y =>
                {
                    var unit = costByCode.TryGetValue(y.ProductCode ?? "", out var c) ? c : 0m;
                    return unit * (y.Quantity > 0m ? y.Quantity : 1m);
                });
                return new
                {
                    Category = g.Key,
                    Revenue = revenue,
                    Cost = estCost,
                    Profit = revenue - estCost
                };
            })
            .OrderByDescending(x => x.Profit)
            .ToList();

        var top = byCategory.FirstOrDefault();
        if (top is null)
            return null;

        var periodText = asksMonthly ? "Bu ay" : "Son 30 gun";
        return FormatStandardReply(
            "Raporlar",
            "Satis Analizi",
            $"{periodText} en cok kar getiren urun grubu: {top.Category}.",
            $"Tahmini ciro: {top.Revenue:N2} TL, tahmini maliyet: {top.Cost:N2} TL, tahmini brut kar: {top.Profit:N2} TL.",
            "Not: Karlilik, satis satirlari ve urun maliyet alanlarina gore hesaplanir; maliyet verisi olmayan satirlar 0 kabul edilir.",
            tone);
    }

    private async Task<string?> TryHandlePurchaseMetricsReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksPurchase = ContainsAny(q, "alis", "alislar", "alim", "alimlar", "toptanci alis", "musteri alis");
        var asksSummary = ContainsAny(q, "toplam", "ozet", "kac", "adet", "tutar", "hacim", "bu ay", "aylik");
        if (!asksPurchase || !asksSummary)
            return null;

        var monthStart = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var asksMonthly = ContainsAny(q, "bu ay", "aylik", "ay");

        var purchaseQuery = _db.Purchases
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value);

        if (asksMonthly)
            purchaseQuery = purchaseQuery.Where(x => x.CreatedAt >= monthStart);

        var purchaseRows = await purchaseQuery
            .Select(x => new { x.GrandTotal, x.TotalHas, x.PurchaseType })
            .ToListAsync(ct);

        if (purchaseRows.Count == 0)
            return FormatStandardReply(
                "Alis Islemleri",
                "Genel Ozet",
                "Secili zaman araliginda alis kaydi bulunamadi.",
                "Tarih/sube kapsamini genisletip tekrar sorgulayin.",
                "Alis kaydi olustukca ozetler otomatik guncellenir.",
                tone);

        var toptanciCount = purchaseRows.Count(x => x.PurchaseType == PurchaseType.Toptanci);
        var musteriCount = purchaseRows.Count(x => x.PurchaseType == PurchaseType.Musteri);

        return FormatStandardReply(
            "Alis Islemleri",
            "Genel Ozet",
            $"{(asksMonthly ? "Bu ay" : "Secili kapsamda")} toplam alis kaydi: {purchaseRows.Count}. Toplam alis tutari: {purchaseRows.Sum(x => x.GrandTotal):N2} TL.",
            $"Toplam has karsiligi: {purchaseRows.Sum(x => x.TotalHas ?? 0m):N4} gr.",
            $"Alis dagilimi -> Musteriden: {musteriCount}, Toptancidan: {toptanciCount}.",
            tone);
    }

    private async Task<string?> TryHandleNotesRemindersReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksNotes = ContainsAny(q, "not", "notlar", "hatirlatma", "hatirlatma", "reminder");
        if (!asksNotes)
            return null;

        var notes = await _db.BranchNotes
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(3)
            .Select(x => x.Title)
            .ToListAsync(ct);

        var nextReminder = await _db.BranchReminders
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderBy(x => x.NextRunAt)
            .Select(x => new { x.Title, x.NextRunAt })
            .FirstOrDefaultAsync(ct);

        var activeReminderCount = await _db.BranchReminders
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .CountAsync(ct);

        var notePreview = notes.Count > 0 ? string.Join(", ", notes) : "Kayitli not bulunamadi";
        var nextReminderText = nextReminder is null
            ? "Yaklasan aktif hatirlatma yok."
            : $"En yakin hatirlatma: {nextReminder.Title} ({nextReminder.NextRunAt:dd.MM.yyyy HH:mm}).";

        return FormatStandardReply(
            "Notlar/Hatirlatmalar",
            "Genel Ozet",
            $"Son not basliklari: {notePreview}.",
            $"Aktif hatirlatma sayisi: {activeReminderCount}.",
            nextReminderText,
            tone);
    }

    private static AiReplyResult? TryHandleNavigationCommand(string message, ResponseTone tone)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var q = NormalizeQuestion(message);
        var asksNavigation = ContainsAny(q, "gotur", "ac", "git", "gec", "yonlendir", "yonlendirir", "bakmak istiyorum", "goster");
        if (!asksNavigation)
            return null;

        if (ContainsAny(q, "hizli hesap", "hizli hesaplama", "hesaplama panel"))
        {
            return BuildNavigationReply(
                "Ana Sayfa",
                "Hizli Hesaplama",
                "Hizli hesaplama paneli aciliyor.",
                "Gram, milyem ve kur tipini secerek sonucu anlik gorebilirsiniz.",
                "Detayli satis/alis islemi icin ilgili modullere gecis yapabilirsiniz.",
                "hizlihesaplama",
                null,
                "Hizli Hesaplama",
                tone);
        }

        if (ContainsAny(q, "ayar", "ayarlar") && ContainsAny(q, "kur", "doviz"))
        {
            return BuildNavigationReply(
                "Ayarlar / Kullanicilar",
                "Kur Ayarlari",
                "Kur ayarlari sekmesi aciliyor.",
                "Gorunur kur kartlarini ve ozel adlarini bu sekmeden yonetebilirsiniz.",
                "Degisiklikten sonra Ana Sayfa kurlar kartina donup dogrulayabilirsiniz.",
                "ayarlar",
                2,
                "Kur Ayarlari",
                tone);
        }

        if (ContainsAny(q, "satis"))
        {
            int? tab = null;
            string tabName = "Iscilikli Altin/Gumus";
            if (ContainsAny(q, "doviz")) { tab = 1; tabName = "Doviz Satis"; }
            else if (ContainsAny(q, "ozel urun")) { tab = 2; tabName = "Ozel Urunler"; }
            else if (ContainsAny(q, "ziynet")) { tab = 3; tabName = "Ziynet Satisi"; }

            return BuildNavigationReply(
                "Satis Islemleri",
                tabName,
                "Satis ekrani aciliyor.",
                "Sekme acildiktan sonra urun secip sepete ekleyebilirsiniz.",
                "Satisi tamamlamadan once fiyat ve odeme yontemini kontrol edin.",
                "satis",
                tab,
                tabName,
                tone);
        }

        if (ContainsAny(q, "alis", "alim"))
        {
            int? tab = null;
            string tabName = "Hurda Alisi (Musteriden)";
            if (ContainsAny(q, "toptanci")) { tab = 1; tabName = "Toptanci alisi(Altin)"; }
            else if (ContainsAny(q, "ziynet")) { tab = 2; tabName = "Ziynet (ceyrek, yarim vb.)"; }
            else if (ContainsAny(q, "gumus")) { tab = 3; tabName = "Gumus(Kulce)"; }
            else if (ContainsAny(q, "ozel urun")) { tab = 4; tabName = "Ozel urunler (alis)"; }
            else if (ContainsAny(q, "doviz")) { tab = 5; tabName = "Doviz Alis"; }
            else if (ContainsAny(q, "hurda")) { tab = 0; tabName = "Hurda Alisi (Musteriden)"; }

            return BuildNavigationReply(
                "Alis Islemleri",
                tabName,
                "Alis ekrani aciliyor.",
                "Sekmede urun/kalem bilgilerini doldurun.",
                "Kayit oncesi tutar ve birim kontrolu yapin.",
                "alis",
                tab,
                tabName,
                tone);
        }

        if (ContainsAny(q, "stok", "depo", "hammadde", "hurda", "alis gecmisi", "gumus"))
        {
            int? tab = 0;
            string tabName = "Depo stok (hammadde)";
            if (ContainsAny(q, "hurda")) { tab = 1; tabName = "Hurda"; }
            else if (ContainsAny(q, "gumus")) { tab = 2; tabName = "Gumus"; }
            else if (ContainsAny(q, "alis gecmisi", "gecmis alis")) { tab = 3; tabName = "Alis gecmisi"; }

            return BuildNavigationReply(
                "Stok / Depo",
                tabName,
                "Stok / Depo ekrani aciliyor.",
                "Ayar bazli gram ve barkodlu/barkodsuz dagilimi kontrol edin.",
                "Ihtiyaca gore ilgili sekmede detay filtreleri uygulayin.",
                "stok",
                tab,
                tabName,
                tone);
        }

        if (ContainsAny(q, "urun kart", "setler", "tekil stok", "ziynet adetli", "ozel urunler"))
        {
            int? tab = 0;
            string tabName = "Tekil stok";
            if (ContainsAny(q, "ziynet")) { tab = 1; tabName = "Ziynet (adetli)"; }
            else if (ContainsAny(q, "ozel urun")) { tab = 2; tabName = "Ozel urunler"; }
            else if (ContainsAny(q, "gumus")) { tab = 3; tabName = "Gumus"; }
            else if (ContainsAny(q, "set")) { tab = 4; tabName = "Setler (altin-iscilikli)"; }

            return BuildNavigationReply(
                "Urun Kartlari",
                tabName,
                "Urun Kartlari ekrani aciliyor.",
                "Ilgili sekmede urunleri listeleyip duzenleyebilirsiniz.",
                "Kayit sonrasi stok etkisini kontrol edin.",
                "urunler",
                tab,
                tabName,
                tone);
        }
        if (ContainsAny(q, "tamir", "siparis", "onarim", "servis"))
        {
            return BuildNavigationReply(
                "Tamir Islemleri",
                "Siparis ve Is Emri",
                "Tamir Islemleri ekrani aciliyor.",
                "Bu ekranda tamir/siparis kayitlarini, tahmini teslim tarihini ve notlarini yonetebilirsiniz.",
                "Kaydi olusturduktan sonra durum takibini ayni ekrandan surdurun.",
                "tamir",
                null,
                "Siparis ve Is Emri",
                tone);
        }
        if (ContainsAny(q, "musteri"))
            return BuildNavigationReply("Musteriler", "Cari Listesi", "Musteriler ekrani aciliyor.", "Arama kutusundan ilgili cari karti secin.", "Finans detayindan borc/alacak hareketlerini inceleyin.", "musteriler", null, "Cari Listesi", tone);
        if (ContainsAny(q, "tedarikci", "toptanci"))
            return BuildNavigationReply("Tedarikciler", "Cari Listesi", "Tedarikciler ekrani aciliyor.", "Firma secip bakiye ve hareketleri acin.", "Alis odeme kayitlariyla capraz kontrol yapin.", "tedarikciler", null, "Cari Listesi", tone);
        if (ContainsAny(q, "kasa"))
            return BuildNavigationReply("Kasa / Gelir - Gider", "Kasa hesaplari", "Kasa ekrani aciliyor.", "Birim bazli kasa bakiyelerini kontrol edin.", "Gerekirse hareket listesinde filtre uygulayin.", "kasa", null, "Kasa hesaplari", tone);
        if (ContainsAny(q, "rapor", "bilanco", "satis analizi", "alis analizi", "stok performans", "musteri tedarikci", "genel finansal"))
        {
            int? tab = 0;
            string tabName = "Genel Finansal Ozet";
            if (ContainsAny(q, "satis analizi", "satis rapor")) { tab = 1; tabName = "Satis Analizi"; }
            else if (ContainsAny(q, "alis analizi", "alis rapor")) { tab = 2; tabName = "Alis Analizi"; }
            else if (ContainsAny(q, "stok performans", "urun stok performans", "olu stok")) { tab = 3; tabName = "Urun ve Stok Performansi"; }
            else if (ContainsAny(q, "musteri tedarikci", "tedarikci rapor", "musteri rapor")) { tab = 4; tabName = "Musteri ve Tedarikci"; }
            else if (ContainsAny(q, "bilanco")) { tab = 5; tabName = "Bilanco"; }

            return BuildNavigationReply(
                "Raporlar",
                tabName,
                "Raporlar ekrani aciliyor.",
                "Tarih ve sube filtresiyle analizi daraltin.",
                "Secili rapor sekmesinde metrikleri detaylandirin.",
                "raporlar",
                tab,
                tabName,
                tone);
        }
        if (ContainsAny(q, "e-fatura", "efatura", "e arsiv", "e-arsiv", "fatura"))
        {
            return BuildNavigationReply(
                "E-Fatura / E-Arsiv",
                "Genel",
                "E-Fatura / E-Arsiv ekrani aciliyor.",
                "Once ayarlari kaydedip baglanti testini basarili hale getirin.",
                "Sonra belge satirindan Onizle ve Gonder ile kesim/gonderim yapin.",
                "efatura",
                null,
                "Genel",
                tone);
        }
        if (ContainsAny(q, "not", "hatirlatma"))
            return BuildNavigationReply("Notlar/Hatirlatmalar", "Genel", "Notlar/Hatirlatmalar ekrani aciliyor.", "Notlari ve aktif hatirlatmalari bu ekrandan yonetebilirsiniz.", "Suresi gelen hatirlatmalari buradan onaylayin.", "notlar", null, "Genel", tone);

        return null;
    }

    private static AiReplyResult BuildNavigationReply(
        string screen,
        string tab,
        string step1,
        string step2,
        string step3,
        string target,
        int? tabIndex,
        string? tabName,
        ResponseTone tone)
    {
        return new AiReplyResult
        {
            Reply = FormatStandardReply(screen, tab, step1, step2, step3, tone),
            Action = new AiActionResponse
            {
                Type = "navigate",
                Target = target,
                TabIndex = tabIndex,
                TabName = tabName
            }
        };
    }

    private decimal ResolveTemperature()
    {
        var configured = _configuration["OpenAI:Temperature"];
        if (decimal.TryParse(configured, out var temp))
            return Math.Clamp(temp, 0m, 1m);
        return 0.2m;
    }

    private async Task<object> BuildContextPayloadAsync(Guid tenantId, Guid? branchId, string? currentScreen, CancellationToken ct)
    {
        var recentSales = await _db.SaleItems
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.Sale.BranchId == branchId.Value)
            .OrderByDescending(x => x.CreatedAt)
            .Take(5)
            .Select(x => new
            {
                x.ProductCode,
                x.ProductName,
                x.Quantity,
                x.LineTotal,
                x.CreatedAt
            })
            .ToListAsync(ct);

        var stockSummary = await _db.Stocks
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderByDescending(x => x.Quantity)
            .Take(5)
            .Select(x => new
            {
                ProductCode = x.Product.ProductCode,
                ProductName = x.Product.Name,
                x.Quantity
            })
            .ToListAsync(ct);

        var supplierSummary = await _db.SupplierBalances
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.Supplier.BranchId == branchId.Value)
            .Select(x => new
            {
                x.BalanceTL,
                x.BalanceUSD,
                x.BalanceEUR,
                x.BalanceHAS,
                x.BalanceGUMUS
            })
            .ToListAsync(ct);

        var supplierDebtCredit = new
        {
            TlNet = supplierSummary.Sum(x => x.BalanceTL),
            UsdNet = supplierSummary.Sum(x => x.BalanceUSD),
            EurNet = supplierSummary.Sum(x => x.BalanceEUR),
            HasNet = supplierSummary.Sum(x => x.BalanceHAS),
            GumusNet = supplierSummary.Sum(x => x.BalanceGUMUS)
        };

        var customerSummary = await _db.CustomerBalances
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.Customer.BranchId == branchId.Value)
            .Select(x => new
            {
                x.BalanceTL,
                x.BalanceUSD,
                x.BalanceEUR,
                x.BalanceGBP,
                x.BalanceHAS
            })
            .ToListAsync(ct);

        var cashSummary = await _db.CashAccounts
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .Select(x => new { x.AccountType, x.Currency, x.CurrentBalance })
            .ToListAsync(ct);
        cashSummary = cashSummary
            .Where(x =>
                string.Equals((x.AccountType ?? "").Trim(), "Kasa", StringComparison.OrdinalIgnoreCase) ||
                string.Equals((x.AccountType ?? "").Trim(), "Vault", StringComparison.OrdinalIgnoreCase) ||
                string.Equals((x.AccountType ?? "").Trim(), "PosBanka", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var scrapStocks = await _db.ScrapStocks
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .Select(x => new
            {
                x.Karat,
                x.WeightGram,
                x.PureGoldGram
            })
            .ToListAsync(ct);

        var depoStocks = await _db.DepoStoklar
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .Select(x => new
            {
                x.Ayar,
                x.TotalGram,
                x.BarcodedGram,
                x.UnbarcodedGram
            })
            .ToListAsync(ct);

        var purchaseSummaryRows = await _db.Purchases
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .Select(x => new { x.GrandTotal, x.TotalHas, x.PurchaseType, x.CreatedAt })
            .ToListAsync(ct);

        var noteSummary = await _db.BranchNotes
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(5)
            .Select(x => new { x.Title, x.UpdatedAt })
            .ToListAsync(ct);

        var reminderSummary = await _db.BranchReminders
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderBy(x => x.NextRunAt)
            .Take(5)
            .Select(x => new { x.Title, x.NextRunAt })
            .ToListAsync(ct);

        var knownScreens = new[]
        {
            "Ana Sayfa",
            "Giris",
            "Sube Secimi",
            "Satis Islemleri",
            "Alis Islemleri",
            "Urun Kartlari",
            "Stok / Depo",
            "Musteriler",
            "Tedarikciler",
            "Tamir Islemleri",
            "Kasa / Gelir - Gider",
            "Raporlar",
            "E-Fatura / E-Arsiv",
            "Gun Sonu Islemleri",
            "Ayarlar / Kullanicilar",
            "Notlar/Hatirlatmalar",
            "Kuyumcu AI Asistan"
        };

        var knownFlows = new[]
        {
            "Toplu alim kaydi: Alis Islemleri > Toptanci Alis sekmesinden yapilir.",
            "Barkod olusturma/tekil urun kaydi: Urun Kartlari ekraninda Yeni urun adiminda yapilir.",
            "Barkodlu-barkodsuz gram etkisi Stok / Depo ekraninda izlenir.",
            "Satis tarafi barkod okutma alani Satis Islemleri > Iscilikli Altin/Gumus sekmesindedir.",
            "Ozel urun satisi: Satis Islemleri > Ozel Urunler sekmesinden yapilir. Urun listeden veya barkodla sepete eklenir, sonra satis tamamlanir.",
            "Ozel urun alisi ve barkodlama: Alis Islemleri > Ozel urunler (alis) sekmesinden yapilir. Adet 1'den buyukse her birim icin ayri barkod uretilir.",
            "Ozel urun kontrol/duzenleme: Urun Kartlari > Ozel urunler sekmesinden yapilir.",
            "Hurda alisi: Alis Islemleri > Hurda Alisi (Musteriden) sekmesinden yapilir.",
            "Hurdadan barkodlu vitrine tasima veya hurda cikisi: Stok / Depo > Hurda sekmesinden yapilir.",
            "Ziynet alisi: Alis Islemleri > Ziynet (ceyrek, yarim vb.) sekmesinden yapilir.",
            "Ziynet satisi: Satis Islemleri > Ziynet Satisi sekmesinden yapilir.",
            "Doviz alisi: Alis Islemleri > Doviz Alis sekmesinden yapilir.",
            "Doviz satisi: Satis Islemleri > Doviz Satis sekmesinden yapilir.",
            "Set kontrol/duzenleme: Urun Kartlari > Setler (altin-iscilikli) sekmesinden yapilir.",
            "Set satisi: Satis Islemleri > Iscilikli Altin/Gumus sekmesinde barkod veya urun kodu ile sepete eklenerek yapilir.",
            "Satis sekmeleri: Iscilikli Altin/Gumus, Doviz Satis, Ozel Urunler, Ziynet Satisi.",
            "Alis sekmeleri: Hurda Alisi (Musteriden), Toptanci alisi(Altin), Ziynet (ceyrek, yarim vb.), Gumus(Kulce), Ozel urunler (alis), Doviz Alis.",
            "Stok/Depo sekmeleri: Depo stok (hammadde), Hurda, Gumus, Alis gecmisi.",
            "E-Fatura sureci: E-Fatura / E-Arsiv ekraninda ayarlari gir, baglantiyi test et, sonra taslagi Onizle ve Gonder ile gonder.",
            "E-Fatura entegratoru: profilde ProviderCode bos ise varsayilan EDM kullanilir.",
            "Musteri/Tedarikci akisi: liste > detay > islem adiminda cari hareket ve bakiyeler guncellenir.",
            "Kasa akisi: birim bazli bakiye ve hareketler Kasa / Gelir - Gider ekraninda izlenir.",
            "Rapor akisi: Raporlar ekraninda secili tab ve tarih araligina gore analiz ve disa aktarim yapilir.",
            "Ayarlar akisi: Kullanicilar, Subeler, Kur Ayarlari ve Abonelik sekmeleri rol/abonelik kurallarina gore acilir.",
            "Giris akisi: giris ve sube secimi tamamlanmadan islem ekranlari sube zorunlulugu nedeniyle calismaz."
        };

        var knownTabs = new
        {
            SatisIslemleri = new[]
            {
                "Iscilikli Altin/Gumus",
                "Doviz Satis",
                "Ozel Urunler",
                "Ziynet Satisi"
            },
            AlisIslemleri = new[]
            {
                "Hurda Alisi (Musteriden)",
                "Toptanci alisi(Altin)",
                "Ziynet (ceyrek, yarim vb.)",
                "Gumus(Kulce)",
                "Ozel urunler (alis)",
                "Doviz Alis"
            },
            UrunKartlari = new[]
            {
                "Tekil stok",
                "Ziynet (adetli)",
                "Ozel urunler",
                "Gumus",
                "Setler (altin-iscilikli)"
            },
            StokDepo = new[]
            {
                "Depo stok (hammadde)",
                "Hurda",
                "Gumus",
                "Alis gecmisi"
            },
            Raporlar = new[]
            {
                "Genel Finansal Ozet",
                "Satis Analizi",
                "Alis Analizi",
                "Urun ve Stok Performansi",
                "Musteri ve Tedarikci",
                "Bilanco"
            },
            Ayarlar = new[]
            {
                "Kullanicilar",
                "Subeler",
                "Kur Ayarlari",
                "Abonelikler"
            },
            EFaturaEArsiv = new[]
            {
                "Profil Ayarlari",
                "Belge Durumu (EDM/GIB)",
                "Onizle ve Gonder",
                "Iptal Talebi"
            }
        };

        return new
        {
            TenantId = tenantId,
            BranchId = branchId,
            CurrentScreen = string.IsNullOrWhiteSpace(currentScreen) ? "Bilinmiyor" : currentScreen,
            KnownScreens = knownScreens,
            KnownTabs = knownTabs,
            KnownFlows = knownFlows,
            RecentSales = recentSales,
            StockSummary = stockSummary,
            SupplierDebtCredit = supplierDebtCredit,
            CustomerDebtCredit = new
            {
                TlNet = customerSummary.Sum(x => x.BalanceTL),
                UsdNet = customerSummary.Sum(x => x.BalanceUSD),
                EurNet = customerSummary.Sum(x => x.BalanceEUR),
                GbpNet = customerSummary.Sum(x => x.BalanceGBP),
                HasNet = customerSummary.Sum(x => x.BalanceHAS)
            },
            CashBalances = new
            {
                Tl = cashSummary.Where(x => string.Equals((x.Currency ?? "").Trim(), "TL", StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentBalance),
                Usd = cashSummary.Where(x => string.Equals((x.Currency ?? "").Trim(), "USD", StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentBalance),
                Eur = cashSummary.Where(x => string.Equals((x.Currency ?? "").Trim(), "EUR", StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentBalance),
                Has = cashSummary.Where(x => string.Equals((x.Currency ?? "").Trim(), "HAS", StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentBalance),
                Gumus = cashSummary.Where(x => string.Equals((x.Currency ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentBalance)
            },
            ScrapStockSummary = new
            {
                TotalHurdaGram = scrapStocks.Sum(x => x.WeightGram),
                TotalHurdaHasGram = scrapStocks.Sum(x => x.PureGoldGram),
                ByAyar = scrapStocks
                    .GroupBy(x => x.Karat)
                    .Select(g => new
                    {
                        Ayar = g.Key,
                        HurdaGram = g.Sum(y => y.WeightGram),
                        HasGram = g.Sum(y => y.PureGoldGram)
                    })
                    .OrderByDescending(x => x.HurdaGram)
                    .ToList()
            },
            DepoStockSummary = new
            {
                TotalGram = depoStocks.Sum(x => x.TotalGram),
                TotalBarcodedGram = depoStocks.Sum(x => x.BarcodedGram),
                TotalUnbarcodedGram = depoStocks.Sum(x => x.UnbarcodedGram),
                ByAyar = depoStocks
                    .GroupBy(x => x.Ayar)
                    .Select(g => new
                    {
                        Ayar = g.Key,
                        TotalGram = g.Sum(y => y.TotalGram),
                        BarcodedGram = g.Sum(y => y.BarcodedGram),
                        UnbarcodedGram = g.Sum(y => y.UnbarcodedGram)
                    })
                    .OrderByDescending(x => x.TotalGram)
                    .ToList()
            },
            PurchaseSummary = new
            {
                TotalCount = purchaseSummaryRows.Count,
                TotalGrandTl = purchaseSummaryRows.Sum(x => x.GrandTotal),
                TotalHas = purchaseSummaryRows.Sum(x => x.TotalHas ?? 0m),
                ToptanciCount = purchaseSummaryRows.Count(x => x.PurchaseType == PurchaseType.Toptanci),
                MusteriCount = purchaseSummaryRows.Count(x => x.PurchaseType == PurchaseType.Musteri)
            },
            NotesSummary = noteSummary,
            RemindersSummary = reminderSummary
        };
    }

    private static string BuildPrompt(string message, object contextPayload)
    {
        var tone = DetectTone(message);
        var toneRule = tone == ResponseTone.TechnicalLong
            ? "Kullanici teknik detay istiyor: formulu, hesap adimlarini ve kritik kosullari acikla."
            : "Varsayilan stil: isletme dilinde kisa, net ve pratik yanit ver.";
        var contextJson = JsonSerializer.Serialize(contextPayload, new JsonSerializerOptions { WriteIndented = true });
        return
            "Sen bir kuyumcu otomasyon sistemi asistanisin.\n\n" +
            "Sistem ozellikleri:\n" +
            "- Stok yonetimi\n" +
            "- Urun barkodlama\n" +
            "- Tedarikci yonetimi\n" +
            "- Satis ve alis islemleri\n" +
            "- Hurda altin islemleri\n" +
            "- Finansal takip (TL, USD, EURO, HAS, GUMUS)\n\n" +
            "Kurallar:\n" +
            "- Teknik ama anlasilir cevap ver\n" +
            "- Gereksiz uzun konusma\n" +
            "- Hesap gerekiyorsa yap\n" +
            "- Kullaniciyi yonlendir\n" +
            "- Kritik/islemsel adimlarda komut calistirma, sadece rehberlik et\n" +
            "- Uygulamada var olmayan ekran/sekme ismi uydurma\n" +
            "- Sadece KnownScreens listesindeki ekran adlarini kullan\n" +
            "- Sekme sorularinda sadece KnownTabs ve KnownFlows verisini referans al\n" +
            $"- Ton kurali: {toneRule}\n" +
            "- Cevabi adim adim degil, normal akici metin/paragraf olarak yaz\n" +
            "- Sayisal/veri sorularinda tenant ve sube kapsamindaki degerleri net sayi olarak ver, tahmin etme\n" +
            "- Ekran adindan emin degilsen yeni isim icat etme, kullanicidan ekran adini teyit etmesini iste\n\n" +
            "Veriler:\n" +
            contextJson + "\n\n" +
            "Kullanici sorusu:\n" +
            message;
    }

    private async Task<string?> TryHandleDeterministicReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var tone = DetectTone(message);

        var categoryProfitReply = await TryHandleCategoryProfitReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(categoryProfitReply))
            return categoryProfitReply;

        var balanceReply = await TryHandleBalanceMetricsReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(balanceReply))
            return balanceReply;

        var purchasesReply = await TryHandlePurchaseMetricsReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(purchasesReply))
            return purchasesReply;

        var eInvoiceReply = await TryHandleEInvoiceReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(eInvoiceReply))
            return eInvoiceReply;

        var authReply = TryHandleAuthGuideReply(message, tone);
        if (!string.IsNullOrWhiteSpace(authReply))
            return authReply;

        var moduleGuideReply = TryHandleModuleGuideReply(message, tone);
        if (!string.IsNullOrWhiteSpace(moduleGuideReply))
            return moduleGuideReply;

        var logicReply = TryHandleCalculationLogicReply(message, tone);
        if (!string.IsNullOrWhiteSpace(logicReply))
            return logicReply;

        var countReply = await TryHandleEntityCountReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(countReply))
            return countReply;

        var cashReply = await TryHandleCashMetricsReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(cashReply))
            return cashReply;

        var profitReply = await TryHandleRecentProfitReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(profitReply))
            return profitReply;

        var stockReply = await TryHandleStockMetricsReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(stockReply))
            return stockReply;

        var notesReply = await TryHandleNotesRemindersReplyAsync(message, tenantId, branchId, ct, tone);
        if (!string.IsNullOrWhiteSpace(notesReply))
            return notesReply;

        return TryHandleKnownFlow(message, tone);
    }

    private async Task<string?> TryHandleBalanceMetricsReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksBalance = ContainsAny(
            q,
            "bakiye", "bakiyem", "borc", "borcum", "alacak", "alacagim",
            "cari", "cari hesap", "veresiye", "hesap durumu", "durum",
            "finans", "finansal");
        var asksCustomerScope = ContainsAny(q, "musteri", "musteriler", "musteride", "musterilerde");
        var asksSupplierScope = ContainsAny(q, "tedarikci", "tedarikciler", "toptanci", "toptancida", "firmada", "tedarikcide");
        var looseNameTokens = ExtractNameTokensLoose(message);
        var canBeSpecificCounterpartyQuery = asksBalance && looseNameTokens.Count >= 2;

        if (asksBalance && (asksCustomerScope || canBeSpecificCounterpartyQuery))
        {
            var customerNameTokens = ExtractNameTokensBeforeMarkers(message, "musteri");
            if (customerNameTokens.Count == 0)
                customerNameTokens = looseNameTokens;
            if (customerNameTokens.Count > 0)
            {
                var customers = await _db.Customers
                    .AsNoTracking()
                    .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                    .Where(x => branchId == null || x.BranchId == branchId.Value)
                    .Select(x => new { x.Id, x.FullName })
                    .ToListAsync(ct);

                var bestMatch = customers
                    .Select(c => new
                    {
                        Customer = c,
                        Score = customerNameTokens.Count(t => NormalizeQuestion(c.FullName).Contains(t, StringComparison.Ordinal))
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Customer.FullName.Length)
                    .FirstOrDefault();

                if (bestMatch is not null)
                {
                    var bal = await _db.CustomerBalances
                        .AsNoTracking()
                        .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CustomerId == bestMatch.Customer.Id)
                        .Select(x => new { x.BalanceTL, x.BalanceUSD, x.BalanceEUR, x.BalanceGBP, x.BalanceHAS })
                        .FirstOrDefaultAsync(ct);

                    if (bal is not null)
                    {
                        return FormatStandardReply(
                            "Musteriler",
                            "Finansal Bilgiler",
                            $"{bestMatch.Customer.FullName} cari durumu -> TL: {bal.BalanceTL:N2}, USD: {bal.BalanceUSD:N4}, EUR: {bal.BalanceEUR:N4}, GBP: {bal.BalanceGBP:N4}.",
                            $"{bestMatch.Customer.FullName} HAS bakiyesi: {bal.BalanceHAS:N4} gr.",
                            "Detay hareketler icin Musteriler ekraninda ilgili musteri kartinin finansal detayini acin.",
                            tone);
                    }
                }
            }

            var rows = await _db.CustomerBalances
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => branchId == null || x.Customer.BranchId == branchId.Value)
                .Select(x => new { x.BalanceTL, x.BalanceUSD, x.BalanceEUR, x.BalanceGBP, x.BalanceHAS })
                .ToListAsync(ct);

            return FormatStandardReply(
                "Musteriler",
                "Finansal Bilgiler",
                $"Musteri cari toplamlari -> TL: {rows.Sum(x => x.BalanceTL):N2}, USD: {rows.Sum(x => x.BalanceUSD):N4}, EUR: {rows.Sum(x => x.BalanceEUR):N4}, GBP: {rows.Sum(x => x.BalanceGBP):N4}.",
                $"Musteri HAS bakiyesi: {rows.Sum(x => x.BalanceHAS):N4} gr.",
                "Detay musteri bazinda kontrol icin Musteriler ekraninda ilgili kartin finansal sekmesine girin.",
                tone);
        }

        if (asksBalance && asksSupplierScope)
        {
            var rows = await _db.SupplierBalances
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => branchId == null || x.Supplier.BranchId == branchId.Value)
                .Select(x => new { x.BalanceTL, x.BalanceUSD, x.BalanceEUR, x.BalanceGBP, x.BalanceHAS, x.BalanceGUMUS })
                .ToListAsync(ct);

            return FormatStandardReply(
                "Tedarikciler",
                "Finansal Bilgiler",
                $"Tedarikci cari toplamlari -> TL: {rows.Sum(x => x.BalanceTL):N2}, USD: {rows.Sum(x => x.BalanceUSD):N4}, EUR: {rows.Sum(x => x.BalanceEUR):N4}, GBP: {rows.Sum(x => x.BalanceGBP):N4}.",
                $"Tedarikci metal bakiyeleri -> HAS: {rows.Sum(x => x.BalanceHAS):N4} gr, GUMUS: {rows.Sum(x => x.BalanceGUMUS):N4} gr.",
                "Detay tedarikci bazinda kontrol icin Tedarikciler ekranindaki cari hareketleri inceleyin.",
                tone);
        }

        return null;
    }

    private static string? TryHandleCalculationLogicReply(string message, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksLogic = ContainsAny(q, "hesap", "hesaplaniyor", "hesaplama", "mantik", "nasil", "nedir");
        if (!asksLogic)
            return null;

        if (ContainsAny(q, "toptanci", "toptanci alis", "toplu alis"))
        {
            var text =
                "Ekran > Sekme: Alis Islemleri > Toptanci alisi(Altin)\n" +
                "Mal tanimini sectiginizde o mal tanimina bagli ayar (milyem) otomatik gelir; mal tanimi listede yoksa kategori alaninin yanindaki 3 nokta ile hemen duzenleyebilirsiniz.\n" +
                "Gram ve birim maliyet girildiginde satir hesaplamasi yapilir. Pratik hesap: (toplam gram x milyem + toplam gram x birim maliyet) x has alis kuru; vergi ve indirim varsa satir tutarina ayrica uygulanir.\n" +
                "Kaydetten sonra depo mantigi calisir: toplam gram ve barkodsuz gram artar, ortalama maliyet agirlikli ortalama ile guncellenir: (mevcut gram x mevcut maliyet + yeni gram x yeni maliyet) / yeni toplam gram.";
            return tone == ResponseTone.TechnicalLong ? text : ToBusinessShort(text);
        }

        if (ContainsAny(q, "hurda alis", "hurda alisini", "hurdanin alisi", "musteriden hurda"))
        {
            var text =
                "Ekran > Sekme: Alis Islemleri > Hurda Alisi (Musteriden)\n" +
                "Hurda alisinda temel hesap has karsiligidir: has = gram x saflik orani (milyem/1000). Ornek: 100 gram, 585 milyem ise has karsiligi 58,5 gram olur.\n" +
                "Odeme tutari bu has degeri ve anlik has alis kuru ile belirlenir. Ornek: 58,5 has x 4.000 TL = 234.000 TL.\n" +
                "Kaydetten sonra sistem ilgili ayarin hurda gram ve has toplamini artirir; boylece kalan hurda ve toplam has bakiyesi anlik guncel kalir.";
            return tone == ResponseTone.TechnicalLong ? text : ToBusinessShort(text);
        }

        if (ContainsAny(q, "hurdadan vitrine", "hurda vitrine", "vitrine urun", "hurdadan urun cikar", "hurdadan barkod"))
        {
            var text =
                "Ekran > Sekme: Stok / Depo > Hurda (ve barkodlama islemi)\n" +
                "Sistem once secilen hurda satirinda kalan gramı kontrol eder. Ornek: satir 120 gram, daha once vitrine 30 gram ciktiysa kalan 90 gramdir; 90 gramdan fazla cikis izin vermez.\n" +
                "Vitrine cikis yapildiginda hurda gramdan duser ve yerine barkodlu tekil urun olusur; yani hurda azalir, vitrindeki barkodlu urun adedi/grami artar.\n" +
                "Bu urun hurdadan uretildigi icin satis aninda ayni gram ikinci kez depodan dusulmez; sistem cifte dusumu engelleyerek stok dogrulugunu korur.";
            return tone == ResponseTone.TechnicalLong ? text : ToBusinessShort(text);
        }

        return null;
    }

    private async Task<string?> TryHandleEInvoiceReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksEInvoice = ContainsAny(
            q,
            "e-fatura", "efatura", "e arsiv", "e-arsiv", "fatura",
            "edm", "senderlabel", "session_id", "return_code", "deadletter",
            "belge durumu", "gib", "outbox", "ubl");
        if (!asksEInvoice)
            return null;

        var asksPurpose = ContainsAny(q, "ne ise yariyor", "ne ise yarar", "ne icin", "amac", "anlat", "ekran");
        var asksIntegrator = ContainsAny(q, "entegrator", "integrator", "provider", "edm", "hangi servis");
        var asksStatus = ContainsAny(q, "durum", "status", "kuyrukta", "gonderildi", "teslim", "reddedildi", "iptal");
        var asksError = ContainsAny(q, "hata", "error", "anlami", "ne demek", "neden", "cozum", "failed", "rejected", "deadletter", "session", "senderlabel", "return_code", "vkn", "tckn");

        if (!asksPurpose && !asksIntegrator && !asksStatus && !asksError)
            return null;

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile!.ProviderCode.Trim().ToLowerInvariant();
        var providerText = providerCode == "edm" ? "EDM" : providerCode.ToUpperInvariant();
        var isActive = profile?.IsActive == true ? "aktif" : "pasif";

        if (asksPurpose)
        {
            return FormatStandardReply(
                "E-Fatura / E-Arsiv",
                "Genel",
                "Bu ekranin amaci e-fatura/e-arsiv profil ayarlarini yonetmek, cikmis belgeleri izlemek ve belgeyi Onizle ve Gonder ile manuel olarak kesip gondermektir.",
                "Is akisinda once Vergi/Firma + EDM kimlik bilgileri kaydedilir, sonra Baglanti Test Et basarili olmadan gonderim yapilmaz.",
                $"Belge listesinde durum, son hata, UUID/ETTN ve outbox tekrar sayisi izlenir; secili profilde entegrator {providerText} ve profil durumu su an {isActive} gorunuyor.",
                tone);
        }

        if (asksIntegrator)
        {
            return FormatStandardReply(
                "E-Fatura / E-Arsiv",
                "EDM API Kimlik Bilgileri",
                $"Sistemde e-fatura provider kodu profilde ayarlanir; bos ise varsayilan olarak EDM kullanilir. Mevcut profil cozumu: {providerText}.",
                "Gonderim icin EDM kullanici adi, EDM sifresi (secret) ve SenderLabel zorunludur; bu alanlardan biri eksikse API gonderimi bilincli olarak durdurur.",
                "Musteri VKN/TCKN sonucuna gore belge tipi EFatura veya EArsiv secilir; EFatura icin 10 hane VKN ve alici etiketi zorunludur.",
                tone);
        }

        if (asksStatus && !asksError)
        {
            return FormatStandardReply(
                "E-Fatura / E-Arsiv",
                "Belge Durumu (EDM/GIB)",
                "Taslak belge olustu ama gonderilmedi; Kuyrukta belge outboxa alindi; Gonderildi EDM/GIB tarafina ulasti; Teslim Edildi nihai basariyi ifade eder.",
                "Reddedildi belge kural/icerik nedeniyle geri dondu; Hata gonderim veya durum sorgusunda teknik/is kurali sorunu oldugunu gosterir; Iptal Edildi basarili iptal sonucudur.",
                "DeadLetter, outbox deneme sayisinin limiti astigini (tekrar tekrar hata aldigini) belirtir ve manuel kontrol gerektirir.",
                tone);
        }

        return FormatStandardReply(
            "E-Fatura / E-Arsiv",
            "Hata Yonetimi",
            "SESSION_ID alinamadi/login basarisiz hatasi EDM kimlik dogrulamasi sorunudur; kullanici adi-sifreyi ve EDM servis erisimini kontrol edin.",
            "SenderLabel zorunlu veya senderAlias bos hatalari gonderici etiketi eksikligini; alici etiketi zorunlu hatasi ise EFatura tipinde receiver alias olmadigini gosterir.",
            "RETURN_CODE!=0, VKN/TCKN uyusmazligi, secili sube-belge subesi farki, DeadLetter veya durum zaman asimi hatalarinda belgeyi Onizle ve Gonder ekraninda duzeltip tekrar kuyruga alin.",
            tone);
    }

    private static string? TryHandleModuleGuideReply(string message, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksGuide = ContainsAny(
            q,
            "ne ise yariyor", "ne ise yarar", "ne icin", "amac", "ekran", "modul",
            "nasil kullan", "akisi", "is akis", "hangi hata", "hata ne demek", "hata anlami", "hatalar");
        if (!asksGuide)
            return null;

        if (ContainsAny(q, "aiasistant", "aiassistant", "ai asistan", "copilot"))
        {
            return FormatStandardReply(
                "Kuyumcu AI Asistan",
                "Sohbet",
                "Bu modul kullanicidan soruyu alip API uzerinden tenant/sube baglaminda yanit alir; gerekli durumda ekran yonlendirme komutu da uygular.",
                "Akista mesaj, yukleniyor karti ve sonuc tek sohbet listesinde tutulur; uygun hedef donerse Shell seviyesinde ilgili ekrana otomatik gecilir.",
                "Yaygin hata: 'AI cevabi alinirken bir hata olustu' ifadesi genelde API/OpenAI baglantisi veya zaman asimi kaynaklidir; once ag ve API durumunu kontrol edin.",
                tone);
        }

        if (ContainsAny(q, "alisviewmodel", "alis islemleri", "dovizalis", "doviz alis odeme", "ozel urunler alis", "purchasepayment"))
        {
            return FormatStandardReply(
                "Alis Islemleri",
                "Hurda/Toptanci/Ziynet/Gumus/Ozel urunler/Doviz Alis",
                "Bu modul tum alim akislarini tab bazli yonetir; secilen taba gore kur, has ve toplam hesaplari yenilenir, odeme dagilimi ilgili odeme pencerelerinde tamamlanir.",
                "Doviz alis tarafinda birim fiyat alis kuru (bid) mantigiyla ilerler; ozel urun alisinda barkod/tekil kayit uretilir, toptanci ve hurda akisinda depo/hurda bakiyesi etkilenir.",
                "Yaygin hatalar: 'Sube secilmedi', 'Musteri/Tedarikci secimi zorunludur', 'Kur yuklenemedi', 'Alis kaydi hatasi'; bu mesajlar genelde eksik secim veya kur/veri erisim sorunu anlamina gelir.",
                tone);
        }

        if (ContainsAny(q, "satisviewmodel", "satis onizleme", "odeme", "paymentmethod", "ozelurunsatisodeme", "satis islemleri"))
        {
            return FormatStandardReply(
                "Satis Islemleri",
                "Iscilikli Altin/Gumus, Doviz Satis, Ozel Urunler, Ziynet Satisi",
                "Bu modul sepete ekleme, odeme dagitimi, teslim/emanet kurallari ve satis kaydini tek merkezden yonetir; ziynet ve ozel urun senaryolari ayri is kurallariyla ele alinir.",
                "Satis onizleme ve odeme adimlarinda kalemlerin finansal etkisi dogrulanir, sonra kasa/cari hareketleri yazilarak satis tamamlanir.",
                "Yaygin hatalar: 'Barkod ile urun bulunamadi', 'Satis icin odeme kalemi olusturulamadi', 'Ziynet birim satis fiyati bulunamadi', 'Sube secilmedi'; bu mesajlar stok/esleme/odeme dagilimi tutarsizligini gosterir.",
                tone);
        }

        if (ContainsAny(q, "stokdepo", "urunkartlari", "urunekleme", "urunduzenle", "ziyneturunekleme", "hurdacikis", "hurdasonislemler"))
        {
            return FormatStandardReply(
                "Stok / Depo + Urun Kartlari",
                "Depo/Hurda/Gumus/Alis gecmisi ve urun kart tablari",
                "Bu moduller urun kaydi, barkodlama, hurdadan vitrine gecis, depo gram dagilimi ve urun karti duzenleme sureclerini yonetir.",
                "Urun kartlari tarafinda taba gore izinler degisir; ornegin gumus/set tarafinda hizli ekleme kisitlari vardir, ozel urun ve ziynet kart olusturma alis akisina baglidir.",
                "Yaygin hatalar: 'Urun adi zorunludur', 'Sube bilgisi bulunamadi', 'DepoStokHavuz kaydi bulunamadi', 'Urun eklenemedi'; bu mesajlar veri zorunlulugu veya depo esleme eksigi anlamina gelir.",
                tone);
        }

        if (ContainsAny(q, "musteri", "musteridetay", "musteriislem", "musteritahsilat", "tedarikci", "tedarikcidetay", "tedarikciislem"))
        {
            return FormatStandardReply(
                "Musteriler / Tedarikciler",
                "Liste, Detay, Islem",
                "Bu moduller cari kart yonetimi, borc-alacak hareketleri ve tahsilat/odeme kayitlarini tenant/sube kapsaminda takip eder.",
                "Detay ekranlari rapor/word cikti ve finansal ozetle birlikte calisir; islem ekranlari secilen cari uzerinden hareket olusturur ve bakiyeyi etkiler.",
                "Yaygin hatalar: 'Musteri/Tedarikci bulunamadi', 'Ad Soyad/Firma zorunludur', 'Islem kaydi basarisiz', 'Kur bilgisi bulunamadi'; bu mesajlar secim eksigi veya finansal veri tutarsizligina isaret eder.",
                tone);
        }

        if (ContainsAny(q, "kasa", "balancesheet", "raporlar", "gunsonu"))
        {
            return FormatStandardReply(
                "Kasa / Raporlar / Gun Sonu",
                "Finansal Ozet ve Kapanis",
                "Bu moduller kasa bakiyeleri, tablo/analiz raporlari ve gun sonu toplu kontrol-kapanis islerini yonetir.",
                "Raporlar sekmesinde aktif tab bazli metrikler uretilir ve excel/docx/pdf ciktilari alinabilir; kasa ekrani birim bazli bakiye ve hareket gorunumu saglar.",
                "Yaygin hatalar: 'Sube secimi bulunamadi', 'Veri bulunamadi', 'Trend hatasi'; bu mesajlar rapor filtresi veya secili sube baglaminin eksik/uygunsuz oldugunu gosterir.",
                tone);
        }

        if (ContainsAny(q, "login", "kayitgiris", "resetpassword", "isletmekayit", "branchselection", "sube secim"))
        {
            return FormatStandardReply(
                "Giris / Kayit / Sube Secimi",
                "Kimlik ve Isletme Baslangic",
                "Bu moduller kullanici girisi, sifre sifirlama, isletme kaydi ve oturum sonrasi sube secimi akislarini yonetir; sonraki tum ekranlar bu baglamla calisir.",
                "Sube secimi basarili olmadan finansal/stok modullerine gecis yapilmamasi gerekir; aksi halde moduller secili sube zorunlulugu hatasi verir.",
                "Yaygin hatalar: 'Hic sube bulunamadi', 'Lutfen bir sube secin' ve giris/sifre dogrulama hatalari; cozum olarak kullanici-sube yetkisi ve aktif oturum bilgisi kontrol edilmelidir.",
                tone);
        }

        if (ContainsAny(q, "settingsusers", "ayarlar", "notlarhatirlatmalar", "tamirislemleri", "tamir", "siparis", "shellviewmodel"))
        {
            return FormatStandardReply(
                "Ayarlar / Notlar / Tamir / Kabuk Navigasyon",
                "Sistem Yonetimi",
                "Bu moduller kullanici-sube-rol ayarlari, abonelik kisitlari, not-hatirlatma takibi, tamir kayitlari ve ana menuden ekrana gecis yonetimini kapsar.",
                "Shell tarafinda modul erisim yetkisi ve abonelik durumuna gore ekran acilir; AI yonlendirmeleri de ayni navigasyon katmani uzerinden uygulanir.",
                "Yaygin hatalar: 'Kullanici adi/sube adi zorunludur', 'Aktif kullanilan sube silinemez', 'Abonelik olmadigi icin ekran kapali'; bunlar is kurali koruma mesajlaridir.",
                tone);
        }

        return null;
    }

    private static string? TryHandleAuthGuideReply(string message, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksPasswordReset = ContainsAny(
            q,
            "sifremi unuttum", "sifre unuttum", "sifre sifirla", "sifreyi sifirla", "parola sifirla", "reset password");
        if (!asksPasswordReset)
            return null;

        return FormatStandardReply(
            "Giris",
            "Sifre Sifirlama",
            "Giris ekranindan 'Sifremi Unuttum' penceresini acip kullanici adi + TC kimlik no + yeni sifre alanlarini doldurarak sifreyi sifirlayabilirsiniz.",
            "Sistem TC icin 11 hane ve algoritma dogrulamasi ister; yeni sifre en az 4 karakter olmali ve tekrar alaniyla birebir ayni olmalidir.",
            "Bu bilgilerle sifirlama yapamiyorsaniz ayni penceredeki gelistirici sifresi adimi ile kullanici adi/sifre guncellemesi yapilabilir.",
            tone);
    }

    private async Task<string?> TryHandleEntityCountReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksCount = ContainsAny(q, "kac", "kac tane", "kac adet", "toplam kac", "sayisi", "mevcut");
        if (!asksCount)
            return null;

        if (ContainsAny(q, "tedarikci", "tedarikcim", "tedarikciler", "toptanci"))
        {
            var totalSuppliers = await _db.Suppliers
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => branchId == null || x.BranchId == branchId.Value)
                .CountAsync(ct);

            return FormatStandardReply(
                "Tedarikciler",
                "Liste",
                $"Kayitli toplam tedarikci sayisi: {totalSuppliers}.",
                "Bu sayi secili tenant ve sube kapsamindaki aktif kayitlardan hesaplanir.",
                "Detay listeyi Tedarikciler ekranindan gorebilirsiniz.",
                tone);
        }

        var asksSpecialProducts = ContainsAny(q, "ozel urun", "ozel urunler");
        var asksProductCards = ContainsAny(q, "urun kart", "urunkart", "urun kartlari", "kartlar");
        if (asksSpecialProducts && asksProductCards)
        {
            var totalSpecialProducts = await _db.Products
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsSpecialProduct)
                .Where(x => branchId == null || x.BranchId == branchId.Value)
                .CountAsync(ct);

            return FormatStandardReply(
                "Urun Kartlari",
                "Ozel urunler",
                $"Toplam ozel urun kaydi: {totalSpecialProducts}.",
                "Bu sayi secili tenant ve sube kapsaminda urun kartlarindaki ozel urun satirlarindan hesaplanir.",
                "Listeyi ayni sekmede acarak urun bazinda kontrol edebilirsiniz.",
                tone);
        }

        return null;
    }

    private async Task<string?> TryHandleCashMetricsReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksCash = ContainsAny(q, "kasa", "kasada", "kasa hesap", "vault", "pos", "posbanka", "nakit", "banka");
        var asksBalance = ContainsAny(q, "bakiye", "bakiyesi", "ne kadar", "kac", "toplam", "ne var", "mevcut", "tutar");
        if (!asksCash || !asksBalance)
            return null;

        var cashRows = await _db.CashAccounts
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .Select(x => new { x.Currency, x.CurrentBalance })
            .ToListAsync(ct);

        decimal SumCur(string c) => cashRows
            .Where(x => string.Equals((x.Currency ?? "").Trim(), c, StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.CurrentBalance);

        var requestedCurrency = ExtractCurrencyCode(q);
        if (!string.IsNullOrWhiteSpace(requestedCurrency))
        {
            var amount = SumCur(requestedCurrency);
            return FormatStandardReply(
                "Kasa / Gelir - Gider",
                "Kasa hesaplari",
                $"{requestedCurrency} toplam kasa bakiyesi: {amount:N4}{(requestedCurrency is "TL" ? " TL" : requestedCurrency is "HAS" or "GUMUS" ? " gr" : "")}.",
                "Bu tutar secili tenant+sube kapsamindaki Kasa/Vault/PosBanka hesaplarinin toplamidir.",
                "Birim kirilimi icin 'kasa birim bazli bakiye' diye sorabilirsiniz.",
                tone);
        }

        return FormatStandardReply(
            "Kasa / Gelir - Gider",
            "Kasa hesaplari",
            $"Birim bazli kasa bakiyeleri -> TL: {SumCur("TL"):N2}, USD: {SumCur("USD"):N4}, EUR: {SumCur("EUR"):N4}.",
            $"Metal kasa bakiyeleri -> HAS: {SumCur("HAS"):N4} gr, GUMUS: {SumCur("GUMUS"):N4} gr.",
            "Bu degerler secili tenant ve sube kapsamindaki Kasa/Vault/PosBanka hesaplarinin toplamidir.",
            tone);
    }

    private async Task<string?> TryHandleRecentProfitReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var asksSales = ContainsAny(q, "satis", "satislar", "son satis", "son satislar", "islem", "islemler");
        var asksProfit = ContainsAny(q, "kar", "karlilik", "karli", "marj", "kazanc", "brut");
        var asksRecent = ContainsAny(q, "son", "guncel", "bugun", "bu ay", "son 5", "son bes");
        if (!asksProfit || (!asksSales && !asksRecent))
            return null;

        var recentSales = await _db.Sales
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => branchId == null || x.BranchId == branchId.Value)
            .OrderByDescending(x => x.CreatedAt)
            .Take(5)
            .Select(s => new
            {
                s.Id,
                s.CreatedAt,
                Lines = s.Items.Select(i => new
                {
                    i.ProductCode,
                    i.ProductItemId,
                    i.Quantity,
                    i.LineTotal
                }).ToList()
            })
            .ToListAsync(ct);

        if (recentSales.Count == 0)
        {
            return FormatStandardReply(
                "Raporlar",
                "Satis Analizi",
                "Secili kapsamda son satis kaydi bulunamadi.",
                "Tarih/sube filtresini genisletip tekrar sorgulayin.",
                "Yeni satis olustuktan sonra karlilik verisi otomatik hesaplanir.",
                tone);
        }

        var productCodes = recentSales
            .SelectMany(x => x.Lines)
            .Select(x => x.ProductCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var productCodeSet = new HashSet<string>(productCodes, StringComparer.OrdinalIgnoreCase);

        var productMetaQuery = _db.Products
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            productMetaQuery = productMetaQuery.Where(p => p.BranchId == branchId.Value);

        var productMeta = (await productMetaQuery
            .Select(p => new { p.ProductCode, p.Cost })
            .ToListAsync(ct))
            .Where(x => productCodeSet.Contains(x.ProductCode))
            .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Cost ?? 0m, StringComparer.OrdinalIgnoreCase);

        var itemIds = recentSales
            .SelectMany(x => x.Lines)
            .Where(x => x.ProductItemId.HasValue)
            .Select(x => x.ProductItemId!.Value)
            .Distinct()
            .ToList();
        var itemCostDict = new Dictionary<Guid, decimal>();
        foreach (var itemId in itemIds)
        {
            var itemCost = await _db.ProductItems.AsNoTracking()
                .Where(pi => pi.TenantId == tenantId && pi.Id == itemId)
                .Select(pi => (decimal?)pi.Cost)
                .FirstOrDefaultAsync(ct);
            itemCostDict[itemId] = itemCost ?? 0m;
        }

        decimal totalRevenue = 0m;
        decimal totalCost = 0m;
        foreach (var sale in recentSales)
        {
            foreach (var line in sale.Lines)
            {
                totalRevenue += line.LineTotal;

                decimal lineCost;
                if (line.ProductItemId.HasValue && itemCostDict.TryGetValue(line.ProductItemId.Value, out var pieceCost))
                {
                    lineCost = pieceCost;
                }
                else
                {
                    var unitCost = productMeta.TryGetValue(line.ProductCode ?? "", out var pc) ? pc : 0m;
                    lineCost = unitCost * (line.Quantity > 0 ? line.Quantity : 1m);
                }

                totalCost += Math.Round(lineCost, 2, MidpointRounding.AwayFromZero);
            }
        }

        var grossProfit = totalRevenue - totalCost;
        var margin = totalRevenue > 0 ? Math.Round((grossProfit / totalRevenue) * 100m, 2, MidpointRounding.AwayFromZero) : 0m;

        return FormatStandardReply(
            "Raporlar",
            "Satis Analizi",
            $"Son {recentSales.Count} satis toplam ciro: {totalRevenue:N2} TL.",
            $"Maliyet: {totalCost:N2} TL, brut kar: {grossProfit:N2} TL, kar marji: %{margin:N2}.",
            "Hesaplama urun ve tekil maliyet alanlarina gore yapilir; maliyet verisi eksik satirlarda 0 kabul edilir.",
            tone);
    }

    private async Task<string?> TryHandleStockMetricsReplyAsync(string message, Guid tenantId, Guid? branchId, CancellationToken ct, ResponseTone tone)
    {
        var q = NormalizeQuestion(message);
        var askedAyar = ExtractAyarCode(q);
        var asksHurda = ContainsAny(q, "hurda", "hurdada", "hurdanin");
        var asksHas = ContainsAny(q, "has", "saf");
        var asksGram = ContainsAny(q, "gram", "gr", "kac", "ne kadar", "miktar");
        var asksStock = ContainsAny(q, "stok", "depoda", "depo", "stokta");

        if (asksHurda && (asksHas || asksGram || asksStock))
        {
            var rows = await _db.ScrapStocks
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => branchId == null || x.BranchId == branchId.Value)
                .Select(x => new { x.Karat, x.WeightGram, x.PureGoldGram })
                .ToListAsync(ct);

            if (askedAyar is not null)
            {
                var row = rows.FirstOrDefault(x => NormalizeAyarCode(x.Karat) == askedAyar);
                var hurdaGram = row?.WeightGram ?? 0m;
                var hasGram = row?.PureGoldGram ?? 0m;
                return FormatStandardReply(
                    "Stok / Depo",
                    "Hurda",
                    $"Sorgulanan ayar: {askedAyar}. Hurda stok: {hurdaGram:N3} gram, has karsiligi: {hasGram:N3} gram.",
                    "Ayni ekranda tarih ve ayar filtresiyle kayitlari detaylandirabilirsiniz.",
                    "Guncel degilse Stok / Depo ekraninda Yenile yapip tekrar sorun.",
                    tone);
            }

            var totalHurdaGram = rows.Sum(x => x.WeightGram);
            var totalHasGram = rows.Sum(x => x.PureGoldGram);
            return FormatStandardReply(
                "Stok / Depo",
                "Hurda",
                $"Toplam hurda stok: {totalHurdaGram:N3} gram.",
                $"Toplam hurda has karsiligi: {totalHasGram:N3} gram.",
                "Ayar bazinda detay icin 'stokta 14 ayar hurda kac gram' gibi sorabilirsiniz.",
                tone);
        }

        var asksGoldGram = ContainsAny(q, "altin", "hammadde", "ham madde");
        if (askedAyar is not null && asksStock && asksGoldGram && (asksGram || asksHas))
        {
            var rows = await _db.DepoStoklar
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => branchId == null || x.BranchId == branchId.Value)
                .Select(x => new { x.Ayar, x.TotalGram })
                .ToListAsync(ct);

            var ayarRows = rows.Where(x => NormalizeAyarCode(x.Ayar) == askedAyar).ToList();
            var totalGram = ayarRows.Sum(x => x.TotalGram);

            var ayarConfig = await _db.AyarAyarlari
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .ToListAsync(ct);
            var matchedAyarConfig = ayarConfig.FirstOrDefault(x => NormalizeAyarCode(x.Ayar) == askedAyar);
            var hasFactor = matchedAyarConfig is not null && matchedAyarConfig.Milyem > 0m
                ? matchedAyarConfig.Milyem / 1000m
                : TryGetAyarHasFactorFromCode(askedAyar);
            var hasGram = totalGram * hasFactor;

            return FormatStandardReply(
                "Stok / Depo",
                "Depo stok (hammadde)",
                $"{askedAyar} toplam hammadde stok: {totalGram:N3} gram.",
                $"{askedAyar} has karsiligi: {hasGram:N3} gram.",
                "Bu ayarin barkodlu/barkodsuz kirilimini gormek icin ayni sekmede ayar bazli satiri kontrol edin.",
                tone);
        }

        return null;
    }

    private static string? TryHandleKnownFlow(string message, ResponseTone tone)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var q = NormalizeQuestion(message);
        var isWhereQuestion = ContainsAny(q, "nerede", "hangi ekran", "hangi sekme", "nasil", "nereden", "hangi bolum");

        if (ContainsAny(q, "ozel urun", "ozel urunler") && ContainsAny(q, "satis", "sat"))
            return FormatStandardReply(
                "Satis Islemleri",
                "Ozel Urunler",
                "Urunu listeden secin veya barkod okutarak sepete ekleyin.",
                "Sepette adet/fiyat kontrolunu yapin.",
                "Satisi tamamlayin; duzenleme gerekirse Urun Kartlari > Ozel urunler sekmesine gecin.",
                tone);

        if (ContainsAny(q, "ozel urun", "ozel urunler") && ContainsAny(q, "alis", "barkodla", "ekle", "kaydet"))
            return FormatStandardReply(
                "Alis Islemleri",
                "Ozel urunler (alis)",
                "Kategori, maliyet/satis ve adet bilgilerini girin.",
                "Adet 1'den buyukse sistem her bir urun icin ayri barkod olusturur.",
                "Kontrol ve duzenleme icin Urun Kartlari > Ozel urunler sekmesini kullanin.",
                tone);

        if (ContainsAny(q, "hurda") && ContainsAny(q, "alis", "musteriden", "nasil al"))
            return FormatStandardReply(
                "Alis Islemleri",
                "Hurda Alisi (Musteriden)",
                "Musteri ve odeme turunu secin.",
                "Gram/milyem girip has hesabini kontrol edin.",
                "Listeye ekleyip hurda alisini tamamlayin.",
                tone);

        if (ContainsAny(q, "hurda") && ContainsAny(q, "vitrine", "vitrin", "barkodla", "hurda cikis", "cikis"))
        {
            return FormatStandardReply(
                "Stok / Depo",
                "Hurda",
                "Hurda kaydini secin.",
                "Vitrine tasima icin 'Vitrine tasi (barkodla)' islemini kullanin.",
                "Cikis gerekiyorsa 'Hurda Cikis' ile islemi tamamlayin.",
                tone);
        }

        if (ContainsAny(q, "ziynet") && ContainsAny(q, "satis", "sat"))
        {
            return FormatStandardReply(
                "Satis Islemleri",
                "Ziynet Satisi",
                "Musteri ve ziynet kategori/tip bilgisini secin.",
                "Adet ve fiyat bilgilerini girip sepete ekleyin.",
                "Sepetten satisi tamamlayin.",
                tone);
        }

        if (ContainsAny(q, "ziynet") && ContainsAny(q, "alis", "ekle", "giris"))
        {
            return FormatStandardReply(
                "Alis Islemleri",
                "Ziynet (ceyrek, yarim vb.)",
                "Kategori/tip ve adet bilgisini girin.",
                "Birim/toplam alis fiyatini kontrol edin.",
                "Sepete ekleyip ziynet alisini tamamlayin.",
                tone);
        }

        if (ContainsAny(q, "doviz", "doviz") && ContainsAny(q, "satis", "sat"))
        {
            return FormatStandardReply(
                "Satis Islemleri",
                "Doviz Satis",
                "Musteri ve para birimini secin.",
                "Miktari girip satira ekleyin.",
                "Sepetten doviz satisini tamamlayin.",
                tone);
        }

        if (ContainsAny(q, "doviz", "doviz") && ContainsAny(q, "alis", "al"))
        {
            return FormatStandardReply(
                "Alis Islemleri",
                "Doviz Alis",
                "Alici turu ve para birimini secin.",
                "Miktar ve birim alis degerini girin.",
                "Listeye ekleyip alis islemini tamamlayin.",
                tone);
        }

        if (ContainsAny(q, "set", "setler") && ContainsAny(q, "duzenle", "kontrol", "stok", "liste", "nerede") && isWhereQuestion)
        {
            return FormatStandardReply(
                "Urun Kartlari",
                "Setler (altin-iscilikli)",
                "Set satirini listeden secin.",
                "Detay/duzenleme islemine girin.",
                "Kaydetmeden once stok etkisini kontrol edin.",
                tone);
        }

        if (ContainsAny(q, "set", "setler") && ContainsAny(q, "satis", "sat"))
        {
            return FormatStandardReply(
                "Satis Islemleri",
                "Iscilikli Altin/Gumus",
                "Set urunu barkod veya urun kodu ile bulun.",
                "Sepete ekleyip toplam/fiyat kontrolu yapin.",
                "Satisi tamamlayin.",
                tone);
        }

        if (ContainsAny(q, "barkod") && ContainsAny(q, "toplu", "toptanci", "alis"))
        {
            return FormatStandardReply(
                "Alis Islemleri",
                "Toptanci alisi(Altin)",
                "Toplu alimi bu sekmede kaydedin.",
                "Tekil barkodlu urun olusturmak icin Urun Kartlari > Yeni adimina gecin.",
                "Barkodlu/barkodsuz etkisini Stok / Depo ekranindan takip edin.",
                tone);
        }

        if (ContainsAny(q, "barkod") && ContainsAny(q, "satis", "sat", "okut"))
        {
            return FormatStandardReply(
                "Satis Islemleri",
                "Iscilikli Altin/Gumus veya Ozel Urunler",
                "Standart barkodlu urunleri Iscilikli Altin/Gumus sekmesinden okutun.",
                "Ozel urun barkodlari icin Ozel Urunler sekmesini kullanin.",
                "Urunu sepete ekleyip satisi tamamlayin.",
                tone);
        }

        return null;
    }

    private static string NormalizeQuestion(string text)
    {
        var lowered = text.ToLowerInvariant();
        return lowered
            .Replace('ı', 'i')
            .Replace('ğ', 'g')
            .Replace('ü', 'u')
            .Replace('ş', 's')
            .Replace('ö', 'o')
            .Replace('ç', 'c');
    }

    private static ResponseTone DetectTone(string message)
    {
        var q = NormalizeQuestion(message);
        if (ContainsAny(q, "teknik", "detayli", "ayrintili", "formul", "hesaplama mantigi", "uzun anlat"))
            return ResponseTone.TechnicalLong;
        if (ContainsAny(q, "kisa", "ozet", "isletme dili", "pratik"))
            return ResponseTone.BusinessShort;
        return ResponseTone.BusinessShort;
    }

    private static bool ContainsAny(string source, params string[] terms)
    {
        foreach (var term in terms)
        {
            if (source.Contains(term))
                return true;
        }

        return false;
    }

    private static string? ExtractAyarCode(string normalizedQuestion)
    {
        var tokens = normalizedQuestion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.EndsWith("k") && int.TryParse(token[..^1], out var kVal))
                return $"{kVal}K";

            if (int.TryParse(token, out var val))
            {
                if (i + 1 < tokens.Length && tokens[i + 1] == "ayar")
                    return $"{val}K";
            }
        }

        return null;
    }

    private static string? ExtractCurrencyCode(string normalizedQuestion)
    {
        if (ContainsAny(normalizedQuestion, "tl", "turk lirasi", "lira"))
            return "TL";
        if (ContainsAny(normalizedQuestion, "usd", "dolar", "dollar"))
            return "USD";
        if (ContainsAny(normalizedQuestion, "eur", "euro", "avro"))
            return "EUR";
        if (ContainsAny(normalizedQuestion, "has"))
            return "HAS";
        if (ContainsAny(normalizedQuestion, "gumus", "gumus"))
            return "GUMUS";

        return null;
    }

    private static List<string> ExtractNameTokensBeforeMarkers(string message, params string[] markers)
    {
        var normalized = NormalizeQuestion(message);
        var markerIndexes = markers
            .Select(m => normalized.IndexOf(m, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .ToList();
        if (markerIndexes.Count == 0)
            return new List<string>();

        var markerIndex = markerIndexes.Min();
        if (markerIndex <= 0)
            return new List<string>();

        var prefix = NormalizeQuestion(message[..markerIndex]);
        var rawTokens = Regex.Split(prefix, @"[^a-z0-9]+")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (rawTokens.Count == 0)
            return new List<string>();

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "peki", "bana", "lutfen", "acaba", "sistemde", "sistemdeki", "kaydli", "kayitli",
            "bu", "su", "benim", "icin", "de", "da", "mi", "miyim", "miyim", "misin", "misiniz"
        };

        var filtered = rawTokens
            .Where(t => t.Length > 1 && !stopWords.Contains(t))
            .ToList();
        if (filtered.Count == 0)
            return new List<string>();

        return filtered.TakeLast(3).ToList();
    }

    private static List<string> ExtractNameTokensLoose(string message)
    {
        var normalized = NormalizeQuestion(message);
        var rawTokens = Regex.Split(normalized, @"[^a-z0-9]+")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
        if (rawTokens.Count == 0)
            return new List<string>();

        var stopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "bu", "ay", "beni", "ekrana", "gotur", "ac", "git", "gec",
            "kac", "ne", "kadar", "toplam", "borcu", "borc", "alacagi", "alacak",
            "nedir", "olan", "sistemde", "bakiyesi", "bakiyesi", "musteri", "tedarikci", "ve", "ile"
        };

        var filtered = rawTokens
            .Where(x => x.Length > 1 && !stopWords.Contains(x))
            .ToList();

        return filtered.Take(3).ToList();
    }

    private static string NormalizeAyarCode(string? ayar)
    {
        if (string.IsNullOrWhiteSpace(ayar))
            return string.Empty;

        var normalized = NormalizeQuestion(ayar).Replace(" ", string.Empty);
        if (normalized.EndsWith("ayar"))
            normalized = normalized[..^4];
        if (!normalized.EndsWith("k"))
            normalized += "k";
        return normalized.ToUpperInvariant();
    }

    private static decimal TryGetAyarHasFactorFromCode(string ayarCode)
    {
        if (!ayarCode.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            return 0m;
        if (!int.TryParse(ayarCode[..^1], out var karat))
            return 0m;
        if (karat <= 0)
            return 0m;

        return karat / 24m;
    }

    private static string FormatStandardReply(string screen, string tab, string step1, string step2, string step3, ResponseTone tone = ResponseTone.BusinessShort)
    {
        var s1 = tone == ResponseTone.BusinessShort ? ToShortSentence(step1) : step1;
        var s2 = tone == ResponseTone.BusinessShort ? ToShortSentence(step2) : step2;
        var s3 = tone == ResponseTone.BusinessShort ? ToShortSentence(step3) : step3;

        return
            $"Ekran > Sekme: {screen} > {tab}\n" +
            $"{s1} {s2} {s3}".Trim();
    }

    private static string ToBusinessShort(string fullReply)
    {
        if (string.IsNullOrWhiteSpace(fullReply))
            return fullReply;

        var lines = fullReply
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .ToList();

        if (lines.Count == 0)
            return fullReply;

        var header = lines[0];
        var bodyParts = lines
            .Skip(1)
            .Select(x => x
                .Replace("Adim 1:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Adim 2:", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Adim 3:", "", StringComparison.OrdinalIgnoreCase)
                .Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(ToShortSentence)
            .ToList();

        if (bodyParts.Count == 0)
            return header;

        return $"{header}\n{string.Join(" ", bodyParts)}";
    }

    private static string ToShortSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        var clean = text.Trim();
        var dot = clean.IndexOf('.');
        if (dot > 0)
            return clean[..(dot + 1)];
        if (clean.Length <= 120)
            return clean;
        return clean[..120].TrimEnd() + "...";
    }

    private sealed class OpenAiChatCompletionResponse
    {
        public List<OpenAiChoice>? Choices { get; set; }
    }

    private sealed class OpenAiChoice
    {
        public OpenAiMessage? Message { get; set; }
    }

    private sealed class OpenAiMessage
    {
        public string? Content { get; set; }
    }
}
