using System.Globalization;
using System.Security.Claims;
using System.Text;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Kuyumcu.PriceService.Services;
using KUYUMCU.Price_Service.Services;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/finance")]
[Authorize]
public sealed class FinanceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GoldPriceService _gold;
    private readonly IAccountingJournalService _accounting;

    public FinanceController(AppDbContext db, GoldPriceService gold, IAccountingJournalService accounting)
    {
        _db = db;
        _gold = gold;
        _accounting = accounting;
    }

    private Guid GetTenantId()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
    }

    private Guid GetBranchId()
    {
        var claim = User?.FindFirstValue("branch_id");
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Branch-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("BranchId missing (JWT veya X-Branch-Id).");
    }

    public sealed class CreateManualCashTransactionRequest
    {
        public Guid BranchId { get; set; }
        public string Currency { get; set; } = "TL";
        public decimal Amount { get; set; }
        public string TxType { get; set; } = "Income";
        public string PaymentMethod { get; set; } = "";
        public string SourceModule { get; set; } = "Manual";
        public string? RefType { get; set; }
        public Guid? RefId { get; set; }
        public string? Description { get; set; }
        public DateTime? TxDate { get; set; }
    }

    private static string NormalizeCurrency(string? unit)
    {
        var u = (unit ?? "TL").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "EURO" => "EUR",
            "GOLD" => "HAS",
            "POUND" => "GBP",
            _ => u
        };
    }

    private static string NormalizeCashTxType(string? txType)
    {
        var t = (txType ?? "").Trim().ToUpperInvariant();
        return t switch
        {
            "EXPENSE" => "Expense",
            _ => "Income"
        };
    }

    private async Task<CashAccount> GetOrCreateAccountAsync(
        Guid tenantId,
        Guid branchId,
        string accountType,
        string currency,
        string name,
        CancellationToken ct)
    {
        var acc = await _db.CashAccounts
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.AccountType == accountType &&
                x.Currency == currency &&
                x.Name == name &&
                !x.IsDeleted, ct);
        if (acc != null) return acc;

        acc = new CashAccount
        {
            TenantId = tenantId,
            BranchId = branchId,
            AccountType = accountType,
            Currency = currency,
            Name = name,
            CurrentBalance = 0m
        };
        _db.CashAccounts.Add(acc);
        return acc;
    }

    private static (string accountType, string currency, string name) ResolveManualAccount(string paymentMethod, string currencyRaw)
    {
        var m = (paymentMethod ?? "").Trim().ToUpperInvariant()
            .Replace("İ", "I")
            .Replace(" ", "")
            .Replace("-", "")
            .Replace("_", "");
        var c = NormalizeCurrency(currencyRaw);
        return m switch
        {
            "NAKIT" => ("Kasa", "TL", "Kasa TL"),
            "HAVALE" or "IBAN" or "BANKA" => c == "TL"
                ? ("PosBanka", "TL", "Banka")
                : ("PosBanka", c, $"Banka {c}"),
            "KREDIKARTIPOS" or "KREDIKARTI" or "POS" => ("PosBanka", "TL", "Kredi Karti-POS"),
            "USD" => ("Vault", "USD", "Vault USD"),
            "EUR" or "EURO" => ("Vault", "EUR", "Vault EUR"),
            "GBP" => ("Vault", "GBP", "Vault GBP"),
            "HAS" or "GOLD" => ("Vault", "HAS", "Vault HAS"),
            "GUMUS" or "GÜMÜŞ" or "SILVER" => ("Vault", "GUMUS", "Vault GUMUS"),
            _ => c == "TL" ? ("Kasa", "TL", "Kasa TL") : ("Vault", c, $"Vault {c}")
        };
    }

    [HttpGet("cashbox/summary")]
    public async Task<IActionResult> CashboxSummary([FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var q = _db.CashAccounts.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);

        var accounts = await q
            .OrderBy(x => x.AccountType)
            .ThenBy(x => x.Currency)
            .ThenBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.BranchId,
                x.AccountType,
                x.Currency,
                x.Name,
                x.CurrentBalance
            })
            .ToListAsync(ct);

        // Üst kasa kartları (TL/USD/EUR/GBP/HAS) yalnızca likit kasa hesaplarını göstermeli.
        // Alacak/veresiye gibi borç hesapları bu kartlarda toplamı ters (eksi) gösterebilir.
        var liquidAccounts = accounts.Where(x =>
            string.Equals(x.AccountType, "Kasa", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.AccountType, "Vault", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.AccountType, "PosBanka", StringComparison.OrdinalIgnoreCase));

        var totals = liquidAccounts
            .GroupBy(x => x.Currency)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.CurrentBalance), StringComparer.OrdinalIgnoreCase);

        var cashTl = totals.TryGetValue("TL", out var tl) ? tl : 0m;
        var cashUsd = totals.TryGetValue("USD", out var usd) ? usd : 0m;
        var cashEur = totals.TryGetValue("EUR", out var eur) ? eur : 0m;
        var cashGbp = totals.TryGetValue("GBP", out var gbp) ? gbp : 0m;
        var cashHas = totals.TryGetValue("HAS", out var has) ? has : 0m;
        var cashGumus = totals.TryGetValue("GUMUS", out var gumusCash) ? gumusCash : 0m;

        var customerQ = _db.CustomerBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Join(
                _db.Customers.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted),
                cb => cb.CustomerId,
                c => c.Id,
                (cb, c) => new { Balance = cb, CustomerBranchId = c.BranchId });
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            customerQ = customerQ.Where(x => x.CustomerBranchId == branchId.Value);

        var supplierQ = _db.SupplierBalances.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Join(
                _db.Suppliers.AsNoTracking().Where(s => s.TenantId == tenantId && !s.IsDeleted),
                sb => sb.SupplierId,
                s => s.Id,
                (sb, s) => new { Balance = sb, SupplierBranchId = s.BranchId });
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            supplierQ = supplierQ.Where(x => x.SupplierBranchId == branchId.Value);

        var customerBalRaw = await customerQ
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Tl = g.Sum(x => x.Balance.BalanceTL),
                Usd = g.Sum(x => x.Balance.BalanceUSD),
                Eur = g.Sum(x => x.Balance.BalanceEUR),
                Gbp = g.Sum(x => x.Balance.BalanceGBP),
                Has = g.Sum(x => x.Balance.BalanceHAS)
            })
            .FirstOrDefaultAsync(ct);
        var customerBal = await BuildCustomerCompositeBalanceAsync(
            tenantId,
            branchId,
            customerBalRaw?.Tl ?? 0m,
            customerBalRaw?.Usd ?? 0m,
            customerBalRaw?.Eur ?? 0m,
            customerBalRaw?.Has ?? 0m,
            ct);
        var supplierBal = await supplierQ
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Tl = g.Sum(x => x.Balance.BalanceTL),
                Usd = g.Sum(x => x.Balance.BalanceUSD),
                Eur = g.Sum(x => x.Balance.BalanceEUR),
                Gbp = g.Sum(x => x.Balance.BalanceGBP),
                Gumus = g.Sum(x => x.Balance.BalanceGUMUS),
                Has = g.Sum(x => x.Balance.BalanceHAS)
            })
            .FirstOrDefaultAsync(ct);

        var customerGumusRows = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x =>
                !branchId.HasValue || branchId == Guid.Empty ||
                x.BranchId == branchId.Value)
            .Select(x => new { x.GroupCode, x.ItemName, x.Direction, x.Quantity })
            .ToListAsync(ct);
        var customerGumus = customerGumusRows
            .Where(x =>
                string.Equals((x.GroupCode ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.ItemName ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);

        // --- Altın HAS (fine gold): Stok/Depo ile aynı milyem; gümüş satırları/ürünleri bu toplamlara girmez. ---
        // Havuz hammadde: satır toplam gram × milyem (yalnızca altın; gümüş havuz satırları ayrı SilverInventoryGrams'ta).
        var havuzQ = _db.DepoStokHavuzlar.AsNoTracking()
            .Where(x => x.TenantId == tenantId);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            havuzQ = havuzQ.Where(x => x.BranchId == branchId.Value);
        var havuzRows = await havuzQ
            .Select(x => new { x.TotalGram, x.Ayar, x.MalTanimNorm })
            .ToListAsync(ct);
        var gumusHammaddeGramToplam = havuzRows
            .Where(x => IsGumusHammaddeHavuzRow(x.MalTanimNorm, x.Ayar))
            .Sum(x => x.TotalGram);
        // Milyem: DepoStokView MapHavuzToSatir + DepoStokSatirYukleyici ile birebir (hammadde grid ile aynı HAS).
        var altinHammaddeHasToplam = havuzRows
            .Where(x => !IsGumusHammaddeHavuzRow(x.MalTanimNorm, x.Ayar))
            .Sum(x => x.TotalGram * MilyemFromHavuzAyarRow(x.Ayar));

        var ziynetQ = _db.Products.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted &&
                        x.InventoryType == kuyumcu_domain.Enums.InventoryType.Ziynet &&
                        (x.StokMiktari ?? 0) > 0);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            ziynetQ = ziynetQ.Where(x => x.BranchId == branchId.Value);
        var ziynetRows = await ziynetQ
            .Select(x => new
            {
                x.WeightGr,
                x.StokMiktari,
                x.Karat,
                x.Category,
                x.Name,
                x.ZiynetTipi,
                x.MalTanim
            })
            .ToListAsync(ct);
        var gumusZiynetGramToplam = ziynetRows
            .Where(x => IsSilverZiynetProduct(x.Category, x.Name, x.Karat, x.ZiynetTipi, x.MalTanim))
            .Sum(x => (x.WeightGr ?? 0m) * (x.StokMiktari ?? 0));
        var altinZiynetHas = ziynetRows
            .Where(x => !IsSilverZiynetProduct(x.Category, x.Name, x.Karat, x.ZiynetTipi, x.MalTanim))
            .Sum(x =>
            {
                var gram = (x.WeightGr ?? 0m) * (x.StokMiktari ?? 0);
                return gram * MilyemFromZiynetAyarText(x.Karat);
            });
        // Kasa "Ürün Stoğu HAS": yalnızca altın — hammadde HAS + Ziynet (gümüş ziynet hariç).
        var productHasTotal = altinHammaddeHasToplam + altinZiynetHas;

        // Hurda HAS: StokDepo hurda sekmesi ile aynı — müşteri alış satırları + hurda-metrics (ScrapStocks değil).
        var (scrapHas, gumusScrapGramToplam) =
            await HurdaPurchaseMetricsHelper.ComputeHurdaStokForCashboxAsync(_db, tenantId, branchId, ct);
        // "Toplam HAS Envanteri" paneli: yalnızca altın (ürün stoğu HAS + hurda altın HAS).
        var totalHasInventory = productHasTotal + scrapHas;

        var customerTl = customerBal.Tl;
        var customerUsd = customerBal.Usd;
        var customerEur = customerBal.Eur;
        var customerGbp = customerBalRaw?.Gbp ?? 0m;
        var customerGumusBal = customerGumus;
        var customerHas = customerBal.Has;

        var supplierTl = supplierBal?.Tl ?? 0m;
        var supplierUsd = supplierBal?.Usd ?? 0m;
        var supplierEur = supplierBal?.Eur ?? 0m;
        var supplierGbp = supplierBal?.Gbp ?? 0m;
        var supplierGumusBal = supplierBal?.Gumus ?? 0m;
        var supplierHas = supplierBal?.Has ?? 0m;

        var netTl = cashTl + customerTl + supplierTl;
        var netUsd = cashUsd + customerUsd + supplierUsd;
        var netEur = cashEur + customerEur + supplierEur;
        var netGbp = cashGbp + customerGbp + supplierGbp;
        var netGumus = cashGumus + customerGumusBal + supplierGumusBal;
        // Net HAS (altın): kasa HAS + yalnızca altın stok HAS (hammadde + ziynet + hurda; gümüş yok) + cari/tedarikçi HAS.
        // Gümüş envanter gramı ProductHas/NetHas'a karışmaz; SilverInventoryGrams altında ayrı.
        var netHas = cashHas + altinHammaddeHasToplam + altinZiynetHas + scrapHas + customerHas + supplierHas;

        var dayEndQ = _db.DayEndReports.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            dayEndQ = dayEndQ.Where(x => x.BranchId == branchId.Value);

        var prevDay = await dayEndQ
            .Where(x => x.BusinessDate < DateTime.UtcNow.Date)
            .OrderByDescending(x => x.BusinessDate)
            .Select(x => new { x.ClosingTl, x.ClosingUsd, x.ClosingEur, x.ClosingHas })
            .FirstOrDefaultAsync(ct);

        static decimal Pct(decimal current, decimal previous)
        {
            if (previous == 0) return 0m;
            return Math.Round(((current - previous) / Math.Abs(previous)) * 100m, 2, MidpointRounding.AwayFromZero);
        }

        var deltaTlPct = prevDay is null ? 0m : Pct(netTl, prevDay.ClosingTl);
        var deltaUsdPct = prevDay is null ? 0m : Pct(netUsd, prevDay.ClosingUsd);
        var deltaEurPct = prevDay is null ? 0m : Pct(netEur, prevDay.ClosingEur);
        var deltaHasPct = prevDay is null ? 0m : Pct(netHas, prevDay.ClosingHas);

        var trendSeed = await dayEndQ
            .OrderByDescending(x => x.BusinessDate)
            .Take(14)
            .OrderBy(x => x.BusinessDate)
            .Select(x => new
            {
                Label = x.BusinessDate.ToString("dd.MM"),
                NetTl = x.ClosingTl,
                NetUsd = x.ClosingUsd,
                NetEur = x.ClosingEur,
                NetHas = x.ClosingHas
            })
            .ToListAsync(ct);
        var trendRows = trendSeed
            .Select(x => (x.Label, NetTl: x.NetTl, NetUsd: x.NetUsd, NetEur: x.NetEur, NetHas: x.NetHas))
            .ToList();
        var todayLabel = DateTime.UtcNow.ToString("dd.MM");
        if (!trendRows.Any() || !string.Equals(trendRows[^1].Label, todayLabel, StringComparison.Ordinal))
        {
            trendRows.Add((todayLabel, netTl, netUsd, netEur, netHas));
        }

        return Ok(new
        {
            Accounts = accounts,
            TotalTl = cashTl,
            TotalUsd = cashUsd,
            TotalEur = cashEur,
            TotalGbp = cashGbp,
            TotalHas = cashHas,
            NetAssets = new
            {
                Cash = new { Tl = cashTl, Usd = cashUsd, Eur = cashEur, Gbp = cashGbp, Has = cashHas, Gumus = cashGumus },
                CustomerBalance = new { Tl = customerTl, Usd = customerUsd, Eur = customerEur, Gbp = customerGbp, Has = customerHas, Gumus = customerGumusBal },
                SupplierBalance = new { Tl = supplierTl, Usd = supplierUsd, Eur = supplierEur, Gbp = supplierGbp, Has = supplierHas, Gumus = supplierGumusBal },
                ProductHas = productHasTotal,
                ScrapHas = scrapHas,
                TotalHasInventory = totalHasInventory,
                SilverInventoryGrams = new
                {
                    DepoGram = gumusHammaddeGramToplam,
                    ZiynetGram = gumusZiynetGramToplam,
                    ScrapGram = gumusScrapGramToplam
                },
                NetTl = netTl,
                NetUsd = netUsd,
                NetEur = netEur,
                NetGbp = netGbp,
                NetGumus = netGumus,
                NetHas = netHas,
                DeltaTlPct = deltaTlPct,
                DeltaUsdPct = deltaUsdPct,
                DeltaEurPct = deltaEurPct,
                DeltaGbpPct = 0m,
                DeltaGumusPct = 0m,
                DeltaHasPct = deltaHasPct,
                Trend = trendRows
            }
        });
    }

    private async Task<(decimal Tl, decimal Usd, decimal Eur, decimal Has)> BuildCustomerCompositeBalanceAsync(
        Guid tenantId,
        Guid? branchId,
        decimal rawTl,
        decimal rawUsd,
        decimal rawEur,
        decimal rawHas,
        CancellationToken ct)
    {
        var quotes = _gold.LatestOrEmpty();
        if (quotes.Count == 0)
            quotes = await _gold.RefreshAsync(ct);

        decimal Ask(string code) => quotes.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Ask ?? 0m;
        decimal Bid(string code) => quotes.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Bid ?? 0m;
        decimal Nz(decimal v, decimal fallback = 0m) => v > 0 ? v : fallback;

        var hasTl = Nz(Ask("G24_TRY"), Bid("G24_TRY"));
        var ceyrekTl = Nz(Ask("CEYREK_YENI"), Ask("CEYREK_ESKI"));
        var yarimTl = Nz(Ask("YARIM_YENI"), Ask("YARIM_ESKI"));
        var tamTl = Nz(Ask("TAM_YENI"), Ask("TAM_ESKI"));
        var cumhuriyetTl = Ask("ATA_YENI");
        var ataTl = Ask("ATA_ESKI");
        var ata5Tl = Ask("ATA5_YENI");
        var gremseTl = Ask("GREMSE_YENI");
        var gumusTl = Ask("XAG_GM_TRY");

        decimal ResolveZiynetUnitTl(string? adRaw)
        {
            var ad = (adRaw ?? "").Trim().ToUpperInvariant();
            if (ad == "HAS") return hasTl;
            if (ad == "GRAM" || ad.Contains("GRAM")) return hasTl;
            if (ad.Contains("ÇEYREK") || ad.Contains("CEYREK")) return ceyrekTl;
            if (ad.Contains("YARIM")) return yarimTl;
            if (ad == "TAM" || ad.Contains("TAM")) return tamTl;
            if (ad.Contains("CUMHURIYET") || ad.Contains("CUMHURİYET")) return cumhuriyetTl > 0 ? cumhuriyetTl : ataTl;
            if (ad.Contains("ATA5")) return ata5Tl;
            if (ad.Contains("ATA")) return ataTl > 0 ? ataTl : cumhuriyetTl;
            if (ad.Contains("GÜMÜŞ") || ad.Contains("GUMUS")) return gumusTl;
            if (ad.Contains("GREMSE")) return gremseTl;
            return 0m;
        }

        var tx = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x =>
                !branchId.HasValue || branchId == Guid.Empty ||
                x.BranchId == branchId.Value ||
                (x.BranchId == null && _db.Customers
                    .Any(c => c.Id == x.CustomerId && c.TenantId == tenantId && !c.IsDeleted && c.BranchId == branchId.Value)))
            .Select(x => new
            {
                x.GroupCode,
                x.ItemName,
                x.ItemType,
                x.Quantity,
                x.Direction,
                x.TotalPriceTl
            })
            .ToListAsync(ct);

        var ziynetTl = tx
            .Where(x => string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => new
            {
                Item = x.ItemName ?? "",
                Tip = string.IsNullOrWhiteSpace(x.ItemType) ? "Yeni" : x.ItemType
            })
            .Select(g =>
            {
                var adet = g.Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);
                return adet * ResolveZiynetUnitTl(g.Key.Item);
            })
            .Sum();

        var iscilikliTl = tx
            .Where(x => string.Equals(x.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase))
            .Sum(x =>
            {
                var tl = Math.Abs(x.TotalPriceTl ?? 0m);
                return x.Direction >= 0 ? tl : -tl;
            });

        return (rawTl + ziynetTl + iscilikliTl, rawUsd, rawEur, rawHas);
    }

    [HttpGet("cash-transactions")]
    public async Task<IActionResult> CashTransactions(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] bool excludeHasSilver = false,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 500) pageSize = 100;

        static decimal RoundAmt(decimal v) => decimal.Round(Math.Abs(v), 4, MidpointRounding.AwayFromZero);
        var takasSaleMap = new Dictionary<Guid, HashSet<decimal>>();
        var takasPurchaseMap = new Dictionary<Guid, HashSet<decimal>>();
        var journalDerivedRows = new List<CashTxRow>();

        static Guid? TryExtractRefId(string? text, string marker)
        {
            var s = text ?? "";
            var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var start = i + marker.Length;
            if (start + 36 > s.Length) return null;
            var cand = s.Substring(start, 36);
            return Guid.TryParse(cand, out var g) ? g : null;
        }

        bool IsTakasByRefAndAmount(CashTxRow row)
        {
            if (row.RefId is not Guid refId) return false;
            var amt = RoundAmt(row.Amount);
            if (string.Equals(row.RefType, "SALE", StringComparison.OrdinalIgnoreCase) &&
                takasSaleMap.TryGetValue(refId, out var saleAmts))
                return saleAmts.Contains(amt);
            if (string.Equals(row.RefType, "PURCHASE", StringComparison.OrdinalIgnoreCase) &&
                takasPurchaseMap.TryGetValue(refId, out var purAmts))
                return purAmts.Contains(amt);
            return false;
        }

        // Birincil kaynak: muhasebe yevmiye satırları.
        // Bu yol, legacy CashTransactions tip bozukluklarından etkilenmez.
        try
        {
            var takasSalePaymentsQ = _db.SalePayments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => (x.Method ?? "").ToUpper().Contains("TAKAS"));
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                takasSalePaymentsQ = takasSalePaymentsQ.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) takasSalePaymentsQ = takasSalePaymentsQ.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) takasSalePaymentsQ = takasSalePaymentsQ.Where(x => x.CreatedAt < to.Value);

            takasSaleMap = (await takasSalePaymentsQ
                .Select(x => new { x.SaleId, x.Amount })
                .ToListAsync(ct))
                .GroupBy(x => x.SaleId)
                .ToDictionary(
                    g => g.Key,
                    g => new HashSet<decimal>(g.Select(x => RoundAmt(x.Amount))),
                    EqualityComparer<Guid>.Default);

            var takasPurchasePaymentsQ = _db.PurchasePayments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => x.PaymentType == kuyumcu_domain.Enums.PurchasePaymentType.Takas);
            if (from.HasValue) takasPurchasePaymentsQ = takasPurchasePaymentsQ.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) takasPurchasePaymentsQ = takasPurchasePaymentsQ.Where(x => x.CreatedAt < to.Value);

            takasPurchaseMap = (await takasPurchasePaymentsQ
                .Select(x => new { x.PurchaseId, x.Amount })
                .ToListAsync(ct))
                .GroupBy(x => x.PurchaseId)
                .ToDictionary(
                    g => g.Key,
                    g => new HashSet<decimal>(g.Select(x => RoundAmt(x.Amount))),
                    EqualityComparer<Guid>.Default);

            var jeQ = _db.JournalEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted);
            if (branchId.HasValue && branchId.Value != Guid.Empty) jeQ = jeQ.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) jeQ = jeQ.Where(x => x.Date >= from.Value);
            if (to.HasValue) jeQ = jeQ.Where(x => x.Date < to.Value);

            var journalRows = await (
                from je in jeQ
                join jl in _db.JournalLines.AsNoTracking() on je.Id equals jl.JournalEntryId
                join acc in _db.Accounts.AsNoTracking() on jl.AccountId equals acc.Id
                where jl.TenantId == tenantId && !jl.IsDeleted
                      && acc.TenantId == tenantId && !acc.IsDeleted
                select new
                {
                    je.Id,
                    je.BranchId,
                    je.Date,
                    je.Description,
                    AccountType = acc.Type,
                    AccountCode = acc.Code,
                    AccountName = acc.Name,
                    jl.Debit,
                    jl.Credit
                })
                .ToListAsync(ct);

            bool IsLiquidity(dynamic x)
            {
                var code = ((string?)x.AccountCode ?? "").ToUpperInvariant();
                var name = ((string?)x.AccountName ?? "").ToUpperInvariant();
                return code.StartsWith("10")
                       || name.Contains("KASA")
                       || name.Contains("BANKA")
                       || name.Contains("VAULT")
                       || name.Contains("POS");
            }

            string ResolveCurrency(string? code, string? name)
            {
                var c = (code ?? "").ToUpperInvariant();
                var n = (name ?? "").ToUpperInvariant();
                if (c.Contains("USD") || n.Contains("USD")) return "USD";
                if (c.Contains("EUR") || n.Contains("EUR")) return "EUR";
                if (c.Contains("GBP") || n.Contains("GBP")) return "GBP";
                if (c.Contains("HAS") || n.Contains("HAS")) return "HAS";
                if (c.Contains("GUMUS") || n.Contains("GUMUS")) return "GUMUS";
                return "TL";
            }

            var fromJournal = journalRows
                .GroupBy(x => new { x.Id, x.BranchId, x.Date, x.Description })
                .SelectMany(g =>
                {
                    var desc = g.Key.Description ?? "";
                    var upDesc = desc.ToUpperInvariant();
                    var parsedSaleRefId = TryExtractRefId(desc, "REF:SALE:");
                    var parsedPurchaseRefId = TryExtractRefId(desc, "REF:PURCHASE:");

                    var sourceModule =
                        upDesc.Contains("REF:SALE:") ? "Sale" :
                        upDesc.Contains("REF:PURCHASE:") ? "Purchase" :
                        "Manual";
                    var refType =
                        upDesc.Contains("REF:SALE:") ? "SALE" :
                        upDesc.Contains("REF:PURCHASE:") ? "PURCHASE" :
                        upDesc.Contains("REF:CASH:") ? "CASH" :
                        null;

                    var result = new List<CashTxRow>();
                    foreach (var liq in g.Where(IsLiquidity))
                    {
                        string txType;
                        decimal amount;

                        if (sourceModule == "Sale")
                        {
                            txType = "Income";
                            amount = liq.Debit;
                        }
                        else if (sourceModule == "Purchase")
                        {
                            txType = "Expense";
                            amount = liq.Credit;
                        }
                        else
                        {
                            // Manual fişte likidite satırının yönüne göre gelir/gider belirlenir.
                            if (liq.Debit > 0m)
                            {
                                txType = "Income";
                                amount = liq.Debit;
                            }
                            else
                            {
                                txType = "Expense";
                                amount = liq.Credit;
                            }
                        }

                        if (amount <= 0m) continue;

                        result.Add(new CashTxRow
                        {
                            Id = Guid.Empty,
                            BranchId = g.Key.BranchId ?? Guid.Empty,
                            TxType = txType,
                            SourceModule = sourceModule,
                            Currency = ResolveCurrency(liq.AccountCode, liq.AccountName),
                            Amount = amount,
                            TxDate = g.Key.Date,
                            RefType = refType,
                            RefId = sourceModule == "Sale" ? parsedSaleRefId
                                : sourceModule == "Purchase" ? parsedPurchaseRefId
                                : null,
                            Description = string.IsNullOrWhiteSpace(desc) ? $"REF:{sourceModule.ToUpperInvariant()}" : desc,
                            AccountName = liq.AccountName
                        });
                    }

                    return result;
                })
                .Where(x => x.BranchId != Guid.Empty && x.Amount > 0m)
                .OrderByDescending(x => x.TxDate)
                .ToList();

            fromJournal = fromJournal
                .Where(x => !IsTakasLedgerRow(x))
                .ToList();
            fromJournal = fromJournal
                .Where(x => !IsTakasByRefAndAmount(x))
                .ToList();

            if (excludeHasSilver)
                fromJournal = fromJournal
                    .Where(x => !IsHasOrSilverCurrency(x.Currency))
                    .ToList();

            journalDerivedRows = fromJournal;
        }
        catch
        {
            // Journal kaynaklı okuma başarısızsa aşağıdaki mevcut fallback akışına devam.
        }

        try
        {
            var q = _db.CashTransactions.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
            if (branchId.HasValue && branchId.Value != Guid.Empty) q = q.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) q = q.Where(x => x.TxDate >= from.Value);
            if (to.HasValue) q = q.Where(x => x.TxDate < to.Value);

        List<CashTxRow> baseRows;
        try
        {
            baseRows = await q
                .OrderByDescending(x => x.TxDate)
                .Take(1500)
                .Select(x => new CashTxRow
                {
                    Id = x.Id,
                    BranchId = x.BranchId,
                    TxType = x.TxType,
                    SourceModule = x.SourceModule,
                    Currency = x.Currency,
                    Amount = x.Amount,
                    TxDate = x.TxDate,
                    RefType = x.RefType,
                    // Legacy veritabanlarında RefId kolonu metin tutulmuş olabilir.
                    // Kasa ekranını kırmamak için liste endpoint'inde RefId materialize etmiyoruz.
                    RefId = null,
                    Description = x.Description,
                    AccountName = x.CashAccount.Name
                })
                .ToListAsync(ct);
        }
        catch (Exception ex) when (IsStringGuidCastError(ex))
        {
            // Eski/bozuk satır tiplerinde ana sorgu düşerse endpoint'i tamamen kırmayalım;
            // fallback (SalePayments/PurchasePayments/Sales/Purchases) satırlarıyla devam edelim.
            baseRows = new List<CashTxRow>();
        }

        var fallbackSalePaymentsQ = _db.SalePayments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x =>
                (x.Method ?? "").ToUpper().Contains("KART") ||
                (x.Method ?? "").ToUpper().Contains("KREDI") ||
                (x.Method ?? "").ToUpper().Contains("KREDİ") ||
                (x.Method ?? "").ToUpper().Contains("POS") ||
                (x.Method ?? "").ToUpper().Contains("IBAN") ||
                (x.Method ?? "").ToUpper().Contains("HAVALE") ||
                (x.Method ?? "").ToUpper().Contains("BANKA"));
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            fallbackSalePaymentsQ = fallbackSalePaymentsQ.Where(x => x.BranchId == branchId.Value);
        if (from.HasValue) fallbackSalePaymentsQ = fallbackSalePaymentsQ.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) fallbackSalePaymentsQ = fallbackSalePaymentsQ.Where(x => x.CreatedAt < to.Value);

        var fallbackSaleRows = await fallbackSalePaymentsQ
            .OrderByDescending(x => x.CreatedAt)
            .Take(600)
            .Select(x => new CashTxRow
            {
                Id = Guid.Empty,
                BranchId = x.BranchId,
                TxType = "Income",
                SourceModule = "Sale",
                Currency = x.Currency == null || x.Currency == "" ? "TL" : x.Currency,
                Amount = x.Amount,
                TxDate = x.CreatedAt,
                RefType = "SALE",
                RefId = (Guid?)x.SaleId,
                Description = $"Satis odeme: {x.Method}",
                AccountName = (x.Method ?? "").ToUpper() == "KART" ? "POS" : "Banka"
            })
            .ToListAsync(ct);

        var fallbackSaleDocQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x =>
                (x.PaymentType ?? "").ToUpper() == "KART" ||
                (x.PaymentType ?? "").ToUpper() == "KREDI KARTI" ||
                (x.PaymentType ?? "").ToUpper() == "KREDİ KARTI" ||
                (x.PaymentType ?? "").ToUpper() == "IBAN" ||
                (x.PaymentType ?? "").ToUpper() == "HAVALE" ||
                (x.PaymentType ?? "").ToUpper() == "HAVALE/EFT");
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            fallbackSaleDocQ = fallbackSaleDocQ.Where(x => x.BranchId == branchId.Value);
        if (from.HasValue) fallbackSaleDocQ = fallbackSaleDocQ.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) fallbackSaleDocQ = fallbackSaleDocQ.Where(x => x.CreatedAt < to.Value);

        var fallbackSaleDocRows = await fallbackSaleDocQ
            .OrderByDescending(x => x.CreatedAt)
            .Take(600)
            .Select(x => new CashTxRow
            {
                Id = Guid.Empty,
                BranchId = x.BranchId,
                TxType = "Income",
                SourceModule = "Sale",
                Currency = "TL",
                Amount = _db.SaleItems
                    .Where(si => si.TenantId == tenantId && !si.IsDeleted && si.SaleId == x.Id)
                    .Select(si => (decimal?)si.LineTotal)
                    .Sum() ?? 0m,
                TxDate = x.CreatedAt,
                RefType = "SALE",
                RefId = x.Id,
                Description = ((x.PaymentType ?? "").ToUpper() == "KART" ||
                               (x.PaymentType ?? "").ToUpper() == "KREDI KARTI" ||
                               (x.PaymentType ?? "").ToUpper() == "KREDİ KARTI")
                    ? "Satis odeme: Kart"
                    : "Satis odeme: IBAN",
                AccountName = ((x.PaymentType ?? "").ToUpper() == "KART" ||
                               (x.PaymentType ?? "").ToUpper() == "KREDI KARTI" ||
                               (x.PaymentType ?? "").ToUpper() == "KREDİ KARTI")
                    ? "POS"
                    : "Banka"
            })
            .Where(x => x.Amount > 0m)
            .ToListAsync(ct);

        var fallbackPurchasePaymentsQ =
            from pay in _db.PurchasePayments.AsNoTracking()
            join pur in _db.Purchases.AsNoTracking() on pay.PurchaseId equals pur.Id
            where pay.TenantId == tenantId
                  && pur.TenantId == tenantId
                  && !pay.IsDeleted
                  && !pur.IsDeleted
                  && (
                      pay.PaymentType == kuyumcu_domain.Enums.PurchasePaymentType.Bank ||
                      ((pay.CashAccount ?? "").ToUpper().Contains("BANKA")) ||
                      ((pay.CashAccount ?? "").ToUpper().Contains("POS")) ||
                      ((pay.CashAccount ?? "").ToUpper().Contains("KREDI")) ||
                      ((pay.CashAccount ?? "").ToUpper().Contains("KREDİ"))
                  )
            select new { pay, pur };
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            fallbackPurchasePaymentsQ = fallbackPurchasePaymentsQ.Where(x => x.pur.BranchId == branchId.Value);
        if (from.HasValue) fallbackPurchasePaymentsQ = fallbackPurchasePaymentsQ.Where(x => x.pay.CreatedAt >= from.Value);
        if (to.HasValue) fallbackPurchasePaymentsQ = fallbackPurchasePaymentsQ.Where(x => x.pay.CreatedAt < to.Value);

        var fallbackPurchaseRows = await fallbackPurchasePaymentsQ
            .OrderByDescending(x => x.pay.CreatedAt)
            .Take(600)
            .Select(x => new CashTxRow
            {
                Id = Guid.Empty,
                BranchId = x.pur.BranchId,
                TxType = "Expense",
                SourceModule = "Purchase",
                Currency = "TL",
                Amount = x.pay.Amount,
                TxDate = x.pay.CreatedAt,
                RefType = "PURCHASE",
                RefId = (Guid?)x.pay.PurchaseId,
                Description = "Alis odeme: Havale/EFT",
                AccountName = "Banka"
            })
            .ToListAsync(ct);

        var fallbackPurchaseDocQ = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x => x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.HavaleEft);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            fallbackPurchaseDocQ = fallbackPurchaseDocQ.Where(x => x.BranchId == branchId.Value);
        if (from.HasValue) fallbackPurchaseDocQ = fallbackPurchaseDocQ.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) fallbackPurchaseDocQ = fallbackPurchaseDocQ.Where(x => x.CreatedAt < to.Value);

        var fallbackPurchaseDocRows = await fallbackPurchaseDocQ
            .OrderByDescending(x => x.CreatedAt)
            .Take(600)
            .Select(x => new CashTxRow
            {
                Id = Guid.Empty,
                BranchId = x.BranchId,
                TxType = "Expense",
                SourceModule = "Purchase",
                Currency = "TL",
                Amount = x.GrandTotal,
                TxDate = x.CreatedAt,
                RefType = "PURCHASE",
                RefId = x.Id,
                Description = "Alis odeme: Havale/EFT",
                AccountName = "Banka"
            })
            .Where(x => x.Amount > 0m)
            .ToListAsync(ct);

        static string BuildKey(string? refType, Guid? refId, string txType, string currency, decimal amount)
        {
            var rType = (refType ?? "").Trim().ToUpperInvariant();
            var rId = refId.HasValue ? refId.Value.ToString("N") : "";
            var t = (txType ?? "").Trim().ToUpperInvariant();
            var c = (currency ?? "").Trim().ToUpperInvariant();
            var a = decimal.Round(Math.Abs(amount), 4, MidpointRounding.AwayFromZero).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
            return $"{rType}|{rId}|{t}|{c}|{a}";
        }

        var existingKeys = new HashSet<string>(
            baseRows.Select(x => BuildKey(x.RefType, x.RefId, x.TxType, x.Currency, x.Amount)),
            StringComparer.OrdinalIgnoreCase);

        var syntheticRows = new List<CashTxRow>();
        foreach (var s in fallbackSaleRows)
        {
            var key = BuildKey(s.RefType, s.RefId, s.TxType, s.Currency, s.Amount);
            if (existingKeys.Contains(key)) continue;
            existingKeys.Add(key);
            syntheticRows.Add(s);
        }
        foreach (var s in fallbackSaleDocRows)
        {
            var key = BuildKey(s.RefType, s.RefId, s.TxType, s.Currency, s.Amount);
            if (existingKeys.Contains(key)) continue;
            existingKeys.Add(key);
            syntheticRows.Add(s);
        }
        foreach (var p in fallbackPurchaseRows)
        {
            var key = BuildKey(p.RefType, p.RefId, p.TxType, p.Currency, p.Amount);
            if (existingKeys.Contains(key)) continue;
            existingKeys.Add(key);
            syntheticRows.Add(p);
        }
        foreach (var p in fallbackPurchaseDocRows)
        {
            var key = BuildKey(p.RefType, p.RefId, p.TxType, p.Currency, p.Amount);
            if (existingKeys.Contains(key)) continue;
            existingKeys.Add(key);
            syntheticRows.Add(p);
        }
        foreach (var j in journalDerivedRows)
        {
            var key = BuildKey(j.RefType, j.RefId, j.TxType, j.Currency, j.Amount);
            if (existingKeys.Contains(key)) continue;
            existingKeys.Add(key);
            syntheticRows.Add(j);
        }

        var merged = baseRows
            .Concat(syntheticRows)
            .OrderByDescending(x => x.TxDate)
            .ToList();
        merged = merged
            .Where(x => !IsTakasLedgerRow(x))
            .ToList();
        merged = merged
            .Where(x => !IsTakasByRefAndAmount(x))
            .ToList();
        if (excludeHasSilver)
            merged = merged
                .Where(x => !IsHasOrSilverCurrency(x.Currency))
                .ToList();
        var total = merged.Count;
        var items = merged
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

            return Ok(new { total, page, pageSize, items });
        }
        catch (Exception ex) when (IsStringGuidCastError(ex))
        {
            // Legacy/veri tipi bozuk CashTransactions satırları varsa, en azından
            // satış/alış ödeme tablolarından defteri doldurup ekranı çalışır tut.
            try
            {
            var salePaymentsQ = _db.SalePayments.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x =>
                    (x.Method ?? "").ToUpper().Contains("KART") ||
                    (x.Method ?? "").ToUpper().Contains("KREDI") ||
                    (x.Method ?? "").ToUpper().Contains("KREDİ") ||
                    (x.Method ?? "").ToUpper().Contains("POS") ||
                    (x.Method ?? "").ToUpper().Contains("IBAN") ||
                    (x.Method ?? "").ToUpper().Contains("HAVALE") ||
                    (x.Method ?? "").ToUpper().Contains("BANKA"));
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                salePaymentsQ = salePaymentsQ.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) salePaymentsQ = salePaymentsQ.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) salePaymentsQ = salePaymentsQ.Where(x => x.CreatedAt < to.Value);

            var saleRows = await salePaymentsQ
                .OrderByDescending(x => x.CreatedAt)
                .Take(1000)
                .Select(x => new CashTxRow
                {
                    Id = Guid.Empty,
                    BranchId = x.BranchId,
                    TxType = "Income",
                    SourceModule = "Sale",
                    Currency = string.IsNullOrWhiteSpace(x.Currency) ? "TL" : x.Currency,
                    Amount = x.Amount,
                    TxDate = x.CreatedAt,
                    RefType = "SALE",
                    RefId = null,
                    Description = $"Satis odeme: {x.Method}",
                    AccountName = (x.Method ?? "").ToUpper() == "KART" ? "POS" : "Banka"
                })
                .ToListAsync(ct);

            var purchasePaymentsQ =
                from pay in _db.PurchasePayments.AsNoTracking()
                join pur in _db.Purchases.AsNoTracking() on pay.PurchaseId equals pur.Id
                where pay.TenantId == tenantId
                      && pur.TenantId == tenantId
                      && !pay.IsDeleted
                      && !pur.IsDeleted
                      && (
                          pay.PaymentType == kuyumcu_domain.Enums.PurchasePaymentType.Bank ||
                          ((pay.CashAccount ?? "").ToUpper().Contains("BANKA")) ||
                          ((pay.CashAccount ?? "").ToUpper().Contains("POS")) ||
                          ((pay.CashAccount ?? "").ToUpper().Contains("KREDI")) ||
                          ((pay.CashAccount ?? "").ToUpper().Contains("KREDİ"))
                      )
                select new { pay, pur };
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                purchasePaymentsQ = purchasePaymentsQ.Where(x => x.pur.BranchId == branchId.Value);
            if (from.HasValue) purchasePaymentsQ = purchasePaymentsQ.Where(x => x.pay.CreatedAt >= from.Value);
            if (to.HasValue) purchasePaymentsQ = purchasePaymentsQ.Where(x => x.pay.CreatedAt < to.Value);

            var purchaseRows = await purchasePaymentsQ
                .OrderByDescending(x => x.pay.CreatedAt)
                .Take(1000)
                .Select(x => new CashTxRow
                {
                    Id = Guid.Empty,
                    BranchId = x.pur.BranchId,
                    TxType = "Expense",
                    SourceModule = "Purchase",
                    Currency = "TL",
                    Amount = x.pay.Amount,
                    TxDate = x.pay.CreatedAt,
                    RefType = "PURCHASE",
                    RefId = null,
                    Description = "Alis odeme: Havale/EFT",
                    AccountName = "Banka"
                })
                .ToListAsync(ct);

            var purchaseDocQ = _db.Purchases.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x =>
                    x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.Nakit ||
                    x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.HavaleEft ||
                    x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.NakitHavaleEft);
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                purchaseDocQ = purchaseDocQ.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) purchaseDocQ = purchaseDocQ.Where(x => x.CreatedAt >= from.Value);
            if (to.HasValue) purchaseDocQ = purchaseDocQ.Where(x => x.CreatedAt < to.Value);

            var purchaseDocRows = await purchaseDocQ
                .OrderByDescending(x => x.CreatedAt)
                .Take(1000)
                .Select(x => new CashTxRow
                {
                    Id = Guid.Empty,
                    BranchId = x.BranchId,
                    TxType = "Expense",
                    SourceModule = "Purchase",
                    Currency = "TL",
                    Amount = x.GrandTotal,
                    TxDate = x.CreatedAt,
                    RefType = "PURCHASE",
                    RefId = null,
                    Description = x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.HavaleEft
                        ? "Alis odeme: Havale/EFT"
                        : (x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.NakitHavaleEft
                            ? "Alis odeme: Nakit+Havale/EFT"
                            : "Alis odeme: Cash"),
                    AccountName = x.PaymentMethod == kuyumcu_domain.Enums.PurchasePaymentMethod.HavaleEft ? "Banka" : "Kasa"
                })
                .Where(x => x.Amount > 0m)
                .ToListAsync(ct);

            var manualJournalQ = _db.JournalEntries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted)
                .Where(x => x.Description.Contains("REF:CASH:"));
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                manualJournalQ = manualJournalQ.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) manualJournalQ = manualJournalQ.Where(x => x.Date >= from.Value);
            if (to.HasValue) manualJournalQ = manualJournalQ.Where(x => x.Date < to.Value);

            var manualJournalRows = await (
                from je in manualJournalQ
                join jl in _db.JournalLines.AsNoTracking() on je.Id equals jl.JournalEntryId
                join acc in _db.Accounts.AsNoTracking() on jl.AccountId equals acc.Id
                where jl.TenantId == tenantId && !jl.IsDeleted
                      && acc.TenantId == tenantId && !acc.IsDeleted
                select new
                {
                    je.Id,
                    je.BranchId,
                    je.Date,
                    je.Description,
                    AccountType = acc.Type,
                    AccountCode = acc.Code,
                    AccountName = acc.Name,
                    jl.Debit,
                    jl.Credit
                })
                .ToListAsync(ct);

            var manualRows = manualJournalRows
                .GroupBy(x => new { x.Id, x.BranchId, x.Date, x.Description })
                .Select(g =>
                {
                    var expenseAmount = g
                        .Where(x => x.AccountType == kuyumcu_domain.Entities.AccountType.Expense)
                        .Sum(x => x.Debit > 0m ? x.Debit : 0m);
                    var incomeAmount = g
                        .Where(x => x.AccountType == kuyumcu_domain.Entities.AccountType.Income)
                        .Sum(x => x.Credit > 0m ? x.Credit : 0m);
                    var isExpense = expenseAmount > 0m;
                    var amount = isExpense ? expenseAmount : incomeAmount;

                    var liquidity = g.FirstOrDefault(x =>
                        x.AccountType == kuyumcu_domain.Entities.AccountType.Asset &&
                        (x.AccountCode.StartsWith("10") || x.AccountName.Contains("Kasa") || x.AccountName.Contains("Banka")));
                    var currency = "TL";
                    var accName = liquidity?.AccountName ?? "Kasa";
                    if ((liquidity?.AccountCode ?? "").Contains("USD", StringComparison.OrdinalIgnoreCase) ||
                        accName.Contains("USD", StringComparison.OrdinalIgnoreCase)) currency = "USD";
                    else if ((liquidity?.AccountCode ?? "").Contains("EUR", StringComparison.OrdinalIgnoreCase) ||
                             accName.Contains("EUR", StringComparison.OrdinalIgnoreCase)) currency = "EUR";
                    else if ((liquidity?.AccountCode ?? "").Contains("GBP", StringComparison.OrdinalIgnoreCase) ||
                             accName.Contains("GBP", StringComparison.OrdinalIgnoreCase)) currency = "GBP";
                    else if ((liquidity?.AccountCode ?? "").Contains("HAS", StringComparison.OrdinalIgnoreCase) ||
                             accName.Contains("HAS", StringComparison.OrdinalIgnoreCase)) currency = "HAS";
                    else if ((liquidity?.AccountCode ?? "").Contains("GUMUS", StringComparison.OrdinalIgnoreCase) ||
                             accName.Contains("GUMUS", StringComparison.OrdinalIgnoreCase)) currency = "GUMUS";

                    return new CashTxRow
                    {
                        Id = Guid.Empty,
                        BranchId = g.Key.BranchId ?? Guid.Empty,
                        TxType = isExpense ? "Expense" : "Income",
                        SourceModule = "Manual",
                        Currency = currency,
                        Amount = amount,
                        TxDate = g.Key.Date,
                        RefType = "CASH",
                        RefId = null,
                        Description = g.Key.Description,
                        AccountName = accName
                    };
                })
                .Where(x => x.BranchId != Guid.Empty && x.Amount > 0m)
                .ToList();

            static string FallbackKey(CashTxRow x)
            {
                var txType = (x.TxType ?? "").Trim().ToUpperInvariant();
                var module = (x.SourceModule ?? "").Trim().ToUpperInvariant();
                var cur = (x.Currency ?? "").Trim().ToUpperInvariant();
                var amt = decimal.Round(Math.Abs(x.Amount), 4, MidpointRounding.AwayFromZero)
                    .ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);
                var dt = x.TxDate.ToString("yyyyMMddHHmmss");
                return $"{txType}|{module}|{cur}|{amt}|{dt}";
            }

            var mergedFallback = saleRows
                .Concat(purchaseRows)
                .Concat(purchaseDocRows)
                .Concat(manualRows)
                .GroupBy(FallbackKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(x => x.TxDate)
                .ToList();
            mergedFallback = mergedFallback
                .Where(x => !IsTakasLedgerRow(x))
                .ToList();
            mergedFallback = mergedFallback
                .Where(x => !IsTakasByRefAndAmount(x))
                .ToList();
            if (excludeHasSilver)
                mergedFallback = mergedFallback
                    .Where(x => !IsHasOrSilverCurrency(x.Currency))
                    .ToList();

            var fallbackTotal = mergedFallback.Count;
            var fallbackItems = mergedFallback
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Ok(new { total = fallbackTotal, page, pageSize, items = fallbackItems });
            }
            catch
            {
                var emergency = await BuildEmergencyCashTransactionsResponseAsync(
                    tenantId, branchId, from, to, page, pageSize, excludeHasSilver, ct);
                return Ok(emergency);
            }
        }
        catch
        {
            var emergency = await BuildEmergencyCashTransactionsResponseAsync(
                tenantId, branchId, from, to, page, pageSize, excludeHasSilver, ct);
            return Ok(emergency);
        }
    }

    private async Task<object> BuildEmergencyCashTransactionsResponseAsync(
        Guid tenantId,
        Guid? branchId,
        DateTime? from,
        DateTime? to,
        int page,
        int pageSize,
        bool excludeHasSilver,
        CancellationToken ct)
    {
        try
        {
            var txQ = _db.CashTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted);
            if (branchId.HasValue && branchId.Value != Guid.Empty)
                txQ = txQ.Where(x => x.BranchId == branchId.Value);
            if (from.HasValue) txQ = txQ.Where(x => x.TxDate >= from.Value);
            if (to.HasValue) txQ = txQ.Where(x => x.TxDate < to.Value);

            var q =
                from tx in txQ
                join acc in _db.CashAccounts.AsNoTracking()
                    .Where(a => a.TenantId == tenantId && !a.IsDeleted)
                    on tx.CashAccountId equals acc.Id into accJoin
                from acc in accJoin.DefaultIfEmpty()
                select new CashTxRow
                {
                    Id = tx.Id,
                    BranchId = tx.BranchId,
                    TxType = tx.TxType,
                    SourceModule = tx.SourceModule,
                    Currency = tx.Currency,
                    Amount = tx.Amount,
                    TxDate = tx.TxDate,
                    RefType = tx.RefType,
                    RefId = null,
                    Description = tx.Description,
                    AccountName = acc != null ? acc.Name : null
                };

            var rows = await q
                .OrderByDescending(x => x.TxDate)
                .Take(3000)
                .ToListAsync(ct);

            rows = rows
                .Where(x => !IsTakasLedgerRow(x))
                .ToList();
            if (excludeHasSilver)
                rows = rows
                    .Where(x => !IsHasOrSilverCurrency(x.Currency))
                    .ToList();

            var total = rows.Count;
            var items = rows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return new { total, page, pageSize, items };
        }
        catch
        {
            return new
            {
                total = 0,
                page,
                pageSize,
                items = new List<CashTxRow>()
            };
        }
    }

    private static bool IsStringGuidCastError(Exception ex)
    {
        var cur = ex;
        while (cur is not null)
        {
            if ((cur.Message ?? "").IndexOf("System.String", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (cur.Message ?? "").IndexOf("System.Guid", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            cur = cur.InnerException!;
        }
        return false;
    }

    private static bool IsHasOrSilverCurrency(string? currency)
    {
        var c = (currency ?? "").Trim().ToUpperInvariant();
        return c is "HAS" or "GUMUS" or "GÜMÜŞ";
    }

    private static bool IsTakasLedgerRow(CashTxRow row)
    {
        var desc = (row.Description ?? "").Trim().ToUpperInvariant();
        var account = (row.AccountName ?? "").Trim().ToUpperInvariant();
        var module = (row.SourceModule ?? "").Trim().ToUpperInvariant();

        return desc.Contains("TAKAS")
               || account.Contains("TAKAS")
               || module == "TAKAS";
    }

    [HttpPost("cash-transactions/manual")]
    public async Task<IActionResult> CreateManualCashTransaction([FromBody] CreateManualCashTransactionRequest req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req is null) return BadRequest(new { error = "İstek boş olamaz." });
        Guid sessionBranchId;
        try
        {
            sessionBranchId = GetBranchId();
        }
        catch
        {
            sessionBranchId = Guid.Empty;
        }
        if (req.BranchId == Guid.Empty)
            req.BranchId = sessionBranchId;
        if (req.BranchId == Guid.Empty && req.RefId.HasValue && req.RefId.Value != Guid.Empty)
            req.BranchId = await ResolveBranchIdFromReferenceAsync(tenantId, req.RefType, req.RefId.Value, ct);
        if (req.BranchId == Guid.Empty) return BadRequest(new { error = "BranchId zorunludur." });
        if (sessionBranchId != Guid.Empty && req.BranchId != sessionBranchId)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });
        if (req.Amount <= 0) return BadRequest(new { error = "Amount 0'dan büyük olmalıdır." });

        var (accountType, currency, name) = ResolveManualAccount(req.PaymentMethod, req.Currency);
        var txType = NormalizeCashTxType(req.TxType);
        var account = await GetOrCreateAccountAsync(tenantId, req.BranchId, accountType, currency, name, ct);

        account.CurrentBalance += txType == "Income" ? req.Amount : -req.Amount;
        var tx = new CashTransaction
        {
            TenantId = tenantId,
            BranchId = req.BranchId,
            CashAccountId = account.Id,
            TxType = txType,
            SourceModule = string.IsNullOrWhiteSpace(req.SourceModule) ? "Manual" : req.SourceModule.Trim(),
            Currency = currency,
            Amount = req.Amount,
            TxDate = req.TxDate ?? DateTime.UtcNow,
            RefType = req.RefType,
            RefId = req.RefId,
            Description = req.Description
        };
        _db.CashTransactions.Add(tx);
        await _db.SaveChangesAsync(ct);
        await _accounting.RecordManualCashTransactionAsync(tx, account, ct);

        return Ok(new
        {
            tx.Id,
            tx.BranchId,
            tx.TxType,
            tx.SourceModule,
            tx.Currency,
            tx.Amount,
            tx.TxDate,
            tx.RefType,
            tx.RefId,
            tx.Description
        });
    }

    private async Task<Guid> ResolveBranchIdFromReferenceAsync(Guid tenantId, string? refType, Guid refId, CancellationToken ct)
    {
        var type = (refType ?? string.Empty).Trim().ToUpperInvariant();
        try
        {
            if (type.Contains("CUSTOMER"))
            {
                var customerBranch = await _db.Customers.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Id == refId)
                    .Select(x => x.BranchId)
                    .FirstOrDefaultAsync(ct);
                if (customerBranch != Guid.Empty) return customerBranch;
            }

            if (type.Contains("SUPPLIER"))
            {
                var supplierTxBranch = await _db.SupplierTransactions.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Id == refId)
                    .Select(x => x.BranchId)
                    .FirstOrDefaultAsync(ct);
                if (supplierTxBranch.HasValue && supplierTxBranch.Value != Guid.Empty) return supplierTxBranch.Value;

                var supplierBranch = await _db.Suppliers.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Id == refId)
                    .Select(x => x.BranchId)
                    .FirstOrDefaultAsync(ct);
                if (supplierBranch.HasValue && supplierBranch.Value != Guid.Empty) return supplierBranch.Value;
            }

            if (type == "SALE")
            {
                var saleBranch = await _db.Sales.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Id == refId)
                    .Select(x => x.BranchId)
                    .FirstOrDefaultAsync(ct);
                if (saleBranch != Guid.Empty) return saleBranch;
            }

            if (type == "PURCHASE")
            {
                var purchaseBranch = await _db.Purchases.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.Id == refId)
                    .Select(x => x.BranchId)
                    .FirstOrDefaultAsync(ct);
                if (purchaseBranch != Guid.Empty) return purchaseBranch;
            }
        }
        catch
        {
            // Branch çözümleme başarısızsa mevcut validasyon akışı çalışsın.
        }

        return Guid.Empty;
    }

    [HttpGet("reports/profit")]
    public async Task<IActionResult> Profit(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var f = from ?? DateTime.UtcNow.Date;
        var t = to ?? f.AddDays(1);

        var salesQ = _db.Sales.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= f && x.CreatedAt < t);
        var purchaseQ = _db.Purchases.AsNoTracking().Where(x => x.TenantId == tenantId && x.Date >= f && x.Date < t);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
        {
            salesQ = salesQ.Where(x => x.BranchId == branchId.Value);
            purchaseQ = purchaseQ.Where(x => x.BranchId == branchId.Value);
        }

        var sales = await salesQ.SelectMany(x => x.Items).SumAsync(i => (decimal?)i.LineTotal, ct) ?? 0m;
        var purchases = await purchaseQ.SumAsync(x => (decimal?)x.GrandTotal, ct) ?? 0m;
        var cashExpenses = await _db.CashTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.TxType == "Expense" && x.TxDate >= f && x.TxDate < t)
            .Where(x => !branchId.HasValue || branchId == Guid.Empty || x.BranchId == branchId.Value)
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;

        var net = sales - purchases;
        return Ok(new
        {
            From = f,
            To = t,
            SalesAmount = sales,
            PurchaseAmount = purchases,
            ExpenseAmount = cashExpenses,
            NetProfit = net
        });
    }

    [HttpGet("reports/top-categories")]
    public async Task<IActionResult> TopCategories(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int take = 10,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (take <= 0 || take > 100) take = 10;
        var f = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var t = to ?? DateTime.UtcNow.Date.AddDays(1);

        var q = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= f && x.CreatedAt < t);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);

        var rows = await q.SelectMany(x => x.Items)
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Diger" : i.Category!)
            .Select(g => new
            {
                Category = g.Key,
                Quantity = g.Sum(x => x.Quantity),
                Amount = g.Sum(x => x.LineTotal)
            })
            .OrderByDescending(x => x.Quantity)
            .Take(take)
            .ToListAsync(ct);

        return Ok(rows);
    }

    [HttpGet("reports/top-customers")]
    public async Task<IActionResult> TopCustomers(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int take = 10,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (take <= 0 || take > 100) take = 10;
        var f = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var t = to ?? DateTime.UtcNow.Date.AddDays(1);

        var q = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CustomerId.HasValue && x.CreatedAt >= f && x.CreatedAt < t);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);

        var rows = await q
            .GroupBy(x => x.CustomerId!.Value)
            .Select(g => new
            {
                CustomerId = g.Key,
                TxCount = g.Count(),
                Amount = g.SelectMany(s => s.Items).Sum(i => i.LineTotal)
            })
            .OrderByDescending(x => x.Amount)
            .Take(take)
            .ToListAsync(ct);

        // ids.Contains(...) SQL Server'da OPENJSON ... WITH üretip bazı sürümlerde
        // "Incorrect syntax near 'WITH'" hatasına düşebiliyor. Bu yüzden top-N küçük
        // küme için müşteri adlarını tekil sorgularla dolduruyoruz.
        var result = new List<object>(rows.Count);
        foreach (var row in rows)
        {
            var customerName = await _db.Customers.AsNoTracking()
                .Where(c => c.Id == row.CustomerId)
                .Select(c => c.FullName)
                .FirstOrDefaultAsync(ct) ?? "-";

            result.Add(new
            {
                row.CustomerId,
                CustomerName = customerName,
                row.TxCount,
                row.Amount
            });
        }

        return Ok(result);
    }

    [HttpGet("reports/overview")]
    public async Task<IActionResult> Overview([FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var now = DateTime.UtcNow;
        var today = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var yearStart = new DateTime(now.Year, 1, 1);

        async Task<(decimal sales, decimal purchase, decimal net)> Calc(DateTime from, DateTime to)
        {
            var purchaseQ = _db.Purchases.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Date >= from && x.Date < to);
            if (branchId.HasValue && branchId.Value != Guid.Empty)
            {
                purchaseQ = purchaseQ.Where(x => x.BranchId == branchId.Value);
            }

            var purchases = await purchaseQ.SumAsync(x => (decimal?)x.GrandTotal, ct) ?? 0m;

            // Kartlar ile "Satış Hareketleri" tablosu birebir aynı hesap kurallarını kullansın.
            var toInclusive = to.AddTicks(-1);
            var linesResult = await SalesPurchaseLines(branchId, from, toInclusive, ct);
            var lines = ((linesResult as OkObjectResult)?.Value as System.Collections.IEnumerable)?
                .Cast<object>() ?? Enumerable.Empty<object>();

            static string PropText(object row, string propName)
                => row.GetType().GetProperty(propName)?.GetValue(row)?.ToString() ?? "";
            static decimal PropDec(object row, string propName)
            {
                var raw = row.GetType().GetProperty(propName)?.GetValue(row);
                if (raw is decimal d) return d;
                if (raw is null) return 0m;
                return decimal.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
            }

            var saleLines = lines.Where(row =>
            {
                var t = PropText(row, "Type").Trim();
                return string.Equals(t, "Satis", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(t, "Satış", StringComparison.OrdinalIgnoreCase);
            });

            var sales = saleLines.Sum(row => PropDec(row, "Revenue"));
            var net = saleLines.Sum(row => PropDec(row, "Profit"));
            return (sales, purchases, net);
        }

        var daily = await Calc(today, today.AddDays(1));
        var monthly = await Calc(monthStart, monthStart.AddMonths(1));
        var yearly = await Calc(yearStart, yearStart.AddYears(1));

        var cashQ = _db.CashAccounts.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            cashQ = cashQ.Where(x => x.BranchId == branchId.Value);
        var cash = await cashQ.ToListAsync(ct);

        // KasaView ile birebir: yalnızca likit kasa hesapları (Kasa/Vault/PosBanka) üst kartlarda görünür.
        var liquidCash = cash.Where(x =>
            string.Equals(x.AccountType, "Kasa", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.AccountType, "Vault", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.AccountType, "PosBanka", StringComparison.OrdinalIgnoreCase));

        var cashTotals = liquidCash
            .GroupBy(x => x.Currency)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.CurrentBalance), StringComparer.OrdinalIgnoreCase);

        decimal SumCur(string c) => cashTotals.TryGetValue(c, out var v) ? v : 0m;

        return Ok(new
        {
            DailySales = daily.sales,
            DailyPurchase = daily.purchase,
            DailyNetProfit = daily.net,
            MonthlySales = monthly.sales,
            MonthlyPurchase = monthly.purchase,
            MonthlyNetProfit = monthly.net,
            YearlySales = yearly.sales,
            YearlyPurchase = yearly.purchase,
            YearlyNetProfit = yearly.net,
            CashTl = SumCur("TL"),
            CashUsd = SumCur("USD"),
            CashEur = SumCur("EUR"),
            CashHas = SumCur("HAS")
        });
    }

    // Eski endpoint - geriye dönük uyumluluk için korunuyor
    [HttpGet("reports/trend12")]
    public Task<IActionResult> Trend12([FromQuery] Guid? branchId, CancellationToken ct = default)
        => Trend(branchId, "monthly", ct);

    [HttpGet("reports/trend")]
    public async Task<IActionResult> Trend([FromQuery] Guid? branchId, [FromQuery] string period = "monthly", CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.Date;
        DateTime start;
        DateTime end;
        Func<DateTime, DateTime> getNext;
        Func<DateTime, string> formatLabel;

        if (period == "daily")
        {
            start = now.AddDays(-29);
            end = now.AddDays(1);
            getNext = d => d.AddDays(1);
            formatLabel = d => d.ToString("dd MMM");
        }
        else if (period == "yearly")
        {
            start = new DateTime(now.Year - 4, 1, 1);
            end = new DateTime(now.Year + 1, 1, 1);
            getNext = d => d.AddYears(1);
            formatLabel = d => d.ToString("yyyy");
        }
        else // monthly
        {
            start = new DateTime(now.Year, now.Month, 1).AddMonths(-11);
            end = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            getNext = d => d.AddMonths(1);
            formatLabel = d => d.ToString("MMM yyyy");
        }

        // Trend verisini, kartlar ile birebir aynı hesap kurallarını kullanan endpoint'ten üret.
        // Böylece "Günlük Satış / Günlük Net Kâr" kartları ile günlük trend noktaları aynı olur.
        var toInclusive = end.AddTicks(-1);
        var linesResult = await SalesPurchaseLines(branchId, start, toInclusive, ct);
        var lines = ((linesResult as OkObjectResult)?.Value as System.Collections.IEnumerable)?
            .Cast<object>()
            .ToList() ?? new List<object>();

        static string PropText(object row, string propName)
            => row.GetType().GetProperty(propName)?.GetValue(row)?.ToString() ?? "";
        static decimal PropDec(object row, string propName)
        {
            var raw = row.GetType().GetProperty(propName)?.GetValue(row);
            if (raw is decimal d) return d;
            if (raw is null) return 0m;
            return decimal.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        }
        static DateTime PropDate(object row, string propName)
        {
            var raw = row.GetType().GetProperty(propName)?.GetValue(row);
            if (raw is DateTime dt) return dt;
            if (raw is null) return DateTime.MinValue;
            return DateTime.TryParse(raw.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTime.MinValue;
        }

        var saleLines = lines
            .Where(row =>
            {
                var t = PropText(row, "Type").Trim();
                return string.Equals(t, "Satis", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(t, "Satış", StringComparison.OrdinalIgnoreCase);
            })
            .Select(row => new
            {
                SaleDate = PropDate(row, "Date"),
                Revenue = PropDec(row, "Revenue"),
                Profit = PropDec(row, "Profit")
            })
            .Where(x => x.SaleDate >= start && x.SaleDate < end)
            .ToList();

        // --- Dönem kovalarına dök ---
        var rows = new List<object>();
        for (var d = start; d < end; d = getNext(d))
        {
            var bucketStart = d;
            var bucketEnd   = getNext(d);
            var bucket      = saleLines.Where(x => x.SaleDate >= bucketStart && x.SaleDate < bucketEnd).ToList();
            rows.Add(new
            {
                Label   = formatLabel(d),
                Sales   = bucket.Sum(x => x.Revenue),
                Profit  = bucket.Sum(x => x.Profit)
            });
        }

        return Ok(rows);
    }

    // Anonim tip projection için yardımcı kayıt
    private readonly record struct Meta(decimal CostRaw, bool IsSpecial, bool IsZiynet, bool IsSilver);
    private sealed record PurchaseItemReportRow(
        Guid PurchaseId,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string? Category,
        string Karat,
        decimal Quantity,
        decimal? BirimIscilikHas,
        decimal? OdenecekToplamHas);
    private sealed record PurchasePaymentReportRow(
        Guid PurchaseId,
        PurchasePaymentType PaymentType,
        string? UnitCode,
        decimal? UnitAmount,
        decimal Amount,
        decimal? GoldWeight);
    private sealed record SalePaymentMethodRow(Guid SaleId, string Method);
    private sealed class SalesPurchaseReportRow
    {
        public DateTime Date { get; set; }
        public string Type { get; set; } = "";
        public string MovementKind { get; set; } = "";
        public string MovementSegment { get; set; } = "";
        public string PaymentMethod { get; set; } = "";
        public string RefNo { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string Product { get; set; } = "";
        public string UrunTipi { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string Category { get; set; } = "";
        public string Karat { get; set; } = "";
        public decimal Quantity { get; set; }
        public string QuantityDisplay { get; set; } = "";
        public decimal Revenue { get; set; }
        public decimal RevenueHas { get; set; }
        public string RevenueViewDisplay { get; set; } = "";
        public decimal Cost { get; set; }
        public decimal CostHas { get; set; }
        public string CostTlDisplay { get; set; } = "";
        public string CostViewDisplay { get; set; } = "";
        public decimal Profit { get; set; }
        public decimal ProfitHas { get; set; }
        public string ProfitViewDisplay { get; set; } = "";
        public decimal MarginPct { get; set; }
        public bool IsSpecialProduct { get; set; }
        public bool IsZiynet { get; set; }
        public bool IsForex { get; set; }
        public bool IsSilver { get; set; }
    }

    private static (DateTime StartInclusive, DateTime EndExclusive) NormalizeDateRangeInclusive(DateTime? from, DateTime? to)
    {
        var start = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;
        var endInclusive = (to ?? DateTime.UtcNow.Date).Date;
        if (endInclusive < start)
            endInclusive = start;

        return (start, endInclusive.AddDays(1));
    }

    [HttpGet("reports/sales-purchase-lines")]
    public async Task<IActionResult> SalesPurchaseLines(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var (f, tExclusive) = NormalizeDateRangeInclusive(from, to);

        var salesQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= f && x.CreatedAt < tExclusive);
        salesQ = salesQ.Where(x => x.PaymentType != "ManualEInvoice");
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            salesQ = salesQ.Where(x => x.BranchId == branchId.Value);

        var rates = _gold.LatestOrEmpty();
        if (rates.Count == 0) rates = await _gold.RefreshAsync(ct);
        decimal Ask(string code) => rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Ask ?? 0m;
        decimal Bid(string code) => rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Bid ?? 0m;
        var hasTl = Ask("G24_TRY");
        if (hasTl <= 0) hasTl = Bid("G24_TRY");
        decimal TlToHas(decimal tl) => hasTl > 0 ? Math.Round(tl / hasTl, 6, MidpointRounding.AwayFromZero) : 0m;

        var lines = await salesQ.SelectMany(s => s.Items
            .Where(i => string.IsNullOrWhiteSpace(i.ProductCode) || !EF.Functions.Like(i.ProductCode, "MANUAL-%"))
            .Select(i => new
            {
                SaleDate = s.CreatedAt,
                i.SaleId,
                i.LineNo,
                s.PaymentType,
                CustomerName = s.Customer != null ? s.Customer.FullName : "",
                i.ProductCode,
                i.ProductName,
                i.Karat,
                i.Category,
                i.ProductItemId,
                i.Quantity,
                Revenue = i.LineTotal
            }))
            .ToListAsync(ct);

        var emanetZiynetRows = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && !x.IsDeleted
                        && x.TxDate >= f
                        && x.TxDate < tExclusive
                        && (x.GroupCode ?? "").Trim().ToUpper() == "ZIYNET"
                        && (x.RefType ?? "").Trim().ToUpper() == "SALE"
                        && (x.CariDurum ?? "").Trim().ToUpper() == "EMANET"
                        && x.RefId == null)
            .Where(x => !branchId.HasValue || branchId.Value == Guid.Empty || x.BranchId == branchId.Value)
            .Join(
                _db.Customers.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted),
                tx => tx.CustomerId,
                c => c.Id,
                (tx, c) => new
                {
                    tx.Id,
                    tx.TxDate,
                    tx.ItemName,
                    tx.ItemType,
                    tx.Quantity,
                    tx.UnitPriceTl,
                    tx.TotalPriceTl,
                    tx.HasEquivalent,
                    CustomerName = c.FullName
                })
            .ToListAsync(ct);

        var salePaymentRows = lines.Count == 0
            ? new List<SalePaymentMethodRow>()
            : await _db.SalePayments.AsNoTracking()
                .Join(
                    salesQ.Select(s => s.Id),
                    sp => sp.SaleId,
                    id => id,
                    (sp, _) => new { sp.SaleId, sp.Method })
                .Select(x => new SalePaymentMethodRow(x.SaleId, x.Method ?? ""))
                .ToListAsync(ct);
        var salePaymentSummaryBySaleId = salePaymentRows
            .GroupBy(x => x.SaleId)
            .ToDictionary(
                g => g.Key,
                g => ResolveSalePaymentSummary(g.Select(x => x.Method).ToList()),
                EqualityComparer<Guid>.Default);

        var productCodes = lines
            .Select(x => x.ProductCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var productCodeSet = new HashSet<string>(productCodes, StringComparer.OrdinalIgnoreCase);
        // Raporlarda satılmış (soft-delete) ürünleri de doğru okumak için query filter'ı kapat.
        var splMetaQ = _db.Products.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            splMetaQ = splMetaQ.Where(p => p.BranchId == branchId.Value);
        var productCostMap = await splMetaQ
            .Select(p => new { p.ProductCode, p.Cost, p.IsSpecialProduct, p.InventoryType, p.BelirlenenSatisFiyatiHas, p.Category, p.Name, p.Karat, p.Olcu })
            .ToListAsync(ct);
        var productMetaDict = productCostMap
            .Where(x => productCodeSet.Contains(x.ProductCode))
            .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var p = g.First();
                    return new
                    {
                        CostRaw = p.Cost ?? 0m,
                        IsSpecial = p.IsSpecialProduct,
                        IsZiynet = p.InventoryType == kuyumcu_domain.Enums.InventoryType.Ziynet,
                        IsSilver = IsSilverProduct(p.Category, p.Name, p.Karat),
                        IsForex = IsForexText(p.Category) || IsForexText(p.Name) || (p.ProductCode ?? "").StartsWith("FX-", StringComparison.OrdinalIgnoreCase),
                        TargetSaleHas = p.BelirlenenSatisFiyatiHas ?? 0m,
                        Olcu = p.Olcu ?? ""
                    };
                },
                StringComparer.OrdinalIgnoreCase);

        var itemIds = lines.Where(x => x.ProductItemId.HasValue).Select(x => x.ProductItemId!.Value).Distinct().ToList();
        var itemCostDict = new Dictionary<Guid, decimal>();
        foreach (var itemId in itemIds)
        {
            var itemCost = await _db.ProductItems.AsNoTracking()
                .Where(pi => pi.TenantId == tenantId && pi.Id == itemId)
                .Select(pi => (decimal?)pi.Cost)
                .FirstOrDefaultAsync(ct);
            itemCostDict[itemId] = itemCost ?? 0m;
        }

        var purchaseBaseQ = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Date >= f && x.Date < tExclusive)
            .Where(x => !branchId.HasValue || branchId.Value == Guid.Empty || x.BranchId == branchId.Value);

        var purchases = await purchaseBaseQ
            .Select(x => new
            {
                x.Id,
                x.Date,
                x.PartnerName,
                x.GrandTotal,
                x.PurchaseType,
                x.PaymentMethod
            })
            .ToListAsync(ct);

        var purchaseItems = purchases.Count == 0
            ? new List<PurchaseItemReportRow>()
            : await _db.PurchaseItems.AsNoTracking()
                .Join(
                    purchaseBaseQ.Select(p => p.Id),
                    pi => pi.PurchaseId,
                    id => id,
                    (pi, _) => pi)
                .Where(x => x.TenantId == tenantId)
                .Select(x => new PurchaseItemReportRow(
                    x.PurchaseId,
                    x.Kind,
                    x.ProductCode,
                    x.ProductName,
                    x.Category,
                    x.Karat,
                    x.Quantity,
                    x.BirimIscilikHas,
                    x.OdenecekToplamHas))
                .ToListAsync(ct);

        var purchasePayments = purchases.Count == 0
            ? new List<PurchasePaymentReportRow>()
            : await _db.PurchasePayments.AsNoTracking()
                .Join(
                    purchaseBaseQ.Select(p => p.Id),
                    pp => pp.PurchaseId,
                    id => id,
                    (pp, _) => pp)
                .Where(x => x.TenantId == tenantId)
                .Select(x => new PurchasePaymentReportRow(
                    x.PurchaseId,
                    x.PaymentType,
                    x.UnitCode,
                    x.UnitAmount,
                    x.Amount,
                    x.GoldWeight))
                .ToListAsync(ct);

        var purchaseItemsByPurchaseId = purchaseItems
            .GroupBy(x => x.PurchaseId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var purchasePaymentsByPurchaseId = purchasePayments
            .GroupBy(x => x.PurchaseId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var saleResult = lines.Select(x =>
        {
            decimal rawCost = 0m;
            if (x.ProductItemId.HasValue && itemCostDict.TryGetValue(x.ProductItemId.Value, out var itemCost))
                rawCost = itemCost;
            else if (productMetaDict.TryGetValue(x.ProductCode ?? "", out var productMetaForCost))
                rawCost = productMetaForCost.CostRaw;

            var meta = productMetaDict.TryGetValue(x.ProductCode ?? "", out var m) ? m : null;
            var isSpecial = meta?.IsSpecial == true;
            var isZiynet = meta?.IsZiynet == true;
            var isSilver = meta?.IsSilver == true;
            var isForex = meta?.IsForex == true || IsForexText(x.Category) || (x.ProductCode ?? "").StartsWith("FX-", StringComparison.OrdinalIgnoreCase);
            var qty = x.Quantity > 0 ? x.Quantity : 1m;

            // SatisViewModel ile aynı maliyet mantığı:
            // - Special: cost alanı TL kabul edilir ve adet/qty ile çarpılır.
            // - Digerleri: cost alanı HAS kabul edilir; ziynette adet kadar çarpılır, tekilde 1x.
            decimal resolvedCostTl;
            decimal resolvedCostHas;
            var ziynetBirimAlisTl = isZiynet
                ? ResolveZiynetAlisUnitTl(x.ProductName, x.Karat, Ask, Bid)
                : 0m;
            if (isZiynet && ziynetBirimAlisTl > 0m)
            {
                // Ziynet maliyeti: anlık alış kuru x satılan adet.
                resolvedCostTl = Math.Round(ziynetBirimAlisTl * qty, 2, MidpointRounding.AwayFromZero);
                resolvedCostHas = TlToHas(resolvedCostTl);
            }
            else if (isSpecial || isSilver || isZiynet)
            {
                resolvedCostTl = Math.Round(rawCost * qty, 2, MidpointRounding.AwayFromZero);
                resolvedCostHas = TlToHas(resolvedCostTl);
            }
            else
            {
                var multiplier = isZiynet ? qty : 1m;
                resolvedCostHas = Math.Round(rawCost * multiplier, 6, MidpointRounding.AwayFromZero);
                resolvedCostTl = hasTl > 0
                    ? Math.Round(resolvedCostHas * hasTl, 2, MidpointRounding.AwayFromZero)
                    : 0m;
            }

            decimal resolvedRevenueHas;
            if (meta is not null && meta.TargetSaleHas > 0m)
            {
                var multiplier = isZiynet ? qty : 1m;
                resolvedRevenueHas = Math.Round(meta.TargetSaleHas * multiplier, 6, MidpointRounding.AwayFromZero);
            }
            else
            {
                resolvedRevenueHas = TlToHas(x.Revenue);
            }

            // Kâr/Zarar = Satış fiyatı - Maliyet fiyatı (HAS öncelikli).
            var profitHas = resolvedRevenueHas - resolvedCostHas;
            var profit = x.Revenue - resolvedCostTl;
            var marginPct = resolvedCostHas > 0m
                ? (profitHas / resolvedCostHas) * 100m
                : (resolvedCostTl > 0m ? (profit / resolvedCostTl) * 100m : 0m);
            var movementSegment = ResolveSalesMovementSegment(isSpecial, isZiynet, isForex, isSilver);
            var paymentMethod = salePaymentSummaryBySaleId.TryGetValue(x.SaleId, out var pmt) && !string.IsNullOrWhiteSpace(pmt)
                ? pmt
                : ResolveSalePaymentSummary(new[] { x.PaymentType ?? "" }.ToList());
            var quantityDisplay = ResolveSalesQuantityDisplay(movementSegment, x.Quantity, x.ProductCode, x.Karat, x.ProductName, x.Category);
            var revenueView = ResolveSalesMoneyDisplay(movementSegment, x.Revenue, resolvedRevenueHas);
            var costView = ResolveSalesMoneyDisplay(movementSegment, resolvedCostTl, resolvedCostHas);
            var profitView = ResolveSalesProfitDisplay(movementSegment, profit, profitHas);
            var urunTipi = ResolveZiynetUrunTipDisplay(
                isZiynet,
                x.ProductName,
                x.Category,
                meta?.Olcu,
                x.Karat);
            return new SalesPurchaseReportRow
            {
                Date = x.SaleDate,
                Type = "Satis",
                MovementKind = "Satış",
                MovementSegment = movementSegment,
                PaymentMethod = paymentMethod,
                RefNo = x.SaleId.ToString(),
                ProductCode = x.ProductCode ?? "",
                Product = string.IsNullOrWhiteSpace(x.ProductName) ? x.ProductCode : x.ProductName,
                UrunTipi = urunTipi,
                CustomerName = string.IsNullOrWhiteSpace(x.CustomerName) ? "-" : x.CustomerName,
                Category = x.Category ?? "",
                Karat = x.Karat ?? "",
                Quantity = x.Quantity,
                QuantityDisplay = quantityDisplay,
                Revenue = x.Revenue,
                RevenueHas = resolvedRevenueHas,
                RevenueViewDisplay = revenueView,
                Cost = resolvedCostTl,
                CostHas = resolvedCostHas,
                CostTlDisplay = $"{resolvedCostTl:N2} TL",
                CostViewDisplay = costView,
                Profit = profit,
                ProfitHas = profitHas,
                ProfitViewDisplay = profitView,
                MarginPct = marginPct,
                IsSpecialProduct = isSpecial,
                IsZiynet = isZiynet,
                IsForex = isForex,
                IsSilver = isSilver
            };
        }).ToList();

        var emanetSaleResult = emanetZiynetRows
            .Select(x =>
            {
                var qty = Math.Abs(x.Quantity);
                var customer = string.IsNullOrWhiteSpace(x.CustomerName) ? "-" : x.CustomerName.Trim();
                if (!customer.Contains("(emanet)", StringComparison.OrdinalIgnoreCase))
                    customer += " (emanet)";
                var tip = string.IsNullOrWhiteSpace(x.ItemType) ? "Yeni" : x.ItemType.Trim();
                var productText = string.IsNullOrWhiteSpace(tip)
                    ? (x.ItemName ?? "Ziynet")
                    : $"{x.ItemName} ({tip})";
                var revenue = x.TotalPriceTl.GetValueOrDefault();
                if (revenue <= 0m && x.UnitPriceTl.GetValueOrDefault() > 0m)
                    revenue = Math.Round(qty * x.UnitPriceTl.GetValueOrDefault(), 2, MidpointRounding.AwayFromZero);
                var cost = 0m;
                var birimAlis = x.HasEquivalent.GetValueOrDefault();
                if (birimAlis > 0m)
                    cost = Math.Round(qty * birimAlis, 2, MidpointRounding.AwayFromZero);
                var revenueHas = TlToHas(revenue);
                var costHas = TlToHas(cost);
                var profit = revenue - cost;
                var profitHas = revenueHas - costHas;
                var marginPct = cost > 0m ? (profit / cost) * 100m : 0m;
                return new SalesPurchaseReportRow
                {
                    Date = x.TxDate,
                    Type = "Satis",
                    MovementKind = "Satış",
                    MovementSegment = "Ziynet",
                    PaymentMethod = "Emanet",
                    RefNo = x.Id.ToString(),
                    ProductCode = "ZIYNET-EMANET",
                    Product = productText,
                    UrunTipi = ResolveZiynetUrunTipDisplay(true, x.ItemName, "Ziynet", x.ItemType, null),
                    CustomerName = customer,
                    Category = "Ziynet",
                    Karat = "",
                    Quantity = qty,
                    QuantityDisplay = $"{Math.Round(qty, 0, MidpointRounding.AwayFromZero):N0} adet",
                    Revenue = revenue,
                    RevenueHas = revenueHas,
                    RevenueViewDisplay = ResolveSalesMoneyDisplay("Ziynet", revenue, revenueHas),
                    Cost = cost,
                    CostHas = costHas,
                    CostTlDisplay = $"{cost:N2} TL",
                    CostViewDisplay = ResolveSalesMoneyDisplay("Ziynet", cost, costHas),
                    Profit = profit,
                    ProfitHas = profitHas,
                    ProfitViewDisplay = ResolveSalesProfitDisplay("Ziynet", profit, profitHas),
                    MarginPct = marginPct,
                    IsSpecialProduct = false,
                    IsZiynet = true,
                    IsForex = false,
                    IsSilver = false
                };
            })
            .ToList();

        var purchaseResult = purchases.Select(x =>
        {
            var items = purchaseItemsByPurchaseId.TryGetValue(x.Id, out var list) ? list : new List<PurchaseItemReportRow>();
            var pays = purchasePaymentsByPurchaseId.TryGetValue(x.Id, out var payList) ? payList : new List<PurchasePaymentReportRow>();
            var movementKind = ResolvePurchaseMovementKind(items, x.PurchaseType);
            var paymentMethod = ResolvePurchasePaymentMethodText(pays, x.PaymentMethod);
            var category = ResolvePurchaseCategoryText(items, movementKind);
            var quantity = ResolvePurchaseQuantityValue(items);
            var quantityDisplay = ResolvePurchaseQuantityDisplay(items);
            var costTlDisplay = ResolvePurchaseCostDisplay(cost: x.GrandTotal, payments: pays);
            var revenue = 0m;
            var cost = x.GrandTotal;
            var profit = revenue - cost;
            var costHas = x.PurchaseType == PurchaseType.Toptanci
                ? ResolveToptanciPurchaseCostHas(items)
                : TlToHas(cost);
            var profitHas = -costHas;
            return new SalesPurchaseReportRow
            {
                Date = x.Date,
                Type = "Alis",
                MovementKind = movementKind,
                MovementSegment = "",
                PaymentMethod = paymentMethod,
                RefNo = x.Id.ToString(),
                ProductCode = "",
                Product = x.PartnerName ?? "-",
                UrunTipi = "",
                CustomerName = "-",
                Category = category,
                Karat = "",
                Quantity = quantity,
                QuantityDisplay = quantityDisplay,
                Revenue = revenue,
                RevenueHas = TlToHas(revenue),
                RevenueViewDisplay = $"{revenue:N2} TL",
                Cost = cost,
                CostHas = costHas,
                CostTlDisplay = costTlDisplay,
                CostViewDisplay = costTlDisplay,
                Profit = profit,
                ProfitHas = profitHas,
                ProfitViewDisplay = $"{profit:N2} TL",
                MarginPct = 0m,
                IsSpecialProduct = false,
                IsZiynet = false,
                IsForex = false,
                IsSilver = false
            };
        }).ToList();

        var deduped = saleResult.Concat(emanetSaleResult).Concat(purchaseResult)
            .GroupBy(x => new
            {
                x.Type,
                x.MovementKind,
                x.MovementSegment,
                x.PaymentMethod,
                x.RefNo,
                x.ProductCode,
                x.Product,
                x.UrunTipi,
                x.CustomerName,
                x.Category,
                x.Karat,
                Date = x.Date,
                x.Quantity,
                x.QuantityDisplay,
                x.Revenue,
                x.RevenueViewDisplay,
                x.Cost,
                x.CostTlDisplay,
                x.CostViewDisplay,
                x.Profit,
                x.ProfitViewDisplay,
                x.MarginPct,
                x.IsSpecialProduct,
                x.IsZiynet,
                x.IsForex,
                x.IsSilver
            })
            .Select(g => g.First())
            .OrderByDescending(x => x.Date)
            .ToList();

        return Ok(deduped);
    }

    [HttpGet("reports/loss-lines")]
    public async Task<IActionResult> LossLines(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var (f, tExclusive) = NormalizeDateRangeInclusive(from, to);
        var rates = _gold.LatestOrEmpty();
        if (rates.Count == 0) rates = await _gold.RefreshAsync(ct);
        decimal Ask(string code) => rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Ask ?? 0m;
        decimal Bid(string code) => rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Bid ?? 0m;
        var hasTl = Ask("G24_TRY");
        if (hasTl <= 0) hasTl = Bid("G24_TRY");

        var salesQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= f && x.CreatedAt < tExclusive);
        salesQ = salesQ.Where(x => x.PaymentType != "ManualEInvoice");
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            salesQ = salesQ.Where(x => x.BranchId == branchId.Value);

        var rows = await salesQ.SelectMany(x => x.Items
                .Where(i => string.IsNullOrWhiteSpace(i.ProductCode) || !EF.Functions.Like(i.ProductCode, "MANUAL-%")))
            .Select(i => new
            {
                i.SaleId,
                i.ProductCode,
                i.ProductName,
                i.Quantity,
                i.ProductItemId,
                i.LineTotal
            })
            .ToListAsync(ct);

        var lossCodes = rows
            .Select(x => x.ProductCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lossCodeSet = new HashSet<string>(lossCodes, StringComparer.OrdinalIgnoreCase);
        var lossMetaQ = _db.Products.AsNoTracking()
            .Where(p => p.TenantId == tenantId && !p.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            lossMetaQ = lossMetaQ.Where(p => p.BranchId == branchId.Value);
        var lossProductCostMap = await lossMetaQ
            .Select(p => new { p.ProductCode, p.Cost, p.IsSpecialProduct, p.InventoryType, p.Category, p.Name, p.Karat })
            .ToListAsync(ct);
        var lossProductMetaDict = lossProductCostMap
            .Where(x => lossCodeSet.Contains(x.ProductCode))
            .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var p = g.First();
                    return new
                    {
                        CostRaw = p.Cost ?? 0m,
                        IsSpecial = p.IsSpecialProduct,
                        IsZiynet = p.InventoryType == kuyumcu_domain.Enums.InventoryType.Ziynet,
                        IsSilver = IsSilverProduct(p.Category, p.Name, p.Karat),
                        IsForex = IsForexText(p.Category) || IsForexText(p.Name),
                    };
                },
                StringComparer.OrdinalIgnoreCase);

        var lossItemIds = rows.Where(x => x.ProductItemId.HasValue).Select(x => x.ProductItemId!.Value).Distinct().ToList();
        var lossItemCostDict = new Dictionary<Guid, decimal>();
        foreach (var itemId in lossItemIds)
        {
            var itemCost = await _db.ProductItems.AsNoTracking()
                .Where(pi => pi.TenantId == tenantId && pi.Id == itemId)
                .Select(pi => (decimal?)pi.Cost)
                .FirstOrDefaultAsync(ct);
            lossItemCostDict[itemId] = itemCost ?? 0m;
        }

        var losses = rows
            .Select(x =>
            {
                decimal rawCost = 0m;
                if (x.ProductItemId.HasValue && lossItemCostDict.TryGetValue(x.ProductItemId.Value, out var itemCost))
                    rawCost = itemCost;
                else if (lossProductMetaDict.TryGetValue(x.ProductCode ?? "", out var productMetaForCost))
                    rawCost = productMetaForCost.CostRaw;

                var meta = lossProductMetaDict.TryGetValue(x.ProductCode ?? "", out var m) ? m : null;
                var isSpecial = meta?.IsSpecial == true;
                var isZiynet = meta?.IsZiynet == true;
                var isSilver = meta?.IsSilver == true;
                var isForex = meta?.IsForex == true || (x.ProductCode ?? "").StartsWith("FX-", StringComparison.OrdinalIgnoreCase);
                var qty = x.Quantity > 0 ? x.Quantity : 1m;
                decimal resolvedCost;
                if (isSpecial || isSilver || isZiynet)
                {
                    resolvedCost = Math.Round(rawCost * qty, 2, MidpointRounding.AwayFromZero);
                }
                else
                {
                    var multiplier = isZiynet ? qty : 1m;
                    var costHas = Math.Round(rawCost * multiplier, 6, MidpointRounding.AwayFromZero);
                    resolvedCost = hasTl > 0
                        ? Math.Round(costHas * hasTl, 2, MidpointRounding.AwayFromZero)
                        : 0m;
                }
                return new
                {
                    x.SaleId,
                    Product = string.IsNullOrWhiteSpace(x.ProductName) ? x.ProductCode : x.ProductName,
                    MovementSegment = ResolveSalesMovementSegment(isSpecial, isZiynet, isForex, isSilver),
                    Revenue = x.LineTotal,
                    Cost = resolvedCost,
                    Loss = resolvedCost - x.LineTotal
                };
            })
            .Where(x => x.Revenue < x.Cost)
            .OrderByDescending(x => x.Loss);

        return Ok(losses);
    }

    [HttpGet("reports/stock-performance")]
    public async Task<IActionResult> StockPerformance([FromQuery] Guid? branchId, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var productsQ = _db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            productsQ = productsQ.Where(x => x.BranchId == branchId.Value);
        var itemsQ = _db.ProductItems.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsInStock && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            itemsQ = itemsQ.Where(x => x.BranchId == branchId.Value);

        var items = await itemsQ
            .Join(productsQ, i => i.ProductId, p => p.Id, (i, p) => new
            {
                p.Category,
                p.Cost,
                i.Weight
            })
            .ToListAsync(ct);

        var ziynetQ = productsQ.Where(x => (x.InventoryType == kuyumcu_domain.Enums.InventoryType.Ziynet || x.IsSpecialProduct));
        var ziynet = await ziynetQ.Select(x => new
        {
            x.Category,
            Count = (x.StokMiktari ?? 0),
            Weight = (x.WeightGr ?? 0m) * (x.StokMiktari ?? 0),
            Cost = ((x.Cost ?? 0m) * (x.StokMiktari ?? 0))
        }).ToListAsync(ct);

        var byCat = items.GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Diger" : x.Category!)
            .Select(g => new
            {
                Category = g.Key,
                Quantity = g.Count(),
                StockCost = g.Sum(x => (x.Cost ?? 0m) * x.Weight)
            }).ToList();

        foreach (var z in ziynet.GroupBy(x => string.IsNullOrWhiteSpace(x.Category) ? "Diger" : x.Category!))
        {
            var ex = byCat.FirstOrDefault(x => x.Category == z.Key);
            if (ex == null)
            {
                byCat.Add(new { Category = z.Key, Quantity = z.Sum(x => x.Count), StockCost = z.Sum(x => x.Cost) });
            }
        }

        var stockCost = byCat.Sum(x => x.StockCost);
        var marketValue = stockCost * 1.08m;
        return Ok(new
        {
            TotalStockCost = stockCost,
            TotalMarketValue = marketValue,
            Categories = byCat.OrderByDescending(x => x.Quantity)
        });
    }

    [HttpGet("reports/dead-stock")]
    public async Task<IActionResult> DeadStock([FromQuery] Guid? branchId, [FromQuery] int days = 180, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (days < 30) days = 30;
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var productsQ = _db.Products.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            productsQ = productsQ.Where(x => x.BranchId == branchId.Value);
        var inStockCodes = await _db.ProductItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IsInStock && !x.IsDeleted)
            .Where(x => !branchId.HasValue || branchId.Value == Guid.Empty || x.BranchId == branchId.Value)
            .Join(productsQ, i => i.ProductId, p => p.Id, (i, p) => p.ProductCode)
            .Distinct()
            .ToListAsync(ct);
        var soldSinceQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= cutoff);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            soldSinceQ = soldSinceQ.Where(x => x.BranchId == branchId.Value);
        var soldSince = await soldSinceQ
            .SelectMany(x => x.Items)
            .Select(i => i.ProductCode)
            .Distinct()
            .ToListAsync(ct);

        var inStockSet = new HashSet<string>(inStockCodes, StringComparer.OrdinalIgnoreCase);
        var soldSet = new HashSet<string>(soldSince, StringComparer.OrdinalIgnoreCase);
        var allProducts = await productsQ
            .Select(p => new
            {
                p.ProductCode,
                p.Name,
                p.Category,
                p.Karat,
                p.WeightGr,
                p.Cost
            })
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var dead = allProducts.Where(p => inStockSet.Contains(p.ProductCode) && !soldSet.Contains(p.ProductCode));
        return Ok(dead);
    }

    [HttpGet("reports/supplier-scorecard")]
    public async Task<IActionResult> SupplierScorecard(
        [FromQuery] Guid? branchId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var f = from ?? DateTime.UtcNow.Date.AddDays(-90);
        var t = to ?? DateTime.UtcNow.Date.AddDays(1);

        var purchasesQ = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Date >= f && x.Date < t && x.SupplierId.HasValue);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            purchasesQ = purchasesQ.Where(x => x.BranchId == branchId.Value);

        var purchases = await purchasesQ
            .Select(x => new { x.SupplierId, x.GrandTotal })
            .ToListAsync(ct);
        var suppliers = await _db.Suppliers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.CompanyName })
            .ToListAsync(ct);

        var rows = purchases
            .GroupBy(x => x.SupplierId!.Value)
            .Select(g => new
            {
                SupplierId = g.Key,
                TxCount = g.Count(),
                TotalPurchase = g.Sum(x => x.GrandTotal),
                AvgPurchase = g.Average(x => x.GrandTotal)
            })
            .OrderByDescending(x => x.TotalPurchase)
            .Select(x => new
            {
                x.SupplierId,
                SupplierName = suppliers.FirstOrDefault(s => s.Id == x.SupplierId)?.CompanyName ?? "-",
                x.TxCount,
                x.TotalPurchase,
                x.AvgPurchase
            });

        return Ok(rows);
    }

    [HttpGet("reports/pnl-table")]
    public async Task<IActionResult> PnlTable([FromQuery] Guid? branchId, [FromQuery] string groupBy = "month", CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var isYear = string.Equals(groupBy, "year", StringComparison.OrdinalIgnoreCase);
        var from = isYear ? new DateTime(DateTime.UtcNow.Year - 4, 1, 1) : DateTime.UtcNow.Date.AddMonths(-11);

        var salesQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= from);
        var purchaseQ = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Date >= from);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
        {
            salesQ = salesQ.Where(x => x.BranchId == branchId.Value);
            purchaseQ = purchaseQ.Where(x => x.BranchId == branchId.Value);
        }

        var saleRows = await salesQ.SelectMany(x => x.Items).Join(_db.Sales.AsNoTracking(), i => i.SaleId, s => s.Id, (i, s) => new { s.CreatedAt, i.LineTotal }).ToListAsync(ct);
        var purRows = await purchaseQ.Select(x => new { x.Date, x.GrandTotal }).ToListAsync(ct);

        string Key(DateTime d) => isYear ? d.ToString("yyyy") : d.ToString("yyyy-MM");

        var keys = saleRows.Select(x => Key(x.CreatedAt)).Union(purRows.Select(x => Key(x.Date))).Distinct().OrderBy(x => x).ToList();
        var rows = keys.Select(k =>
        {
            var s = saleRows.Where(x => Key(x.CreatedAt) == k).Sum(x => x.LineTotal);
            var p = purRows.Where(x => Key(x.Date) == k).Sum(x => x.GrandTotal);
            return new { Period = k, Sales = s, Purchase = p, NetProfit = s - p };
        });
        return Ok(rows);
    }

    public sealed record CloseDayReq(Guid BranchId, DateTime? BusinessDate = null);

    [HttpPost("dayend/close")]
    public async Task<IActionResult> CloseDay([FromBody] CloseDayReq req, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        if (req.BranchId == Guid.Empty) return BadRequest(new { error = "BranchId zorunludur." });
        var day = (req.BusinessDate ?? DateTime.UtcNow).Date;
        var next = day.AddDays(1);

        var exists = await _db.DayEndReports
            .AnyAsync(x => x.TenantId == tenantId && x.BranchId == req.BranchId && x.BusinessDate == day && !x.IsDeleted, ct);
        if (exists) return BadRequest(new { error = "Bu gun zaten kapatildi." });

        var allAccounts = await _db.CashAccounts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == req.BranchId && !x.IsDeleted)
            .ToListAsync(ct);

        decimal SumByCurrency(string cur) => allAccounts.Where(x => x.Currency == cur).Sum(x => x.CurrentBalance);

        var income = await _db.CashTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == req.BranchId && !x.IsDeleted && x.TxDate >= day && x.TxDate < next && x.TxType == "Income")
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
        var expense = await _db.CashTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == req.BranchId && !x.IsDeleted && x.TxDate >= day && x.TxDate < next && x.TxType == "Expense")
            .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;

        var row = new kuyumcu_domain.Entities.DayEndReport
        {
            TenantId = tenantId,
            BranchId = req.BranchId,
            BusinessDate = day,
            OpeningTl = 0m,
            OpeningUsd = 0m,
            OpeningEur = 0m,
            OpeningHas = 0m,
            ClosingTl = SumByCurrency("TL"),
            ClosingUsd = SumByCurrency("USD"),
            ClosingEur = SumByCurrency("EUR"),
            ClosingHas = SumByCurrency("HAS"),
            TotalIncomeTl = income,
            TotalExpenseTl = expense,
            Status = "Closed"
        };

        _db.DayEndReports.Add(row);
        await _db.SaveChangesAsync(ct);

        return Ok(new { row.Id, row.BusinessDate, row.ClosingTl, row.ClosingUsd, row.ClosingEur, row.ClosingHas });
    }

    [HttpGet("dayend/{id:guid}/pdf")]
    public async Task<IActionResult> DayEndPdf(Guid id, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var row = await _db.DayEndReports.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (row == null) return NotFound();

        var lines = new[]
        {
            "Gun Sonu Raporu",
            $"Tarih: {row.BusinessDate:yyyy-MM-dd}",
            $"TL: {row.ClosingTl.ToString("N2", CultureInfo.InvariantCulture)}",
            $"USD: {row.ClosingUsd.ToString("N2", CultureInfo.InvariantCulture)}",
            $"EUR: {row.ClosingEur.ToString("N2", CultureInfo.InvariantCulture)}",
            $"HAS: {row.ClosingHas.ToString("N3", CultureInfo.InvariantCulture)}",
            $"Gelir(TL): {row.TotalIncomeTl.ToString("N2", CultureInfo.InvariantCulture)}",
            $"Gider(TL): {row.TotalExpenseTl.ToString("N2", CultureInfo.InvariantCulture)}"
        };

        var bytes = BuildSimplePdf(lines);
        return File(bytes, "application/pdf", $"gunsonu-{row.BusinessDate:yyyyMMdd}.pdf");
    }

    /// <summary>
    /// Dönem içi satış satırlarında toplam kâr (satış tutarı TL − çözümlenen maliyet TL).
    /// Genel özet kartları ile satış hareketleri tablosu aynı maliyet kurallarını kullanır.
    /// </summary>
    private async Task<decimal> SumSaleLineNetProfitTlAsync(
        Guid tenantId,
        Guid? branchId,
        DateTime from,
        DateTime toExclusive,
        CancellationToken ct)
    {
        var salesQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.CreatedAt >= from && x.CreatedAt < toExclusive);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            salesQ = salesQ.Where(x => x.BranchId == branchId.Value);

        var rates = _gold.LatestOrEmpty();
        if (rates.Count == 0) rates = await _gold.RefreshAsync(ct);
        decimal Ask(string code) => rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Ask ?? 0m;
        decimal Bid(string code) => rates.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))?.Bid ?? 0m;
        var hasTl = Ask("G24_TRY");
        if (hasTl <= 0) hasTl = Bid("G24_TRY");

        var lines = await salesQ.SelectMany(s => s.Items.Select(i => new
            {
                i.ProductCode,
                i.ProductName,
                i.Karat,
                i.ProductItemId,
                i.Quantity,
                Revenue = i.LineTotal
            }))
            .ToListAsync(ct);

        if (lines.Count == 0)
            return 0m;

        var productCodes = lines
            .Select(x => x.ProductCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var productCodeSet = new HashSet<string>(productCodes, StringComparer.OrdinalIgnoreCase);

        var netProfitMetaQ = _db.Products.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            netProfitMetaQ = netProfitMetaQ.Where(p => p.BranchId == branchId.Value);
        var productCostMap = await netProfitMetaQ
            .Select(p => new { p.ProductCode, p.Cost, p.IsSpecialProduct, p.InventoryType, p.Category, p.Name, p.Karat })
            .ToListAsync(ct);
        var productMetaDict = productCostMap
            .Where(x => productCodeSet.Contains(x.ProductCode))
            .GroupBy(x => x.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var p = g.First();
                    return new
                    {
                        CostRaw = p.Cost ?? 0m,
                        IsSpecial = p.IsSpecialProduct,
                        IsZiynet = p.InventoryType == kuyumcu_domain.Enums.InventoryType.Ziynet,
                        IsSilver = IsSilverProduct(p.Category, p.Name, p.Karat),
                        IsForex = IsForexText(p.Category) || IsForexText(p.Name)
                    };
                },
                StringComparer.OrdinalIgnoreCase);

        var itemIds = lines.Where(x => x.ProductItemId.HasValue).Select(x => x.ProductItemId!.Value).Distinct().ToList();
        var itemCostDict = new Dictionary<Guid, decimal>();
        foreach (var itemId in itemIds)
        {
            var itemCost = await _db.ProductItems.AsNoTracking()
                .Where(pi => pi.TenantId == tenantId && pi.Id == itemId)
                .Select(pi => (decimal?)pi.Cost)
                .FirstOrDefaultAsync(ct);
            itemCostDict[itemId] = itemCost ?? 0m;
        }

        decimal total = 0m;
        foreach (var x in lines)
        {
            decimal rawCost = 0m;
            var hasItemCost = false;
            if (x.ProductItemId.HasValue && itemCostDict.TryGetValue(x.ProductItemId.Value, out var itemCost))
            {
                rawCost = itemCost;
                hasItemCost = true;
            }
            else if (productMetaDict.TryGetValue(x.ProductCode ?? "", out var productMetaForCost))
                rawCost = productMetaForCost.CostRaw;

            var meta = productMetaDict.TryGetValue(x.ProductCode ?? "", out var m) ? m : null;
            var isSpecial = meta?.IsSpecial == true;
            var isZiynet = meta?.IsZiynet == true;
            var isSilver = meta?.IsSilver == true;
            var isForex = meta?.IsForex == true
                          || (x.ProductCode ?? "").StartsWith("FX-", StringComparison.OrdinalIgnoreCase);
            var qty = x.Quantity > 0 ? x.Quantity : 1m;

            decimal resolvedCostTl;
            var ziynetBirimAlisTl = isZiynet
                ? ResolveZiynetAlisUnitTl(x.ProductName, x.Karat, Ask, Bid)
                : 0m;
            if (isZiynet && ziynetBirimAlisTl > 0m)
            {
                resolvedCostTl = Math.Round(ziynetBirimAlisTl * qty, 2, MidpointRounding.AwayFromZero);
            }
            else if (hasItemCost || isSpecial || isSilver || isForex || isZiynet)
            {
                // ProductItem maliyeti depo/purchase akışında TL tutulur.
                resolvedCostTl = Math.Round(rawCost * qty, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                var multiplier = isZiynet ? qty : 1m;
                var resolvedCostHas = Math.Round(rawCost * multiplier, 6, MidpointRounding.AwayFromZero);
                resolvedCostTl = hasTl > 0
                    ? Math.Round(resolvedCostHas * hasTl, 2, MidpointRounding.AwayFromZero)
                    : 0m;
            }

            total += x.Revenue - resolvedCostTl;
        }

        return total;
    }

    private static string ResolvePurchaseMovementKind(IReadOnlyList<PurchaseItemReportRow> items, PurchaseType purchaseType)
    {
        if (items is null || items.Count == 0)
            return purchaseType == PurchaseType.Toptanci ? "Toptancı Alışı" : "Alış";

        var kinds = items
            .Select(MapPurchaseItemKind)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (kinds.Count == 0)
            return purchaseType == PurchaseType.Toptanci ? "Toptancı Alışı" : "Alış";
        if (kinds.Count == 1)
            return kinds[0];
        return "Karma";
    }

    private static string ResolvePurchasePaymentMethodText(IReadOnlyList<PurchasePaymentReportRow> payments, PurchasePaymentMethod fallback)
    {
        if (payments is null || payments.Count == 0)
            return ToPurchasePaymentMethodLabel(fallback);

        var labels = payments
            .Select(x => ToPurchasePaymentTypeLabel(x.PaymentType))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return labels.Count == 0 ? ToPurchasePaymentMethodLabel(fallback) : string.Join(" + ", labels);
    }

    private static string ResolvePurchaseCategoryText(IReadOnlyList<PurchaseItemReportRow> items, string movementKind)
    {
        if (items is null || items.Count == 0) return "-";

        var primary = items
            .OrderByDescending(x => Math.Abs(x.Quantity))
            .First();
        var detail = ResolvePurchaseItemDetail(primary);
        if (string.IsNullOrWhiteSpace(detail))
            return movementKind;
        return $"{movementKind}>{detail}";
    }

    private static decimal ResolvePurchaseQuantityValue(IReadOnlyList<PurchaseItemReportRow> items)
        => items is null || items.Count == 0 ? 0m : items.Sum(x => Math.Abs(x.Quantity));

    private static string ResolvePurchaseQuantityDisplay(IReadOnlyList<PurchaseItemReportRow> items)
    {
        if (items is null || items.Count == 0) return "-";
        var total = items.Sum(x => Math.Abs(x.Quantity));
        var units = items
            .Select(ResolvePurchaseItemUnit)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (units.Count != 1)
            return $"{items.Count} kalem";

        var unit = units[0];
        if (unit.Equals("sayi", StringComparison.OrdinalIgnoreCase))
            return $"{total:N2}";
        if (unit.Equals("adet", StringComparison.OrdinalIgnoreCase))
        {
            var adet = Math.Round(total, 0, MidpointRounding.AwayFromZero);
            return $"{adet:N0} adet";
        }
        return $"{total:N2} {unit}";
    }

    private static decimal ResolveToptanciPurchaseCostHas(IReadOnlyList<PurchaseItemReportRow> items)
    {
        if (items is null || items.Count == 0) return 0m;

        // Toplam has maliyeti = (Toplam gram x Milyem) + (Toplam gram x Birim işçilik has/gr)
        // Çoklu satırda satır bazında toplanır.
        var total = items.Sum(item =>
        {
            var gram = Math.Abs(item.Quantity);
            if (gram <= 0m) return 0m;

            var milyem = MilyemFromKarat(item.Karat);
            var birimIscilikHas = item.BirimIscilikHas.GetValueOrDefault();
            return (gram * milyem) + (gram * birimIscilikHas);
        });

        if (total > 0m)
            return Math.Round(total, 6, MidpointRounding.AwayFromZero);

        // Eski/eksik kayıtlarda yardımcı fallback.
        var fallback = items.Sum(x => x.OdenecekToplamHas.GetValueOrDefault());
        return fallback > 0m
            ? Math.Round(fallback, 6, MidpointRounding.AwayFromZero)
            : 0m;
    }

    private static string MapPurchaseItemKind(PurchaseItemReportRow item)
    {
        var special = IsSpecialPurchaseItem(item);
        if (special) return "Özel Ürün";

        return item.Kind switch
        {
            ItemKind.Forex => "Döviz",
            ItemKind.Ziynet => "Ziynet",
            ItemKind.Scrap => "Hurda",
            ItemKind.Silver => "Gümüş",
            ItemKind.Product or ItemKind.Finished or ItemKind.CraftedGold => "Toptancı Alışı",
            ItemKind.GramGold => "Has",
            _ => IsForexText(item.Category) || IsForexText(item.ProductName) ? "Döviz" : "Alış"
        };
    }

    private static string ResolvePurchaseItemDetail(PurchaseItemReportRow item)
    {
        var kind = MapPurchaseItemKind(item);
        if (kind.Equals("Döviz", StringComparison.OrdinalIgnoreCase))
            return ExtractForexUnit(item);
        if (!string.IsNullOrWhiteSpace(item.Category))
            return item.Category.Trim();
        if (!string.IsNullOrWhiteSpace(item.ProductName))
            return item.ProductName.Trim();
        if (!string.IsNullOrWhiteSpace(item.Karat) && !string.IsNullOrWhiteSpace(item.ProductCode))
            return $"{item.Karat.Trim()} {item.ProductCode.Trim()}";
        return (item.ProductCode ?? "").Trim();
    }

    private static string ResolvePurchaseItemUnit(PurchaseItemReportRow item)
    {
        if (item.Kind == ItemKind.Forex || IsForexText(item.Category) || IsForexText(item.ProductName))
            return "sayi";
        if (item.Kind == ItemKind.Ziynet)
            return "adet";
        if (IsSpecialPurchaseItem(item))
            return "adet";
        if (item.Kind is ItemKind.Scrap or ItemKind.GramGold or ItemKind.CraftedGold or ItemKind.Silver)
            return "gr";
        if (item.Kind is ItemKind.Product or ItemKind.Finished)
            return "gr";
        return Math.Abs(item.Quantity % 1m) > 0.0001m ? "gr" : "adet";
    }

    private static bool IsSpecialPurchaseItem(PurchaseItemReportRow item)
    {
        var name = (item.ProductName ?? "").Trim();
        var category = (item.Category ?? "").Trim();
        return name.Contains("özel", StringComparison.CurrentCultureIgnoreCase)
               || name.Contains("ozel", StringComparison.CurrentCultureIgnoreCase)
               || category.Contains("özel", StringComparison.CurrentCultureIgnoreCase)
               || category.Contains("ozel", StringComparison.CurrentCultureIgnoreCase)
               || category.Contains("saat", StringComparison.CurrentCultureIgnoreCase);
    }

    private static string ResolvePurchaseCostDisplay(decimal cost, IReadOnlyList<PurchasePaymentReportRow> payments)
    {
        if (payments is null || payments.Count == 0)
            return $"{cost:N2} TL";

        decimal SumTl(PurchasePaymentType t) => payments.Where(x => x.PaymentType == t).Sum(x => x.Amount);
        var cashTl = SumTl(PurchasePaymentType.Cash);
        var bankTl = SumTl(PurchasePaymentType.Bank);
        var creditTl = SumTl(PurchasePaymentType.Credit);
        var takasTl = SumTl(PurchasePaymentType.Takas);
        var goldTl = SumTl(PurchasePaymentType.Gold);

        var takasHas = payments
            .Where(x => x.PaymentType == PurchasePaymentType.Takas)
            .Sum(x =>
            {
                var unit = NormalizeCurrency(x.UnitCode);
                if (unit == "HAS") return Math.Abs(x.UnitAmount ?? 0m);
                return Math.Abs(x.GoldWeight ?? 0m);
            });

        var parts = new List<string>();
        if (cashTl > 0m) parts.Add($"Nakit: {cashTl:N2} TL");
        if (bankTl > 0m) parts.Add($"Havale/EFT: {bankTl:N2} TL");
        if (creditTl > 0m) parts.Add($"Veresiye: {creditTl:N2} TL");
        if (goldTl > 0m) parts.Add($"Has/Gümüş: {goldTl:N2} TL");
        if (takasTl > 0m)
        {
            if (takasHas > 0m) parts.Add($"Takas: {takasTl:N2} TL ({takasHas:N4} HAS)");
            else parts.Add($"Takas: {takasTl:N2} TL");
        }

        if (parts.Count == 0) return $"{cost:N2} TL";
        return string.Join(" + ", parts);
    }

    private static string ExtractForexUnit(PurchaseItemReportRow item)
    {
        static string NormalizeUnit(string raw)
        {
            var u = (raw ?? "").Trim().ToUpperInvariant();
            if (u.StartsWith("FX-", StringComparison.OrdinalIgnoreCase))
                u = u[3..];
            return u switch
            {
                "EURO" => "EUR",
                "TRY" => "TL",
                "POUND" => "GBP",
                _ => u
            };
        }

        var candidates = new[]
        {
            NormalizeUnit(item.ProductCode),
            NormalizeUnit(item.Karat),
            NormalizeUnit(item.Category ?? ""),
            NormalizeUnit(item.ProductName)
        };

        foreach (var c in candidates)
        {
            if (c.Contains("USD")) return "USD";
            if (c.Contains("EUR")) return "EUR";
            if (c.Contains("GBP")) return "GBP";
            if (c.Contains("TL")) return "TL";
            if (c.Contains("HAS")) return "HAS";
            if (c.Contains("GUMUS") || c.Contains("GÜMÜŞ")) return "GÜMÜŞ";
        }

        return "DÖVİZ";
    }

    private static string ToPurchasePaymentTypeLabel(PurchasePaymentType paymentType)
    {
        return paymentType switch
        {
            PurchasePaymentType.Cash => "Nakit",
            PurchasePaymentType.Bank => "Havale/EFT",
            PurchasePaymentType.Credit => "Veresiye",
            PurchasePaymentType.Gold => "Has/Gümüş",
            PurchasePaymentType.Takas => "Takas",
            _ => "Diğer"
        };
    }

    private static string ToPurchasePaymentMethodLabel(PurchasePaymentMethod paymentMethod)
    {
        return paymentMethod switch
        {
            PurchasePaymentMethod.Nakit => "Nakit",
            PurchasePaymentMethod.HavaleEft => "Havale/EFT",
            PurchasePaymentMethod.NakitHavaleEft => "Nakit + Havale/EFT",
            PurchasePaymentMethod.Veresiye => "Veresiye",
            PurchasePaymentMethod.Emanet => "Emanet",
            _ => "Diğer"
        };
    }

    private static string ResolveSalesMovementSegment(bool isSpecial, bool isZiynet, bool isForex, bool isSilver)
    {
        if (isForex) return "Döviz";
        if (isSilver) return "Gümüş";
        if (isSpecial) return "Özel Ürünler";
        if (isZiynet) return "Ziynet";
        return "İşçilikli (Tekil)";
    }

    private static string ResolveZiynetUrunTipDisplay(
        bool isZiynet,
        string? productName,
        string? category,
        string? productOlcu,
        string? karat)
    {
        if (!isZiynet) return "";
        if (IsGramHasZiynetRow(productName, category, karat)) return "";

        var tip = NormalizeZiynetTipLabel(productOlcu);
        if (!string.IsNullOrWhiteSpace(tip)) return tip;

        // Bazı eski kayıtlar tip bilgisini ürün adında taşıyabiliyor.
        return NormalizeZiynetTipLabel(productName);
    }

    private static bool IsGramHasZiynetRow(string? productName, string? category, string? karat)
    {
        var token = ((productName ?? "") + " " + (category ?? "") + " " + (karat ?? ""))
            .Trim()
            .ToUpperInvariant()
            .Replace('İ', 'I');
        return token.Contains("GRAM") || token.Contains("HAS");
    }

    private static string NormalizeZiynetTipLabel(string? raw)
    {
        var t = (raw ?? "")
            .Trim()
            .ToUpperInvariant()
            .Replace('İ', 'I');
        if (string.IsNullOrWhiteSpace(t)) return "";
        if (t.Contains("YENI")) return "Yeni";
        if (t.Contains("ESKI")) return "Eski";
        return "";
    }

    private static string ResolveSalePaymentSummary(IReadOnlyList<string> methods)
    {
        if (methods is null || methods.Count == 0) return "-";
        var mapped = methods
            .Select(MapSalePaymentMethodLabel)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mapped.Count == 0) return "-";
        if (mapped.Count == 1) return mapped[0];
        return string.Join(" + ", mapped);
    }

    private static string MapSalePaymentMethodLabel(string? raw)
    {
        var cleaned = (raw ?? "")
            .Trim()
            .Trim('-', '|', ':', ';', '(', ')', '[', ']', '{', '}')
            .Replace("\t", " ")
            .Replace("  ", " ");
        var m = cleaned.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(m)) return "";
        return m switch
        {
            "NAKIT" => "Nakit",
            "IBAN" => "Havale/EFT",
            "HAVALE" => "Havale/EFT",
            "HAVALE/EFT" => "Havale/EFT",
            "BANKA" => "Havale/EFT",
            "KART" => "Kart",
            "VERESIYE" => "Veresiye",
            "TEDARIKCIVERESIYE" => "Veresiye",
            "TAKAS" => "Takas",
            _ => "Diğer"
        };
    }

    private static string ResolveSalesQuantityDisplay(
        string movementSegment,
        decimal quantity,
        string? productCode,
        string? karat,
        string? productName,
        string? category)
    {
        var qty = quantity > 0 ? quantity : 0m;
        if (movementSegment == "Ziynet" || movementSegment == "Özel Ürünler")
        {
            var adet = Math.Round(qty, 0, MidpointRounding.AwayFromZero);
            return $"{adet:N0} adet";
        }

        if (movementSegment == "Döviz")
        {
            var unit = ExtractForexUnitFromSales(productCode, karat, productName, category);
            return $"{qty:N2} {unit}";
        }

        return $"{qty:N3} gr";
    }

    private static string ResolveSalesMoneyDisplay(string movementSegment, decimal tl, decimal has)
    {
        if (movementSegment == "İşçilikli (Tekil)" || movementSegment == "Ziynet")
            return $"{tl:N2} TL ({has:N4} HAS)";
        return $"{tl:N2} TL";
    }

    private static string ResolveSalesProfitDisplay(string movementSegment, decimal tl, decimal has)
    {
        if (movementSegment == "İşçilikli (Tekil)" || movementSegment == "Ziynet")
            return tl >= 0m
                ? $"{tl:N2} TL ({has:N4} HAS) - Kar"
                : $"{Math.Abs(tl):N2} TL ({Math.Abs(has):N4} HAS) - Zarar";

        return tl >= 0m
            ? $"{tl:N2} TL - Kar"
            : $"{Math.Abs(tl):N2} TL - Zarar";
    }

    private static string ExtractForexUnitFromSales(string? productCode, string? karat, string? productName, string? category)
    {
        static string Norm(string? raw)
        {
            var u = (raw ?? "").Trim().ToUpperInvariant();
            if (u.StartsWith("FX-", StringComparison.OrdinalIgnoreCase))
                u = u[3..];
            return u switch
            {
                "EURO" => "EUR",
                "POUND" => "GBP",
                "TRY" => "TL",
                _ => u
            };
        }

        var parts = new[] { Norm(productCode), Norm(karat), Norm(productName), Norm(category) };
        foreach (var p in parts)
        {
            if (p.Contains("USD")) return "USD";
            if (p.Contains("EUR")) return "EUR";
            if (p.Contains("GBP")) return "GBP";
            if (p.Contains("TL")) return "TL";
        }
        return "DÖVİZ";
    }

    private static byte[] BuildSimplePdf(IEnumerable<string> lines)
    {
        var text = string.Join("\\n", lines.Select(EscapePdfText));
        var content = $"BT /F1 12 Tf 40 760 Td ({text}) Tj ET";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var header = "%PDF-1.4\n";
        var obj1 = "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n";
        var obj2 = "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n";
        var obj3 = "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >> endobj\n";
        var obj4 = "4 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n";
        var obj5 = $"5 0 obj << /Length {contentBytes.Length} >> stream\n{content}\nendstream endobj\n";

        var body = obj1 + obj2 + obj3 + obj4 + obj5;
        var xrefStart = Encoding.ASCII.GetByteCount(header + body);
        var xref = "xref\n0 6\n0000000000 65535 f \n";
        var trailer = $"trailer << /Size 6 /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF";
        return Encoding.ASCII.GetBytes(header + body + xref + trailer);
    }

    private static string EscapePdfText(string s)
        => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static bool IsSilverProduct(string? category, string? name, string? karat)
    {
        return IsSilverText(category) || IsSilverText(name) || IsSilverText(karat);
    }

    /// <summary>Ziynet satırı gümüş mü — altın HAS toplamlarına girmez; Mal/ZiynetTipi dahil.</summary>
    private static bool IsSilverZiynetProduct(
        string? category,
        string? name,
        string? karat,
        string? ziynetTipi,
        string? malTanim)
    {
        return IsSilverProduct(category, name, karat)
               || IsSilverText(ziynetTipi)
               || IsSilverText(malTanim);
    }

    private static bool IsSilverText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var n = text.Trim()
            .ToLowerInvariant()
            .Replace('ı', 'i')
            .Replace('İ', 'i')
            .Replace('ş', 's')
            .Replace('ğ', 'g')
            .Replace('ü', 'u')
            .Replace('ö', 'o')
            .Replace('ç', 'c');
        return n.Contains("gumus") || n.Contains("silver");
    }

    private static bool IsForexText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var n = text.Trim()
            .ToUpperInvariant()
            .Replace('İ', 'I');
        return n.Contains("DOVIZ") || n.Contains("DÖVIZ") || n.Contains("DÖVİZ") || n.Contains("FOREX");
    }

    /// <summary>StokDepoViewModel.IsGumusHammaddeSatiri ile aynı — hammadde HAS’a gümüş satırı girmez.</summary>
    private static bool IsGumusHammaddeHavuzRow(string? malTanimNorm, string? ayar)
    {
        return IsSilverText(malTanimNorm) || IsSilverText(ayar);
    }

    private static decimal MilyemFromKarat(string? karat)
    {
        var a = (karat ?? "").Trim().ToUpperInvariant();
        if (a.Contains("24") || a.Contains("999")) return 1.000m;
        if (a.Contains("22") || a.Contains("916")) return 0.916m;
        if (a.Contains("18") || a.Contains("750")) return 0.750m;
        if (a.Contains("14") || a.Contains("585")) return 0.585m;
        return 0m;
    }

    private static decimal MilyemFromAyarForStockPanel(string? ayar)
    {
        var a = (ayar ?? "").Trim().ToUpperInvariant();
        if (a.Contains("14") || a.Contains("585")) return 0.585m;
        if (a.Contains("18") || a.Contains("750")) return 0.750m;
        if (a.Contains("22") || a.Contains("916")) return 0.916m;
        if (a.Contains("24") || a.Contains("999") || a.Contains("1000")) return 1.000m;
        // StokDepoViewModel ile aynı fallback
        return 0.916m;
    }

    /// <summary>WPF DepoStokSatirYukleyici.DepotAyarKey ile aynı — havuz satırı ayar anahtarı.</summary>
    private static string DepotAyarKeyForHavuz(string ayar)
    {
        if (string.IsNullOrWhiteSpace(ayar)) return "";
        var a = ayar.Trim().ToUpperInvariant();
        if (a.Contains("14") || a.Contains("585")) return "14K";
        if (a.Contains("18") || a.Contains("750")) return "18K";
        if (a.Contains("22") || a.Contains("916")) return "22K";
        if (a.Contains("24") || a.Contains("999")) return "24K";
        if (a.EndsWith("K") && a.Length <= 4) return a;
        return a.Replace(" ", "");
    }

    /// <summary>WPF DepoStokSatirYukleyici.AyarGorunenMetin ile aynı.</summary>
    private static string AyarGorunenMetinForHavuz(string karatRaw)
    {
        var k = DepotAyarKeyForHavuz(karatRaw ?? "");
        return string.IsNullOrEmpty(k) ? (karatRaw ?? "").Trim() : k;
    }

    /// <summary>WPF MapHavuzToSatir milyem switch ile aynı (gram × milyem = HAS).</summary>
    private static decimal MilyemFromHavuzAyarRow(string? ayarRaw)
    {
        var ayar = AyarGorunenMetinForHavuz(ayarRaw ?? "");
        return ayar switch
        {
            "14K" => 585m / 1000m,
            "18K" => 750m / 1000m,
            "22K" => 916m / 1000m,
            "24K" => 1m,
            _ => 916m / 1000m
        };
    }

    /// <summary>StokDepoViewModel.NormalizeAyarKey (ziynet özeti) ile aynı.</summary>
    private static string NormalizeAyarKeyForZiynet(string? ayarRaw)
    {
        var a = (ayarRaw ?? "").Trim().ToUpperInvariant();
        if (a.Contains("14") || a.Contains("585")) return "14K";
        if (a.Contains("18") || a.Contains("750")) return "18K";
        if (a.Contains("22") || a.Contains("916")) return "22K";
        if (a.Contains("24") || a.Contains("999") || a.Contains("1000")) return "24K";
        return string.IsNullOrWhiteSpace(a) ? "DİĞER" : a;
    }

    /// <summary>StokDepoViewModel.ResolveMilyemFromAyarText ile aynı — ziynet HAS.</summary>
    private static decimal MilyemFromZiynetAyarText(string? ayarRaw)
    {
        var a = NormalizeAyarKeyForZiynet(ayarRaw);
        return a switch
        {
            "14K" => 585m / 1000m,
            "18K" => 750m / 1000m,
            "22K" => 916m / 1000m,
            "24K" => 1m,
            _ => 916m / 1000m
        };
    }

    private static decimal ResolveZiynetAlisUnitTl(
        string? productName,
        string? tipOrKarat,
        Func<string, decimal> ask,
        Func<string, decimal> bid)
    {
        static decimal Nz(decimal primary, decimal fallback = 0m) => primary > 0m ? primary : fallback;

        var aile = NormalizeZiynetAilesiToken(productName);
        if (string.IsNullOrWhiteSpace(aile))
            return 0m;

        if (aile is "GRAM" or "HAS")
            return Nz(bid("G24_TRY"), ask("G24_TRY"));

        // Cumhuriyet fiyatı bu akışta ATA kodu üzerinden izlenir.
        var baseCode = aile == "CUMHURIYET" ? "ATA" : aile;
        var tip = NormalizeZiynetTipToken(tipOrKarat);
        var code = $"{baseCode}_{tip}";
        return Nz(bid(code), ask(code));
    }

    private static string NormalizeZiynetTipToken(string? raw)
    {
        var text = (raw ?? "").Trim().ToUpperInvariant();
        if (text.Contains("ESKI") || text.Contains("ESKİ")) return "ESKI";
        return "YENI";
    }

    private static string NormalizeZiynetAilesiToken(string? raw)
    {
        var text = (raw ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Contains("ÇEYREK") || text.Contains("CEYREK")) return "CEYREK";
        if (text.Contains("YARIM")) return "YARIM";
        if (text.Contains("ATA5")) return "ATA5";
        if (text.Contains("GREMSE")) return "GREMSE";
        if (text.Contains("CUMHURIYET") || text.Contains("CUMHURİYET")) return "CUMHURIYET";
        if (text.Contains("ATA")) return "ATA";
        if (text == "TAM" || text.Contains("TAM")) return "TAM";
        if (text.Contains("GRAM")) return "GRAM";
        if (text.Contains("HAS")) return "HAS";
        return "";
    }

    private sealed class CashTxRow
    {
        public Guid Id { get; set; }
        public Guid BranchId { get; set; }
        public string TxType { get; set; } = "";
        public string SourceModule { get; set; } = "";
        public string Currency { get; set; } = "TL";
        public decimal Amount { get; set; }
        public DateTime TxDate { get; set; }
        public string? RefType { get; set; }
        public Guid? RefId { get; set; }
        public string? Description { get; set; }
        public string? AccountName { get; set; }
    }
}

