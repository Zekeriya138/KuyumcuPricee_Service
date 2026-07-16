using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using KUYUMCU.Price_Service.Persistence;
using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static KUYUMCU.Price_Service.Persistence.UserDisplayNames;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT zorunlu
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _svc;
    private readonly AppDbContext _db;
    private readonly TransactionReversalService _reversal;

    public CustomersController(ICustomerService svc, AppDbContext db, TransactionReversalService reversal)
    {
        _svc = svc;
        _db = db;
        _reversal = reversal;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] int? cariTip, CancellationToken ct)
        => Ok(await _svc.ListAsync(q, cariTip, ct));

    /// <summary>Müşteri listesi + özet finans (tek istek; N+1 önler). Son işlemler dahil değildir.</summary>
    [HttpGet("finance-bulk")]
    public async Task<IActionResult> FinanceBulk([FromQuery] int? cariTip, CancellationToken ct)
    {
        var customers = (await _svc.ListAsync(null, cariTip, ct)).ToList();
        if (customers.Count == 0)
            return Ok(Array.Empty<CustomerWithFinanceDto>());

        var tenantId = customers[0].TenantId;
        var branchId = customers[0].BranchId;

        IQueryable<Customer> branchCustomers = _db.Customers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted);
        if (cariTip.HasValue)
            branchCustomers = branchCustomers.Where(x => x.CariTip == cariTip.Value);

        // Contains(500 guid) → SQL Server OPENJSON/WITH hatası verebilir; JOIN kullan.
        var balanceMap = await (
            from b in _db.CustomerBalances.AsNoTracking()
            join c in branchCustomers on b.CustomerId equals c.Id
            where b.TenantId == tenantId && !b.IsDeleted
            select b
        ).ToDictionaryAsync(x => x.CustomerId, ct);

        var allTx = await (
            from t in _db.CustomerTransactions.AsNoTracking()
            join c in branchCustomers on t.CustomerId equals c.Id
            where t.TenantId == tenantId &&
                  t.BranchId == branchId &&
                  !t.IsDeleted &&
                  !t.IsReversed
            select t
        ).ToListAsync(ct);

        var txByCustomer = allTx
            .GroupBy(x => x.CustomerId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CustomerTransaction>)g.ToList());

        var result = customers.Select(c =>
        {
            balanceMap.TryGetValue(c.Id, out var bal);
            txByCustomer.TryGetValue(c.Id, out var txs);
            var core = BuildFinanceCore(bal, txs ?? Array.Empty<CustomerTransaction>());
            return new CustomerWithFinanceDto(
                c.Id,
                c.CariTip,
                c.FullName ?? "",
                c.NationalId,
                c.Phone,
                c.Email,
                c.City,
                c.District,
                c.Address,
                c.Note,
                c.TedarikciExtJson,
                core.Doviz,
                core.Ziynet,
                core.Iscilikli);
        }).ToList();

        return Ok(result);
    }

    /// <summary>Şube müşterilerinin son işlemleri (liste ekranı Son İşlemler sekmesi).</summary>
    [HttpGet("recent-transactions")]
    public async Task<IActionResult> RecentTransactions([FromQuery] int? cariTip, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var customers = (await _svc.ListAsync(null, cariTip, ct)).ToList();
        if (customers.Count == 0)
            return Ok(Array.Empty<CustomerRecentTxWithPartyDto>());

        var tenantId = customers[0].TenantId;
        var branchId = customers[0].BranchId;

        IQueryable<Customer> branchCustomers = _db.Customers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted);
        if (cariTip.HasValue)
            branchCustomers = branchCustomers.Where(x => x.CariTip == cariTip.Value);

        var joined = await (
            from t in _db.CustomerTransactions.AsNoTracking()
            join c in branchCustomers on t.CustomerId equals c.Id
            where t.TenantId == tenantId &&
                  t.BranchId == branchId &&
                  !t.IsDeleted &&
                  !t.IsReversed
            orderby t.TxDate descending, t.CreatedAt descending
            select new { Tx = t, c.Id, Name = c.FullName ?? "" }
        ).Take(limit * 4).ToListAsync(ct);

        var txList = joined.Select(x => x.Tx).ToList();
        var sonIslemKaynak = FilterDuplicateZiynetEmanetRecentRows(txList)
            .Where(x => !IsInternalZiynetAuditRow(x))
            .ToList();

        var partyByTxId = joined.ToDictionary(x => x.Tx.Id, x => (x.Id, x.Name));

        HashSet<Guid> blockedSaleIds;
        try
        {
            blockedSaleIds = await LoadBlockedSaleIdsAsync(tenantId, ct);
        }
        catch
        {
            blockedSaleIds = new HashSet<Guid>();
        }

        var partyIdsInScope = joined.Select(x => x.Id).Distinct().ToHashSet();
        List<CustomerTransaction> historyTx;
        if (partyIdsInScope.Count == 0)
        {
            historyTx = new List<CustomerTransaction>();
        }
        else
        {
            // SQL Server OPENJSON/CTE hatasını önlemek için Contains SQL'e gönderilmez.
            var branchHistoryTx = await _db.CustomerTransactions.AsNoTracking()
                .Where(t => t.TenantId == tenantId &&
                            t.BranchId == branchId &&
                            !t.IsDeleted &&
                            !t.IsReversed)
                .ToListAsync(ct);
            historyTx = branchHistoryTx.Where(t => partyIdsInScope.Contains(t.CustomerId)).ToList();
        }
        var sonMiktarByParty = partyIdsInScope.ToDictionary(
            pid => pid,
            pid => CariRecentTxBalanceHelper.BuildCustomerIndex(historyTx.Where(t => t.CustomerId == pid)));

        var result = sonIslemKaynak
            .OrderByDescending(x => x.TxDate)
            .ThenByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x =>
            {
                var mapped = MapCustomerRecentRow(x, blockedSaleIds);
                partyByTxId.TryGetValue(x.Id, out var party);
                var birim = "";
                var sonMiktar = "-";
                if (sonMiktarByParty.TryGetValue(party.Id, out var idx) &&
                    idx.TryGetValue(x.Id, out var snap))
                {
                    birim = snap.Birim;
                    sonMiktar = snap.SonMiktar;
                }

                return new CustomerRecentTxWithPartyDto(
                    party.Id,
                    party.Name,
                    mapped.TransactionId,
                    mapped.BatchId,
                    mapped.RefType,
                    mapped.RefId,
                    mapped.SourceKind,
                    mapped.CanReverse,
                    mapped.IsReversed,
                    mapped.IslemTarihi,
                    mapped.Grup,
                    mapped.Kalem,
                    mapped.Deger,
                    birim,
                    sonMiktar,
                    mapped.CariDurum,
                    mapped.Aciklama,
                    mapped.Kullanici);
            })
            .ToList();

        var reversedJoined = await (
            from r in _db.TransactionReversalLogs.AsNoTracking()
            join c in branchCustomers on r.PartyId equals c.Id
            where r.TenantId == tenantId &&
                  (r.BranchId == null || r.BranchId == branchId) &&
                  r.PartyType == "Customer" &&
                  !r.IsDeleted
            orderby r.ReversedAt descending
            select new { Log = r, c.Id, Name = c.FullName ?? "" }
        ).Take(limit).ToListAsync(ct);

        var reversedRows = reversedJoined.Select(x => new CustomerRecentTxWithPartyDto(
            x.Id,
            x.Name,
            null,
            x.Log.BatchId,
            x.Log.OperationType,
            null,
            "Reversal",
            false,
            true,
            x.Log.ReversedAt,
            x.Log.DisplayGrup,
            x.Log.DisplayKalem,
            x.Log.DisplayDeger,
            "",
            "-",
            "Geri Alındı",
            ResolveReversalAciklama(x.Log),
            x.Log.ReversedByUserName ?? "")).ToList();

        var merged = result.Concat(reversedRows)
            .OrderByDescending(x => x.IslemTarihi)
            .Take(limit)
            .ToList();

        return Ok(merged);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
        => (await _svc.GetAsync(id, ct)) is { } c ? Ok(c) : NotFound();

    public record CreateCustomerDto(
        string FullName,
        int CariTip,
        string? NationalId,
        DateTime? BirthDate,
        string? Phone,
        string? Email,
        string? City,
        string? District,
        string? Address,
        string? Note,
        string? TedarikciExtJson
    );

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomerDto dto, CancellationToken ct)
    {
        var ent = new Customer
        {
            FullName = dto.FullName,
            CariTip = dto.CariTip,
            NationalId = dto.NationalId,
            BirthDate = dto.BirthDate,
            Phone = dto.Phone,
            Email = dto.Email,
            City = dto.City,
            District = dto.District,
            Address = dto.Address,
            Note = dto.Note,
            TedarikciExtJson = dto.TedarikciExtJson
        };

        var created = await _svc.CreateAsync(ent, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CreateCustomerDto dto, CancellationToken ct)
    {
        var inp = new Customer
        {
            FullName = dto.FullName,
            CariTip = dto.CariTip,
            NationalId = dto.NationalId,
            BirthDate = dto.BirthDate,
            Phone = dto.Phone,
            Email = dto.Email,
            City = dto.City,
            District = dto.District,
            Address = dto.Address,
            Note = dto.Note,
            TedarikciExtJson = dto.TedarikciExtJson
        };
        return await _svc.UpdateAsync(id, inp, ct) ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
        => await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();

    public sealed record CustomerBalanceRowDto(
        decimal BalanceTL,
        decimal BalanceUSD,
        decimal BalanceEUR,
        decimal BalanceGBP,
        decimal BalanceHAS,
        decimal BalanceGUMUS,
        // Brüt borç/alacak: borçlar (Direction<0) ve alacaklar (Direction>=0) ayrı ayrı toplanır;
        // birbirini götürmeden ilgili sütunda gösterilir. Net bakiye = Alacak - Borç.
        decimal BorcTL = 0m,
        decimal AlacakTL = 0m,
        decimal BorcUSD = 0m,
        decimal AlacakUSD = 0m,
        decimal BorcEUR = 0m,
        decimal AlacakEUR = 0m,
        decimal BorcGBP = 0m,
        decimal AlacakGBP = 0m,
        decimal BorcHAS = 0m,
        decimal AlacakHAS = 0m,
        decimal BorcGUMUS = 0m,
        decimal AlacakGUMUS = 0m
    );

    public sealed record CustomerZiynetRowDto(
        string Ad,
        string Tip,
        decimal Adet,
        // Brüt borç/alacak adedi (birbirini götürmeden).
        decimal Borc = 0m,
        decimal Alacak = 0m
    );

    public sealed record CustomerIscilikliRowDto(
        Guid Id,
        string CariDurum,
        string UrunAdi,
        decimal Gramaj,
        string Ayar,
        decimal HasKarsiligi,
        decimal SatisFiyatiTl,
        DateTime IslemTarihi
    );

    public sealed record CustomerRecentTxRowDto(
        Guid? TransactionId,
        Guid? BatchId,
        string RefType,
        Guid? RefId,
        string SourceKind,
        bool CanReverse,
        bool IsReversed,
        DateTime IslemTarihi,
        string Grup,
        string Kalem,
        string Deger,
        string Birim,
        string SonMiktar,
        string CariDurum,
        string Aciklama,
        string Kullanici
    );

    public sealed record CustomerRecentTxWithPartyDto(
        Guid PartyId,
        string PartyName,
        Guid? TransactionId,
        Guid? BatchId,
        string RefType,
        Guid? RefId,
        string SourceKind,
        bool CanReverse,
        bool IsReversed,
        DateTime IslemTarihi,
        string Grup,
        string Kalem,
        string Deger,
        string Birim,
        string SonMiktar,
        string CariDurum,
        string Aciklama,
        string Kullanici
    );

    public sealed record CustomerFinanceDto(
        CustomerBalanceRowDto Doviz,
        List<CustomerZiynetRowDto> Ziynet,
        List<CustomerIscilikliRowDto> Iscilikli,
        List<CustomerRecentTxRowDto> SonIslemler
    );

    public sealed record CustomerWithFinanceDto(
        Guid Id,
        int CariTip,
        string FullName,
        string? NationalId,
        string? Phone,
        string? Email,
        string? City,
        string? District,
        string? Address,
        string? Note,
        string? TedarikciExtJson,
        CustomerBalanceRowDto Doviz,
        List<CustomerZiynetRowDto> Ziynet,
        List<CustomerIscilikliRowDto> Iscilikli
    );

    public sealed record CustomerFinanceCoreDto(
        CustomerBalanceRowDto Doviz,
        List<CustomerZiynetRowDto> Ziynet,
        List<CustomerIscilikliRowDto> Iscilikli
    );

    [HttpGet("{id:guid}/finance")]
    public async Task<IActionResult> Finance([FromRoute] Guid id, CancellationToken ct)
    {
        var customer = await _svc.GetAsync(id, ct);
        if (customer is null) return NotFound();

        var bal = await _db.CustomerBalances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomerId == id && x.TenantId == customer.TenantId && !x.IsDeleted, ct);
        var tx = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.CustomerId == id && x.TenantId == customer.TenantId && x.BranchId == customer.BranchId && !x.IsDeleted && !x.IsReversed)
            .OrderByDescending(x => x.TxDate)
            .ToListAsync(ct);

        var core = BuildFinanceCore(bal, tx);

        var sonIslemKaynak = FilterDuplicateZiynetEmanetRecentRows(tx)
            .Where(x => !IsInternalZiynetAuditRow(x))
            .ToList();
        HashSet<Guid> blockedSaleIds;
        try
        {
            blockedSaleIds = await LoadBlockedSaleIdsAsync(customer.TenantId, ct);
        }
        catch
        {
            blockedSaleIds = new HashSet<Guid>();
        }
        var sonMiktarIndex = CariRecentTxBalanceHelper.BuildCustomerIndex(tx);
        var sonIslemler = sonIslemKaynak
            .OrderByDescending(x => x.TxDate)
            .ThenByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => MapCustomerRecentRow(x, blockedSaleIds, sonMiktarIndex))
            .ToList();

        var existingAuditSaleIds = sonIslemKaynak
            .Where(x =>
                string.Equals((x.GroupCode ?? "").Trim(), "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.ItemName ?? "").Trim(), "SALE_EVENT", StringComparison.OrdinalIgnoreCase) &&
                x.RefId.HasValue)
            .Select(x => x.RefId!.Value)
            .ToHashSet();
        var existingAuditPurchaseIds = sonIslemKaynak
            .Where(x =>
                string.Equals((x.GroupCode ?? "").Trim(), "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.ItemName ?? "").Trim(), "PURCHASE_EVENT", StringComparison.OrdinalIgnoreCase) &&
                x.RefId.HasValue)
            .Select(x => x.RefId!.Value)
            .ToHashSet();

        var salesDocs = await _db.Sales.AsNoTracking()
            .Where(x =>
                x.TenantId == customer.TenantId &&
                x.BranchId == customer.BranchId &&
                x.CustomerId == id &&
                !x.IsDeleted)
            .Select(x => new { x.Id, x.CreatedAt, x.PaymentType, x.UserId })
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var purchaseDocs = await _db.Purchases.AsNoTracking()
            .Where(x =>
                x.TenantId == customer.TenantId &&
                x.BranchId == customer.BranchId &&
                x.CustomerId == id &&
                !x.IsDeleted)
            .Select(x => new { x.Id, x.Date, x.PaymentMethod, x.UserId })
            .OrderByDescending(x => x.Date)
            .Take(200)
            .ToListAsync(ct);

        var userIds = salesDocs.Select(x => x.UserId)
            .Concat(purchaseDocs.Select(x => x.UserId))
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        var userNames = await BuildUserNameMapAsync(_db, customer.TenantId, userIds, ct);

        foreach (var sale in salesDocs)
        {
            if (existingAuditSaleIds.Contains(sale.Id)) continue;
            sonIslemler.Add(new CustomerRecentTxRowDto(
                null, null, "SALE", sale.Id, "Sale",
                !blockedSaleIds.Contains(sale.Id),
                false,
                sale.CreatedAt,
                "AUDIT",
                "Satış İşlemi",
                "İşlem kaydı",
                "",
                "-",
                "İşlem",
                $"Satış belgesi (Ref: {sale.Id}, Ödeme: {sale.PaymentType ?? "-"})",
                userNames.TryGetValue(sale.UserId, out var sn) ? sn : ""));
        }
        foreach (var purchase in purchaseDocs)
        {
            if (existingAuditPurchaseIds.Contains(purchase.Id)) continue;
            sonIslemler.Add(new CustomerRecentTxRowDto(
                null, null, "PURCHASE", purchase.Id, "Purchase",
                true,
                false,
                purchase.Date,
                "AUDIT",
                "Alış İşlemi",
                "İşlem kaydı",
                "",
                "-",
                "İşlem",
                $"Alış belgesi (Ref: {purchase.Id}, Ödeme: {purchase.PaymentMethod})",
                userNames.TryGetValue(purchase.UserId, out var pn) ? pn : ""));
        }

        sonIslemler = sonIslemler
            .OrderByDescending(x => x.IslemTarihi)
            .Take(200)
            .ToList();

        return Ok(new CustomerFinanceDto(core.Doviz, core.Ziynet, core.Iscilikli, sonIslemler));
    }

    private static CustomerFinanceCoreDto BuildFinanceCore(CustomerBalance? bal, IReadOnlyList<CustomerTransaction> tx)
    {
        var gumusFromTx = tx
            .Where(x => string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase)
                        && string.Equals((x.ItemName ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);

        var misclassifiedHasAltinZiynetNet = tx
            .Where(x => string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals((x.ItemName ?? "").Trim(), "RESTORE", StringComparison.OrdinalIgnoreCase)
                        && CustomerFinanceHelper.IsHasAltinZiynetAd(NormalizeZiynetItemName(x.ItemName)))
            .Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);

        var balanceHas = bal?.BalanceHAS ?? 0m;
        var displayedHas = misclassifiedHasAltinZiynetNet != 0m && Math.Abs(balanceHas) < 0.0001m
            ? misclassifiedHasAltinZiynetNet
            : balanceHas;

        // Brüt borç (Direction<0) ve alacak (Direction>=0): SETTLE_* offset kayıtları karşı sütundan düşer.
        (decimal Borc, decimal Alacak) DovizGross(string unit)
        {
            var rows = tx.Where(x => string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase)
                                     && string.Equals((x.ItemName ?? "").Trim(), unit, StringComparison.OrdinalIgnoreCase));
            return CustomerFinanceHelper.ComputeGrossColumns(rows);
        }

        // Net bakiye (bakiye tablosu) ile işlemlerden gelen brüt fark uyuşmuyorsa (kayıtsız açılış vb.),
        // farkı ilgili sütuna yansıt ki her zaman: Alacak - Borç = Net olsun.
        static (decimal Borc, decimal Alacak) Reconcile(decimal borc, decimal alacak, decimal net, int decimals)
        {
            var residual = net - (alacak - borc);
            var eps = decimals >= 4 ? 0.00005m : 0.005m;
            if (residual > eps) alacak += residual;
            else if (residual < -eps) borc += -residual;
            return (decimal.Round(borc, decimals, MidpointRounding.AwayFromZero),
                    decimal.Round(alacak, decimals, MidpointRounding.AwayFromZero));
        }

        // HAS için hem DOVIZ/HAS hem de (eski/yanlış sınıflanmış) ZIYNET has altın kayıtları dahil.
        var hasRows = tx.Where(x =>
            (string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase)
                && string.Equals((x.ItemName ?? "").Trim(), "HAS", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase)
                && !string.Equals((x.ItemName ?? "").Trim(), "RESTORE", StringComparison.OrdinalIgnoreCase)
                && CustomerFinanceHelper.IsHasAltinZiynetAd(NormalizeZiynetItemName(x.ItemName)))).ToList();
        var hasGross = CustomerFinanceHelper.ComputeGrossColumns(hasRows);
        var hasBorcRaw = hasGross.Borc;
        var hasAlacakRaw = hasGross.Alacak;

        var grossTl = DovizGross("TL");
        var grossUsd = DovizGross("USD");
        var grossEur = DovizGross("EUR");
        var grossGbp = DovizGross("GBP");
        var grossGumus = DovizGross("GUMUS");

        var (borcTl, alacakTl) = Reconcile(grossTl.Borc, grossTl.Alacak, bal?.BalanceTL ?? 0m, 2);
        var (borcUsd, alacakUsd) = Reconcile(grossUsd.Borc, grossUsd.Alacak, bal?.BalanceUSD ?? 0m, 4);
        var (borcEur, alacakEur) = Reconcile(grossEur.Borc, grossEur.Alacak, bal?.BalanceEUR ?? 0m, 4);
        var (borcGbp, alacakGbp) = Reconcile(grossGbp.Borc, grossGbp.Alacak, bal?.BalanceGBP ?? 0m, 4);
        var (borcHas, alacakHas) = Reconcile(hasBorcRaw, hasAlacakRaw, displayedHas, 4);
        var (borcGumus, alacakGumus) = Reconcile(grossGumus.Borc, grossGumus.Alacak, gumusFromTx, 4);

        var doviz = new CustomerBalanceRowDto(
            bal?.BalanceTL ?? 0m,
            bal?.BalanceUSD ?? 0m,
            bal?.BalanceEUR ?? 0m,
            bal?.BalanceGBP ?? 0m,
            decimal.Round(displayedHas, 4, MidpointRounding.AwayFromZero),
            decimal.Round(gumusFromTx, 4, MidpointRounding.AwayFromZero),
            BorcTL: borcTl, AlacakTL: alacakTl,
            BorcUSD: borcUsd, AlacakUSD: alacakUsd,
            BorcEUR: borcEur, AlacakEUR: alacakEur,
            BorcGBP: borcGbp, AlacakGBP: alacakGbp,
            BorcHAS: borcHas, AlacakHAS: alacakHas,
            BorcGUMUS: borcGumus, AlacakGUMUS: alacakGumus
        );

        var ziynet = tx
            .Where(x => string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals((x.ItemName ?? "").Trim(), "RESTORE", StringComparison.OrdinalIgnoreCase)
                        && !CustomerFinanceHelper.IsHasAltinZiynetAd(NormalizeZiynetItemName(x.ItemName)))
            .GroupBy(x => new
            {
                ItemName = NormalizeZiynetItemName(x.ItemName),
                Tip = NormalizeZiynetTipGroupingKey(NormalizeZiynetItemName(x.ItemName), x.ItemType)
            })
            .Select(g =>
            {
                var gross = CustomerFinanceHelper.ComputeGrossColumns(g);
                var borc = decimal.Round(gross.Borc, 3, MidpointRounding.AwayFromZero);
                var alacak = decimal.Round(gross.Alacak, 3, MidpointRounding.AwayFromZero);
                return new CustomerZiynetRowDto(
                    g.Key.ItemName,
                    g.Key.Tip,
                    alacak - borc,
                    borc,
                    alacak
                );
            })
            .Where(x => x.Borc != 0m || x.Alacak != 0m)
            .OrderBy(x => x.Ad)
            .ThenBy(x => x.Tip)
            .ToList();

        var iscilikli = tx
            .Where(x => x.GroupCode == "ISCILIKLI")
            .OrderByDescending(x => x.TxDate)
            .Select(x => new CustomerIscilikliRowDto(
                x.Id,
                ResolveIscilikliCariDurum(x),
                x.ItemName,
                Math.Abs(x.Gram ?? 0m),
                x.Ayar ?? "",
                Math.Abs(x.HasEquivalent ?? 0m),
                Math.Abs(x.TotalPriceTl ?? 0m),
                x.TxDate
            ))
            .ToList();

        return new CustomerFinanceCoreDto(doviz, ziynet, iscilikli);
    }

    [HttpGet("{id:guid}/reversed-transactions")]
    public async Task<IActionResult> ReversedTransactions([FromRoute] Guid id, CancellationToken ct)
    {
        var customer = await _svc.GetAsync(id, ct);
        if (customer is null) return NotFound();
        var rows = await _reversal.GetCustomerReversedAsync(customer.TenantId, id, ct);
        return Ok(rows);
    }

    private static string ResolveRecentAciklama(CustomerTransaction x)
    {
        var note = x.Note ?? "";
        if (string.Equals(x.RefType, "TRANSFER", StringComparison.OrdinalIgnoreCase) ||
            note.Contains(CariTransferMarker.Prefix, StringComparison.OrdinalIgnoreCase))
            return CariTransferMarker.FormatDisplayNote(note);
        return note;
    }

    private static string ResolveReversalAciklama(TransactionReversalLog log)
    {
        var aciklama = (log.DisplayAciklama ?? "").Trim();
        var reason = (log.Reason ?? "").Trim();

        if (string.Equals(log.OperationType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
        {
            var transferText = CariTransferMarker.FormatDisplayNote(aciklama);
            if (!string.IsNullOrWhiteSpace(transferText) && transferText != "Transfer")
                return string.IsNullOrWhiteSpace(reason) ? $"{transferText} (Geri alındı)" : $"{transferText} — {reason}";
        }

        if (!string.IsNullOrWhiteSpace(aciklama) && !string.IsNullOrWhiteSpace(reason))
            return $"{aciklama} — {reason}";
        return !string.IsNullOrWhiteSpace(reason) ? reason : aciklama;
    }

    private static CustomerRecentTxRowDto MapCustomerRecentRow(
        CustomerTransaction x,
        HashSet<Guid> blockedSaleIds,
        IReadOnlyDictionary<Guid, CariSonMiktarSnapshot>? sonMiktarIndex = null)
    {
        var refType = (x.RefType ?? "").Trim().ToUpperInvariant();
        var sourceKind = "Transaction";
        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ItemName, "SALE_EVENT", StringComparison.OrdinalIgnoreCase))
            sourceKind = "Sale";
        else if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(x.ItemName, "PURCHASE_EVENT", StringComparison.OrdinalIgnoreCase))
            sourceKind = "Purchase";
        else if (refType == "SALE") sourceKind = "Sale";
        else if (refType == "PURCHASE") sourceKind = "Purchase";

        var canReverse = !x.IsReversed;
        if (canReverse && (sourceKind == "Sale" || refType == "SALE") && x.RefId.HasValue)
            canReverse = !blockedSaleIds.Contains(x.RefId.Value);
        if (canReverse && string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(x.ItemName, "SALE_EVENT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(x.ItemName, "PURCHASE_EVENT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(x.ItemName, "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(x.ItemName, "ZIYNET_URUN_STOK", StringComparison.OrdinalIgnoreCase))
            canReverse = false;

        var birim = "";
        var sonMiktar = "-";
        if (sonMiktarIndex != null &&
            sonMiktarIndex.TryGetValue(x.Id, out var snap))
        {
            birim = snap.Birim;
            sonMiktar = snap.SonMiktar;
        }

        return new CustomerRecentTxRowDto(
            x.Id,
            x.BatchId,
            refType.Length > 0 ? refType : (x.GroupCode ?? ""),
            x.RefId,
            sourceKind,
            canReverse,
            x.IsReversed,
            x.TxDate,
            x.GroupCode ?? "",
            ResolveRecentKalem(x),
            FormatRecentValue(x),
            birim,
            sonMiktar,
            ResolveRecentCariDurum(x),
            ResolveRecentAciklama(x),
            x.KullaniciAdi ?? "");
    }

    private async Task<HashSet<Guid>> LoadBlockedSaleIdsAsync(Guid tenantId, CancellationToken ct)
    {
        var invoiceSaleIds = await _db.Invoices.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SaleId.HasValue)
            .Select(x => x.SaleId!.Value)
            .ToListAsync(ct);
        if (invoiceSaleIds.Count == 0) return new HashSet<Guid>();

        var blocked = new HashSet<Guid>();
        var docs = await _db.EInvoiceDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Join(_db.Invoices.AsNoTracking().Where(i => i.TenantId == tenantId && i.SaleId.HasValue && invoiceSaleIds.Contains(i.SaleId!.Value)),
                d => d.InvoiceId, i => i.Id, (d, i) => new { i.SaleId, d.DeliveredAt, d.Status, d.SubmittedAt })
            .ToListAsync(ct);
        foreach (var row in docs)
        {
            if (!row.SaleId.HasValue) continue;
            if (row.DeliveredAt.HasValue ||
                (!string.Equals(row.Status, "Draft", StringComparison.OrdinalIgnoreCase) && row.SubmittedAt.HasValue))
                blocked.Add(row.SaleId.Value);
        }
        return blocked;
    }

    private static bool IsInternalZiynetAuditRow(CustomerTransaction x)
        => string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
           string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_URUN_STOK", StringComparison.OrdinalIgnoreCase);

    private static string FormatRecentValue(CustomerTransaction x)
    {
        var sign = x.Direction >= 0 ? "+" : "-";

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
            return $"{sign}{Math.Abs(x.Quantity):N3} adet";

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase))
        {
            var tl = Math.Abs(x.TotalPriceTl ?? x.Quantity);
            return tl > 0m ? $"{tl:N2} TL" : "İşlem kaydı";
        }
        if (string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase))
            return $"{sign}{Math.Abs(x.Quantity):N4} {x.ItemName}";

        if (string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            return $"{sign}{Math.Abs(x.Quantity):N3} adet";

        if (string.Equals(x.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase))
        {
            var gram = x.Gram ?? 0m;
            var has = x.HasEquivalent ?? 0m;
            var tl = x.TotalPriceTl ?? 0m;
            return $"{sign}{Math.Abs(gram):N3} gr | {Math.Abs(has):N6} has | {Math.Abs(tl):N2} TL";
        }

        return $"{sign}{Math.Abs(x.Quantity):N4}";
    }

    private static string ResolveRecentKalem(CustomerTransaction x)
    {
        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase))
        {
            var item = (x.ItemName ?? "").Trim().ToUpperInvariant();
            if (item == "SALE_EVENT") return "Satış İşlemi";
            if (item == "PURCHASE_EVENT") return "Alış İşlemi";
        }

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
        {
            var raw = (x.ItemType ?? "").Trim();
            if (raw.Contains('|', StringComparison.Ordinal))
            {
                var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                    return $"{parts[0]} / {parts[1]}";
                if (parts.Length == 1)
                    return parts[0];
            }

            var note = x.Note ?? "";
            var marker = "Ziynet düşüm:";
            var idx = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var tail = note[(idx + marker.Length)..].Trim();
                var pipeIdx = tail.IndexOf('|');
                var detail = pipeIdx >= 0 ? tail[..pipeIdx].Trim() : tail;
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    var openParen = detail.IndexOf('(');
                    if (openParen > 0 && detail.Contains(')'))
                    {
                        var ad = detail[..openParen].Trim();
                        var tip = detail[(openParen + 1)..].Replace(")", "").Trim();
                        return string.IsNullOrWhiteSpace(tip) ? ad : $"{ad} / {tip}";
                    }
                    return detail;
                }
            }

            return "Ziynet";
        }

        return string.IsNullOrWhiteSpace(x.ItemType) ? (x.ItemName ?? "") : $"{x.ItemName} ({x.ItemType})";
    }

    private static string ResolveRecentCariDurum(CustomerTransaction x)
    {
        var refType = (x.RefType ?? "").Trim().ToUpperInvariant();
        var mevcut = (x.CariDurum ?? "").Trim();
        var isEmanet = IsEmanetKaydi(x);

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_URUN_STOK", StringComparison.OrdinalIgnoreCase))
            return "İşlem";

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
        {
            if (refType is "PAYMENT" or "ODEME")
                return "Ödeme";
            if (refType is "COLLECTION" or "TAHSILAT")
                return "Tahsilat";
            return x.Direction >= 0 ? "Tahsilat" : "Ödeme";
        }

        if (isEmanet)
            return x.Direction >= 0 ? "Emanet (Alacaklı)" : "Emanet (Borçlu)";

        // MusteriIslemWindow (manuel) akışında eylem metni göster.
        if (refType == "OPENING_BALANCE")
            return "Açılış Bakiye Girişi";
        if (refType == "BALANCE_CONVERSION")
            return "Bakiye Dönüştürme";
        if (refType == "TRANSFER")
            return "Transfer";
        if (refType is "MANUAL" or "MANUAL_SETTLE")
            return x.Direction >= 0 ? "Tahsilat" : "Ödeme";

        if (string.Equals(mevcut, "Tahsilat", StringComparison.OrdinalIgnoreCase))
            return "Tahsilat";
        if (string.Equals(mevcut, "Odeme", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mevcut, "Ödeme", StringComparison.OrdinalIgnoreCase))
            return "Ödeme";
        if (string.Equals(mevcut, "Alacakli", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mevcut, "Alacaklı", StringComparison.OrdinalIgnoreCase))
            return "Alacaklı";
        if (string.Equals(mevcut, "Borclu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mevcut, "Borçlu", StringComparison.OrdinalIgnoreCase))
            return "Borçlu";

        // Satış/alış veresiye gibi finans hareketlerinde borç-alacak dili korunur.
        if (string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase) &&
            (refType == "SALE" || refType == "PURCHASE"))
            return x.Direction >= 0 ? "Alacaklı" : "Borçlu";

        return string.IsNullOrWhiteSpace(mevcut) ? "Finans" : mevcut;
    }

    private static string ResolveIscilikliCariDurum(CustomerTransaction x)
    {
        if (IsEmanetKaydi(x))
            return x.Direction >= 0 ? "Emanet (Alacaklı)" : "Emanet (Borçlu)";

        if (string.IsNullOrWhiteSpace(x.CariDurum))
            return x.Direction >= 0 ? "Alacakli" : "Borclu";

        return x.CariDurum!;
    }

    private static string NormalizeZiynetTipDisplay(string? raw)
    {
        var txt = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt)) return "Yeni";
        if (string.Equals(txt, "yeni", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(txt, "new", StringComparison.OrdinalIgnoreCase))
            return "Yeni";
        if (string.Equals(txt, "eski", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(txt, "old", StringComparison.OrdinalIgnoreCase))
            return "Eski";
        return txt;
    }

    private static string NormalizeZiynetItemName(string? raw)
    {
        var value = (raw ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Contains("KÜLÇE") || value.Contains("KULCE"))
            return "GRAM ALTIN(KÜLÇE)";
        if ((value.Contains("22 AYAR") || value.Contains("22AYAR")) &&
            (value.Contains("GR") || value.Contains("GRAM")))
            return "22 AYAR(GR)";
        if (value == "GRAM" || value.Contains("GRAM ALTIN (HAS)") || value.Contains("GRAM ALTIN(HAS)"))
            return "GRAM ALTIN(KÜLÇE)";
        return value;
    }

    private static string NormalizeZiynetTipGroupingKey(string normalizedItemName, string? rawTip)
    {
        var item = (normalizedItemName ?? "").Trim().ToUpperInvariant();
        if (item == "GRAM ALTIN(KÜLÇE)" || item == "GRAM ALTIN(KULCE)")
            return "Yeni";
        return NormalizeZiynetTipDisplay(rawTip);
    }

    private static bool IsEmanetKaydi(CustomerTransaction x)
    {
        // "Emanet" kavramı kaldırıldı. Borç/alacak dili kullanılır.
        // Bu yardımcı yalnızca eski kayıtların tekilleştirme mantığı için korunur ve
        // hiçbir satırı artık emanet olarak etiketlemez.
        return false;
    }

    private static List<CustomerTransaction> FilterDuplicateZiynetEmanetRecentRows(List<CustomerTransaction> rows)
    {
        if (rows.Count == 0) return rows;

        static bool IsZiynetEmanet(CustomerTransaction x) =>
            string.Equals((x.GroupCode ?? "").Trim(), "ZIYNET", StringComparison.OrdinalIgnoreCase)
            && string.Equals((x.RefType ?? "").Trim(), "SALE", StringComparison.OrdinalIgnoreCase)
            && IsEmanetKaydi(x);

        static string Nz(string? value, string fallback = "")
            => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToUpperInvariant();

        var processRows = rows
            .Where(x =>
                IsZiynetEmanet(x)
                && !x.RefId.HasValue
                && (x.Note ?? "").Contains("Ziynet satış emanet kaydı", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (processRows.Count == 0) return rows;

        var filtered = new List<CustomerTransaction>(rows.Count);
        foreach (var row in rows)
        {
            if (!IsZiynetEmanet(row) || !row.RefId.HasValue)
            {
                filtered.Add(row);
                continue;
            }

            var hasProcessTwin = processRows.Any(p =>
                Nz(p.ItemName) == Nz(row.ItemName)
                && Nz(p.ItemType, "YENI") == Nz(row.ItemType, "YENI")
                && p.Direction == row.Direction
                && Math.Abs(Math.Abs(p.Quantity) - Math.Abs(row.Quantity)) <= 0.0001m
                && Math.Abs((p.TxDate - row.TxDate).TotalMinutes) <= 10d);

            if (!hasProcessTwin)
                filtered.Add(row);
        }

        return filtered;
    }
}
