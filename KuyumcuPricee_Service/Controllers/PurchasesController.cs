using System.Security.Claims;
using System.Globalization;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using KUYUMCU.Price_Service.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using kuyumcu_domain.Enums; // StockRefKind, ItemKind
using System.Linq; // LINQ için eklendi
using KUYUMCU.Price_Service.Services;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT zorunlu
public class PurchasesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStockService _stock;
    private readonly IScrapService _scrap;
    private readonly IFinanceService _finance;
    private readonly IAccountingJournalService _accounting;

    public PurchasesController(
        AppDbContext db,
        IStockService stock,
        IScrapService scrap,
        IFinanceService finance,
        IAccountingJournalService accounting)
    {
        _db = db;
        _stock = stock;
        _scrap = scrap;
        _finance = finance;
        _accounting = accounting;
    }

    // ---------------- Tenant helper ----------------
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

    // ================== DTO'lar ==================
    // Create
    public sealed record CreatePurchaseItemReq(
        int? LineNo,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Quantity,
        decimal UnitCost,
        decimal Discount,
        decimal TaxRate,
        decimal? BirimIscilikHas = null,
        decimal? OdenecekToplamHas = null
    );

    public sealed record CreatePurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        Guid? SupplierId,
        PurchaseType? AlisTipi,
        PurchasePaymentMethod? PaymentMethod,
        string? PaymentUnit,
        decimal? PaymentUnitAmount,
        decimal? TotalHas,
        DateTime? Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        List<CreatePurchaseItemReq> Items
    );

    // Update
    public sealed record UpdatePurchaseItemReq(
        int? LineNo,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Quantity,
        decimal UnitCost,
        decimal Discount,
        decimal TaxRate,
        decimal? BirimIscilikHas = null,
        decimal? OdenecekToplamHas = null
    );

    public sealed record UpdatePurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        DateTime? Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        List<UpdatePurchaseItemReq> Items
    );

    // Response
    public sealed record PurchaseItemDto(
        Guid Id,
        Guid PurchaseId,
        int LineNo,
        ItemKind Kind,
        string ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Quantity,
        decimal UnitCost,
        decimal Discount,
        decimal TaxRate,
        decimal LineTotal,
        decimal? BirimIscilikHas,
        decimal? OdenecekToplamHas
    );

    public sealed record PurchaseDto(
        Guid Id,
        Guid BranchId,
        Guid UserId,
        Guid? CustomerId,
        Guid? SupplierId,
        int PurchaseType,
        int PaymentMethod,
        decimal? TotalHas,
        DateTime Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        decimal Subtotal,
        decimal DiscountTotal,
        decimal TaxTotal,
        decimal GrandTotal,
        decimal TotalAmount,
        List<PurchaseItemDto> Items,
        string? Kullanici = null
    );

    public sealed record PurchasePaymentDto(
        int PaymentType,
        decimal Amount,
        string? UnitCode = null,
        decimal? UnitAmount = null,
        decimal? GoldWeight = null,
        decimal? SilverWeight = null,
        string? GoldKarat = null,
        decimal? GoldPrice = null,
        string? BankName = null,
        string? IBAN = null,
        DateTime? DueDate = null,
        string? CashAccount = null
    );

    /// <summary>GET purchases/{id}/payments yanıt satırı.</summary>
    public sealed record PurchasePaymentRowDto(
        Guid Id,
        int PaymentType,
        decimal Amount,
        string? UnitCode,
        decimal? UnitAmount,
        decimal? GoldWeight,
        decimal? SilverWeight,
        string? GoldKarat,
        decimal? GoldPrice,
        string? BankName,
        string? IBAN,
        DateTime? DueDate,
        string? CashAccount,
        DateTime CreatedAt);

    /// <summary>Tek işlemde alış + ödeme satırları + tedarikçi/depo (hurda çıkış).</summary>
    public sealed record CompletePurchaseReq(
        CreatePurchaseReq Purchase,
        List<PurchasePaymentDto> Payments
    );

    // ================== CREATE ==================
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePurchaseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        var (purchase, err) = await TryBuildPurchaseAsync(req, tenantId, userId, ct);
        if (err != null) return err;
        var isToptanci = (req.AlisTipi ?? PurchaseType.Musteri) == PurchaseType.Toptanci;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.Purchases.Add(purchase!);
            await _db.SaveChangesAsync(ct);
            var headerPayments = BuildHeaderPayments(purchase!, req);
            if (headerPayments.Count > 0)
                await _finance.ApplyPurchasePaymentsAsync(tenantId, purchase!.BranchId, purchase.Id, headerPayments, ct);
            await _accounting.RecordPurchaseAsync(purchase!, headerPayments, ct);
            if (purchase!.Items.Any(i => i.Kind == ItemKind.Scrap))
                await _scrap.AddFromPurchaseItemsAsync(tenantId, purchase, ct);
            await ApplyPurchaseStockAsync(purchase!, isToptanci, tenantId, ct);
            await ApplyCustomerFinanceForPurchaseAsync(purchase!, isToptanci, tenantId, req.PaymentUnit, req.PaymentUnitAmount, ct);
            await AddPurchaseRecentAuditRowsAsync(purchase!, tenantId, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return CreatedAtAction(nameof(GetById), new { id = purchase!.Id }, ToDto(purchase));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Alışı karma ödemeyle atomik kaydeder: Purchase + kalemler + PurchasePayments, tedarikçi CurrentDebt (SQL),
    /// toptancı depo stoku (DepoStok), hurda ödemesinde depodan gram düşümü.
    /// Nakit/banka satırları PurchasePayment kaydı ile izlenir; ayrı kasa/banka defteri tablosu yoksa bu satırlar muhasebe referansıdır.
    /// </summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] CompletePurchaseReq body, CancellationToken ct)
    {
        if (body.Purchase is null)
            return BadRequest(new { error = "Purchase zorunludur." });
        if (body.Payments is null || body.Payments.Count == 0)
            return BadRequest(new { error = "En az bir ödeme satırı girilmelidir." });

        var tenantId = GetTenantId();
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        var (purchase, err) = await TryBuildPurchaseAsync(body.Purchase, tenantId, userId, ct);
        if (err != null) return err;

        var normalized = new List<PurchasePayment>();
        decimal sum = 0m;
        foreach (var p in body.Payments)
        {
            if (!Enum.IsDefined(typeof(PurchasePaymentType), p.PaymentType))
                return BadRequest(new { error = $"Geçersiz PaymentType: {p.PaymentType}" });
            var pt = (PurchasePaymentType)p.PaymentType;
            decimal amt = p.Amount;
            if (pt == PurchasePaymentType.Gold)
            {
                var w = p.GoldWeight ?? 0m;
                var price = p.GoldPrice ?? 0m;
                amt = Math.Round(w * price, 2);
                if (w <= 0 || price <= 0)
                    return BadRequest(new { error = "Hurda ödemesinde gram ve birim fiyat zorunludur." });
            }
            else
            {
                if (amt < 0)
                    return BadRequest(new { error = "Ödeme tutarı negatif olamaz." });
            }

            if (pt == PurchasePaymentType.Credit && !p.DueDate.HasValue)
                return BadRequest(new { error = "Veresiye için vade tarihi girin." });

            normalized.Add(new PurchasePayment
            {
                TenantId = tenantId,
                PurchaseId = Guid.Empty,
                PaymentType = pt,
                Amount = amt,
                UnitCode = NormalizeUnitCode(p.UnitCode),
                UnitAmount = p.UnitAmount,
                GoldWeight = pt == PurchasePaymentType.Gold ? p.GoldWeight : null,
                SilverWeight = p.SilverWeight,
                GoldKarat = pt == PurchasePaymentType.Gold ? p.GoldKarat?.Trim() : null,
                GoldPrice = pt == PurchasePaymentType.Gold ? p.GoldPrice : null,
                BankName = string.IsNullOrWhiteSpace(p.BankName) ? null : p.BankName.Trim(),
                IBAN = string.IsNullOrWhiteSpace(p.IBAN) ? null : p.IBAN.Trim(),
                DueDate = p.DueDate?.ToUniversalTime(),
                CashAccount = string.IsNullOrWhiteSpace(p.CashAccount) ? null : p.CashAccount.Trim()
            });
            sum += amt;
        }

        if (Math.Abs(sum - purchase!.GrandTotal) > 0.05m)
            return BadRequest(new { error = "Ödeme toplamı, alış tutarına eşit olmalıdır.", paid = sum, grandTotal = purchase.GrandTotal });

        var isToptanci = (body.Purchase.AlisTipi ?? PurchaseType.Musteri) == PurchaseType.Toptanci;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            _db.Purchases.Add(purchase);
            await _db.SaveChangesAsync(ct);

            await ApplyPurchaseStockAsync(purchase, isToptanci, tenantId, ct);
            await ApplyCustomerFinanceForPurchaseAsync(
                purchase,
                isToptanci,
                tenantId,
                body.Purchase.PaymentUnit,
                body.Purchase.PaymentUnitAmount,
                ct);
            await AddPurchaseRecentAuditRowsAsync(purchase, tenantId, ct);

            // Ödemeleri doğrudan DbSet'e ekleme: purchase.Payments.Add() bazen Purchase'ı Modified yapıp
            // ikinci SaveChanges'te gereksiz UPDATE üretir; 0 satır etkilenince EF Core concurrency hatası verir.
            foreach (var pay in normalized)
            {
                pay.PurchaseId = purchase.Id;
                _db.PurchasePayments.Add(pay);
            }
            await _finance.ApplyPurchasePaymentsAsync(tenantId, purchase.BranchId, purchase.Id, normalized, ct);
            await _accounting.RecordPurchaseAsync(purchase, normalized, ct);

            await _finance.ApplyForexPurchaseVaultAsync(
                tenantId,
                purchase.BranchId,
                purchase.Id,
                purchase.Items.ToList(),
                ct);

            // Tedarikçi borcunu doğrudan SQL ile güncelle (EF tracked UPDATE bazen 0 satır / concurrency hatası verebiliyor)
            if (purchase.SupplierId.HasValue)
            {
                var paidNonCredit = normalized
                    .Where(p => p.PaymentType != PurchasePaymentType.Credit)
                    .Sum(p => p.Amount);
                var netDebtDelta = purchase.GrandTotal - paidNonCredit;
                var sid = purchase.SupplierId.Value;
                var now = DateTime.UtcNow;
                var rows = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $@"UPDATE Suppliers
                       SET CurrentDebt = CurrentDebt + {netDebtDelta},
                           UpdatedAt = {now}
                       WHERE Id = {sid} AND TenantId = {tenantId} AND IsDeleted = CAST(0 AS bit)",
                    ct);
                if (rows != 1)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new
                    {
                        error = "Tedarikçi cari borcu güncellenemedi (kayıt bulunamadı veya silinmiş olabilir).",
                        supplierId = sid
                    });
                }

                var supplierBalance = await _db.SupplierBalances
                    .FirstOrDefaultAsync(x => x.SupplierId == sid && x.TenantId == tenantId && !x.IsDeleted, ct);
                if (supplierBalance is null)
                {
                    supplierBalance = new SupplierBalance
                    {
                        TenantId = tenantId,
                        SupplierId = sid
                    };
                    _db.SupplierBalances.Add(supplierBalance);
                }

                foreach (var credit in normalized.Where(x => x.PaymentType == PurchasePaymentType.Credit))
                {
                    var unit = NormalizeUnitCode(credit.UnitCode);
                    var unitAmount = credit.UnitAmount ?? credit.Amount;
                    if (unitAmount <= 0) continue;
                    switch (unit)
                    {
                        case "USD":
                            supplierBalance.BalanceUSD += unitAmount;
                            break;
                        case "EUR":
                            supplierBalance.BalanceEUR += unitAmount;
                            break;
                        case "GBP":
                            supplierBalance.BalanceGBP += unitAmount;
                            break;
                        case "HAS":
                            supplierBalance.BalanceHAS += unitAmount;
                            break;
                        case "GUMUS":
                            supplierBalance.BalanceGUMUS += unitAmount;
                            break;
                        default:
                            supplierBalance.BalanceTL += unitAmount;
                            break;
                    }

                    // Tedarikçi finans "Son İşlemler" için hareket kaydı.
                    _db.SupplierTransactions.Add(new SupplierTransaction
                    {
                        TenantId = tenantId,
                        SupplierId = sid,
                        BranchId = purchase.BranchId,
                        TxType = "COLLECTION",
                        SourceUnit = unit,
                        SourceAmount = decimal.Round(unitAmount, 6, MidpointRounding.AwayFromZero),
                        TargetUnit = unit,
                        TargetAmount = decimal.Round(unitAmount, 6, MidpointRounding.AwayFromZero),
                        IsConverted = false,
                        SourceUnitTlRate = 1m,
                        TargetUnitTlRate = 1m,
                        Description = $"Veresiye alacak kaydı - alış (PurchaseId: {purchase.Id}, Birim: {unit}, Tutar: {unitAmount:0.####})",
                        TxDate = purchase.Date
                    });
                }

                var onlyCreditSelected = normalized.Count > 0 && normalized.All(x => x.PaymentType == PurchasePaymentType.Credit);
                var ziynetItems = purchase.Items
                    .Where(i => i.Kind == ItemKind.Ziynet && i.Quantity > 0)
                    .ToList();
                if (onlyCreditSelected && ziynetItems.Count > 0)
                {
                    foreach (var item in ziynetItems)
                    {
                        var adet = decimal.Round(item.Quantity, 3, MidpointRounding.AwayFromZero);
                        if (adet <= 0m) continue;

                        // Has altın ziynet kalemi: adet defterine değil, HAS döviz bakiyesine yaz.
                        if (IsGramAltinHasZiynet(item))
                        {
                            supplierBalance.BalanceHAS += adet;
                            _db.SupplierTransactions.Add(new SupplierTransaction
                            {
                                TenantId = tenantId,
                                SupplierId = sid,
                                BranchId = purchase.BranchId,
                                TxType = "COLLECTION",
                                SourceUnit = "HAS",
                                SourceAmount = adet,
                                TargetUnit = "HAS",
                                TargetAmount = adet,
                                IsConverted = false,
                                SourceUnitTlRate = 1m,
                                TargetUnitTlRate = 1m,
                                Description = $"Veresiye alacak kaydı - ziynet has altın (PurchaseId: {purchase.Id}, Tutar: {adet:0.####} gr)",
                                TxDate = purchase.Date
                            });
                            continue;
                        }

                        var ad = NormalizeZiynetAd(item.ProductName, item.Category, item.ProductCode);
                        var tip = NormalizeZiynetFinanceTip(ad, "Yeni");

                        _db.SupplierTransactions.Add(new SupplierTransaction
                        {
                            TenantId = tenantId,
                            SupplierId = sid,
                            BranchId = purchase.BranchId,
                            TxType = "ZIYNET",
                            SourceUnit = "ADET",
                            SourceAmount = adet,
                            TargetUnit = "ADET",
                            TargetAmount = adet,
                            IsConverted = false,
                            SourceUnitTlRate = 1m,
                            TargetUnitTlRate = 1m,
                            Description = BuildSupplierZiynetDescription(ad, tip, adet, $"PURCHASE:{purchase.Id}"),
                            TxDate = purchase.Date
                        });
                    }
                }
                supplierBalance.UpdatedAt = DateTime.UtcNow;
            }

            // Müşteri veresiye finans hareketleri.
            if (purchase.CustomerId.HasValue && !purchase.SupplierId.HasValue)
            {
                var custId = purchase.CustomerId.Value;
                var creditRows = normalized.Where(x => x.PaymentType == PurchasePaymentType.Credit).ToList();
                if (creditRows.Count > 0)
                {
                    var custBal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, custId, ct);
                    var onlyCreditSelected = normalized.All(x => x.PaymentType == PurchasePaymentType.Credit);
                    var ziynetItems = purchase.Items
                        .Where(i => i.Kind == ItemKind.Ziynet && i.Quantity > 0)
                        .ToList();

                    // Ziynet alışında yalnız veresiye seçildiyse:
                    // - Gram Altın (Has) satırları HAS döviz bakiyesine yazılır.
                    // - Diğer ziynet satırları ZIYNET paneline Ad/Tip/Adet olarak yazılır.
                    // Bu modda genel DOVIZ kredi satırı üretilmez (çifte yansımayı önler).
                    if (onlyCreditSelected && ziynetItems.Count > 0)
                    {
                        var productCodes = ziynetItems
                            .Select(i => (i.ProductCode ?? "").Trim())
                            .Where(c => !string.IsNullOrWhiteSpace(c))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        Dictionary<string, string> olcuByCode;
                        if (productCodes.Count == 0)
                        {
                            olcuByCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                        else
                        {
                            // SQL compatibility seviyelerinde IN-list/OPENJSON çevirisinin "WITH" hatasına düşmemesi için
                            // önce branch ürünlerini çekip kod filtresini bellek içinde uygula.
                            var codeSet = new HashSet<string>(productCodes, StringComparer.OrdinalIgnoreCase);
                            var branchProducts = await _db.Products.AsNoTracking()
                                .Where(p => p.TenantId == tenantId && p.BranchId == purchase.BranchId)
                                .Select(p => new { p.ProductCode, p.Olcu })
                                .ToListAsync(ct);

                            olcuByCode = branchProducts
                                .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode) && codeSet.Contains(p.ProductCode!))
                                .GroupBy(p => p.ProductCode!, StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(
                                    g => g.Key,
                                    g => (g.Select(x => x.Olcu).FirstOrDefault() ?? "").Trim(),
                                    StringComparer.OrdinalIgnoreCase);
                        }

                        foreach (var item in ziynetItems)
                        {
                            if (IsGramAltinHasZiynet(item))
                            {
                                custBal.BalanceHAS += item.Quantity;
                                await CustomerFinanceHelper.AddTransactionAsync(
                                    _db,
                                    tenantId,
                                    custId,
                                    purchase.BranchId,
                                    groupCode: "DOVIZ",
                                    itemName: "HAS",
                                    itemType: null,
                                    quantity: item.Quantity,
                                    direction: +1,
                                    gram: item.Quantity,
                                    ayar: item.Karat,
                                    milyem: CustomerFinanceHelper.MilyemFromAyar(item.Karat),
                                    hasEq: item.Quantity,
                                    unitPriceTl: item.Quantity > 0 ? Math.Round(item.LineTotal / item.Quantity, 6, MidpointRounding.AwayFromZero) : null,
                                    totalPriceTl: item.LineTotal,
                                    refType: "PURCHASE",
                                    refId: purchase.Id,
                                    note: "Ziynet veresiye - gram altın (has)",
                                    txDate: purchase.Date,
                                    ct,
                                    cariDurumOverride: "Alacakli");
                                continue;
                            }

                            var ad = NormalizeZiynetAd(item.ProductName, item.Category, item.ProductCode);
                            var tip = ResolveZiynetTip(item, olcuByCode);
                            await CustomerFinanceHelper.AddTransactionAsync(
                                _db,
                                tenantId,
                                custId,
                                purchase.BranchId,
                                groupCode: "ZIYNET",
                                itemName: ad,
                                itemType: NormalizeZiynetFinanceTip(ad, tip),
                                quantity: item.Quantity,
                                direction: +1,
                                gram: null,
                                ayar: item.Karat,
                                milyem: CustomerFinanceHelper.MilyemFromAyar(item.Karat),
                                hasEq: null,
                                unitPriceTl: item.UnitCost,
                                totalPriceTl: item.LineTotal,
                                refType: "PURCHASE",
                                refId: purchase.Id,
                                note: "Ziynet veresiye alışı",
                                txDate: purchase.Date,
                                ct,
                                cariDurumOverride: "Alacakli");
                        }
                    }
                    else
                    {
                        // Genel veresiye akışı: seçili birim döviz paneline (+) yansır.
                        foreach (var credit in creditRows)
                        {
                            var unit = NormalizeUnitCode(credit.UnitCode);
                            var unitAmount = credit.UnitAmount ?? 0m;
                            if (unitAmount <= 0) continue;
                            switch (unit)
                            {
                                case "USD":
                                    custBal.BalanceUSD += unitAmount;
                                    break;
                                case "EUR":
                                    custBal.BalanceEUR += unitAmount;
                                    break;
                                case "GBP":
                                    custBal.BalanceGBP += unitAmount;
                                    break;
                                case "HAS":
                                    custBal.BalanceHAS += unitAmount;
                                    break;
                                default:
                                    custBal.BalanceTL += unitAmount;
                                    break;
                            }

                            await CustomerFinanceHelper.AddTransactionAsync(
                                _db,
                                tenantId,
                                custId,
                                purchase.BranchId,
                                groupCode: "DOVIZ",
                                itemName: unit,
                                itemType: null,
                                quantity: unitAmount,
                                direction: +1,
                                gram: null,
                                ayar: null,
                                milyem: null,
                                hasEq: null,
                                unitPriceTl: null,
                                totalPriceTl: credit.Amount,
                                refType: "PURCHASE",
                                refId: purchase.Id,
                                note: "Doviz alis veresiye",
                                txDate: purchase.Date,
                                ct,
                                cariDurumOverride: "Alacakli");
                        }
                    }

                    custBal.UpdatedAt = DateTime.UtcNow;
                }
            }

            foreach (var pay in normalized.Where(x => x.PaymentType == PurchasePaymentType.Gold))
            {
                var ayar = NormalizeAyar(pay.GoldKarat);
                var gw = pay.GoldWeight ?? 0m;
                if (string.IsNullOrEmpty(ayar) || gw <= 0)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = "Hurda ödemesi için geçerli ayar ve gram girin." });
                }
                var depo = _db.DepoStoklar.Local.FirstOrDefault(x =>
                    x.TenantId == tenantId && x.BranchId == purchase.BranchId && x.Ayar == ayar);
                if (depo is null)
                {
                    depo = await _db.DepoStoklar
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x =>
                            x.TenantId == tenantId && x.BranchId == purchase.BranchId && x.Ayar == ayar, ct);
                }
                if (depo is null || !depo.WithdrawUnbarcoded(gw))
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = $"Depoda yeterli {ayar} hurda yok (çıkış yapılamadı).", ayar, gram = gw });
                }
                await DepoStokTripleHelper.WithdrawUnbarcodedProportionalAsync(_db, tenantId, purchase.BranchId, ayar, gw, ct);
            }

            // Alış + kalemleri bu aşamada DB'de sabit; yanlışlıkla Modified kaldıysa gereksiz UPDATE'leri kaldır.
            var pe = _db.Entry(purchase);
            if (pe.State == EntityState.Modified)
                pe.State = EntityState.Unchanged;
            foreach (var line in purchase.Items)
            {
                var le = _db.Entry(line);
                if (le.State == EntityState.Modified)
                    le.State = EntityState.Unchanged;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                await tx.RollbackAsync(ct);
                var touched = string.Join(", ", ex.Entries.Select(e => e.Metadata.DisplayName()));
                return Conflict(new
                {
                    error = "Kayıt güncellenemedi (eşzamanlılık). Başka bir işlem veriyi değiştirmiş olabilir veya satır bulunamadı.",
                    entities = touched
                });
            }

            await tx.CommitAsync(ct);

            return Ok(new { id = purchase.Id, grandTotal = purchase.GrandTotal });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<(Purchase? Purchase, IActionResult? Error)> TryBuildPurchaseAsync(
        CreatePurchaseReq req, Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (req.Items is null || req.Items.Count == 0)
            return (null, BadRequest(new { error = "En az bir kalem (Items) gereklidir." }));

        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (branch is null)
            return (null, BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." }));

        var isToptanci = (req.AlisTipi ?? PurchaseType.Musteri) == PurchaseType.Toptanci;
        if (isToptanci)
        {
            var hasSupplier = req.SupplierId.HasValue;
            var hasTedarikciCustomer = req.CustomerId.HasValue;
            var hasPartnerName = !string.IsNullOrWhiteSpace(req.PartnerName);
            if (!hasSupplier && !hasTedarikciCustomer && !hasPartnerName)
                return (null, BadRequest(new { error = "Toptancı alışında Tedarikçi (SupplierId veya CustomerId) ya da PartnerName zorunludur." }));
            if (hasTedarikciCustomer)
            {
                var tedarikci = await _db.Customers.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == req.CustomerId!.Value && c.TenantId == tenantId && !c.IsDeleted && c.CariTip == 1, ct);
                if (tedarikci is null)
                    return (null, BadRequest(new { error = "CustomerId geçerli bir Tedarikçi (CariTip=1) olmalıdır." }));
            }
        }

        var missingCodes = new List<string>();
        if (!isToptanci)
        {
            foreach (var it in req.Items)
            {
                if (it.Kind == ItemKind.Scrap) continue; // Hurda satırları ProductCode zorunlu değil
                if (it.Kind == ItemKind.Forex) continue; // Döviz alışı ProductCode gerektirmez
                if (string.IsNullOrWhiteSpace(it.ProductCode)) { missingCodes.Add("(boş kod)"); continue; }
                var exists = await _db.Products.AsNoTracking()
                    .AnyAsync(p => p.TenantId == tenantId && p.BranchId == branch.Id && p.ProductCode == it.ProductCode, ct);
                if (!exists) missingCodes.Add(it.ProductCode);
            }
        }
        if (missingCodes.Count > 0)
        {
            return (null, BadRequest(new
            {
                error = "Aşağıdaki ProductCode(lar) bu tenant’ta yok:",
                codes = missingCodes.Distinct().ToArray()
            }));
        }

        var purchase = new Purchase
        {
            TenantId = tenantId,
            BranchId = branch.Id,
            UserId = userId,
            CustomerId = req.CustomerId,
            SupplierId = req.SupplierId,
            PurchaseType = req.AlisTipi ?? PurchaseType.Musteri,
            PaymentMethod = req.PaymentMethod ?? PurchasePaymentMethod.Nakit,
            TotalHas = req.TotalHas,
            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
            DocumentNo = req.DocumentNo?.Trim(),
            PartnerName = req.PartnerName?.Trim(),
            Note = req.Note?.Trim(),
        };

        decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;
        int lineNo = 0;
        foreach (var it in req.Items)
        {
            var lineSub = Math.Round(it.Quantity * it.UnitCost, 2);
            var lineDisc = Math.Round(it.Discount, 2);
            var taxable = lineSub - lineDisc;
            var lineTax = Math.Round(taxable * (it.TaxRate / 100m), 2);
            var lineTot = Math.Round(taxable + lineTax, 2);

            subtotal += lineSub;
            discountTotal += lineDisc;
            taxTotal += lineTax;
            grandTotal += lineTot;

            purchase.Items.Add(new PurchaseItem
            {
                TenantId = tenantId,
                LineNo = ++lineNo,
                Kind = it.Kind,
                ProductCode = it.ProductCode?.Trim() ?? "",
                ProductName = it.ProductName?.Trim() ?? "",
                Karat = it.Karat?.Trim() ?? "",
                Category = it.Category?.Trim(),
                Quantity = it.Quantity,
                UnitCost = it.UnitCost,
                Discount = it.Discount,
                TaxRate = it.TaxRate,
                LineTotal = lineTot,
                BirimIscilikHas = it.BirimIscilikHas,
                OdenecekToplamHas = it.OdenecekToplamHas
            });
        }

        purchase.Subtotal = Math.Round(subtotal, 2);
        purchase.DiscountTotal = Math.Round(discountTotal, 2);
        purchase.TaxTotal = Math.Round(taxTotal, 2);
        purchase.GrandTotal = Math.Round(grandTotal, 2);
        purchase.TotalAmount = purchase.GrandTotal;
        return (purchase, null);
    }

    private async Task ApplyPurchaseStockAsync(Purchase purchase, bool isToptanci, Guid tenantId, CancellationToken ct)
    {
        if (isToptanci)
        {
            foreach (var it in purchase.Items)
            {
                if (it.Kind == ItemKind.Forex) continue;
                var ayar = NormalizeAyar(it.Karat);
                if (string.IsNullOrEmpty(ayar) || it.Quantity <= 0) continue;
                var depo = _db.DepoStoklar.Local.FirstOrDefault(x =>
                    x.TenantId == tenantId && x.BranchId == purchase.BranchId && x.Ayar == ayar);
                if (depo is null)
                {
                    depo = await _db.DepoStoklar
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == purchase.BranchId && x.Ayar == ayar, ct);
                }
                if (depo is null)
                {
                    depo = new DepoStok { TenantId = tenantId, BranchId = purchase.BranchId, Ayar = ayar, TotalGram = 0, BarcodedGram = 0, UnbarcodedGram = 0, OrtalamaMaliyet = it.UnitCost };
                    _db.DepoStoklar.Add(depo);
                }
                else if (depo.IsDeleted)
                {
                    // Unique index Tenant+Branch+Ayar olduğu için soft-delete satırı canlandırıp devam et.
                    depo.IsDeleted = false;
                    depo.UpdatedAt = DateTime.UtcNow;
                }
                depo.Add(it.Quantity, it.UnitCost);
                var mal = DepoStokTripleHelper.MalTanimFromPurchaseItem(it, it.Karat ?? "");
                var birimHavuz = it.BirimIscilikHas ?? 0m;
                await DepoStokTripleHelper.AddOrIncrementHavuzAsync(_db, tenantId, purchase.BranchId, ayar, mal, purchase.PartnerName ?? "", birimHavuz, it.Quantity, ct);
            }
        }
        else
        {
            foreach (var it in purchase.Items)
            {
                if (it.Kind == ItemKind.Scrap) continue; // Hurda ScrapService.AddFromPurchaseItemsAsync ile işlendi
                if (it.Kind == ItemKind.Forex) continue;
                if (string.IsNullOrWhiteSpace(it.ProductCode)) continue;
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == purchase.BranchId && p.ProductCode == it.ProductCode, ct);
                if (product is null) continue;

                await _stock.AdjustAsync(
                    branchId: purchase.BranchId,
                    productId: product.Id,
                    productItemId: null,
                    deltaQuantity: +it.Quantity,
                    refKind: StockRefKind.Purchase,
                    refId: purchase.Id,
                    note: $"Purchase {purchase.Id}",
                    ct: ct);

                var isScrap = it.Kind.ToString().Equals("Scrap", StringComparison.OrdinalIgnoreCase);
                if (!isScrap)
                {
                    _db.ProductItems.Add(new ProductItem
                    {
                        TenantId = tenantId,
                        ProductId = product.Id,
                        BranchId = purchase.BranchId,
                        Barcode = GenerateBarcode(),
                        Serial = Guid.NewGuid().ToString("N")[..8].ToUpper(),
                        Karat = it.Karat ?? "",
                        Weight = it.Quantity,
                        IsInStock = true
                    });

                    // Ziynet HAS ürün kartındaki StokMiktari üzerinden; tekil barkodlu gram DepoStokHavuz ile Kasa özeti için.
                    if ((product.InventoryType ?? InventoryType.Tekil) != InventoryType.Ziynet)
                        await ApplyCustomerPurchaseBarcodedToDepoAsync(purchase, product, it, it.Quantity, tenantId, ct);
                }
            }
        }
    }

    /// <summary>
    /// Müşteri alışında oluşan barkodlu tekil gramı <see cref="DepoStok"/> ve <see cref="DepoStokHavuz"/> satırlarına yazar.
    /// Kasa "Ürün Stoğu HAS" / cashbox özeti Havuz barkodlu gram toplamından geldiği için bu adım zorunludur (yalnızca Stocks/ProductItems yetmez).
    /// </summary>
    private async Task ApplyCustomerPurchaseBarcodedToDepoAsync(
        Purchase purchase,
        Product product,
        PurchaseItem it,
        decimal gram,
        Guid tenantId,
        CancellationToken ct)
    {
        if (gram <= 0.0001m) return;
        var ayar = NormalizeAyar(it.Karat ?? product.Karat);
        if (string.IsNullOrEmpty(ayar)) return;

        var depo = _db.DepoStoklar.Local.FirstOrDefault(
            x => x.TenantId == tenantId && x.BranchId == purchase.BranchId && x.Ayar == ayar);
        if (depo is null)
        {
            depo = await _db.DepoStoklar
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    x => x.TenantId == tenantId && x.BranchId == purchase.BranchId && x.Ayar == ayar, ct);
        }
        if (depo is null)
        {
            depo = new DepoStok
            {
                TenantId = tenantId,
                BranchId = purchase.BranchId,
                Ayar = ayar,
                TotalGram = 0,
                BarcodedGram = 0,
                UnbarcodedGram = 0,
                OrtalamaMaliyet = it.UnitCost
            };
            _db.DepoStoklar.Add(depo);
        }
        else if (depo.IsDeleted)
        {
            depo.IsDeleted = false;
            depo.UpdatedAt = DateTime.UtcNow;
        }

        if (!depo.OnBarcodedProductReturned(gram))
            throw new InvalidOperationException($"DepoStok barkodlu girişi yapılamadı (ayar {ayar}, {gram:0.###} g).");

        var malN = DepoStokTripleHelper.NormalizeMal(product.MalTanim);
        var firmaN = DepoStokTripleHelper.NormalizeFirma(product.DepoTedarikciFirma);
        var birim = product.DepoBirimMaliyet.HasValue
            ? DepoStokTripleHelper.RoundBirimMaliyet(product.DepoBirimMaliyet.Value)
            : DepoStokTripleHelper.RoundBirimMaliyet(it.UnitCost);

        if (string.IsNullOrEmpty(malN) || string.IsNullOrEmpty(firmaN))
        {
            malN = "TEKIL_ALIS";
            firmaN = "SUBE";
            birim = DepoStokTripleHelper.RoundBirimMaliyet(it.UnitCost);
        }

        var havuz = _db.DepoStokHavuzlar.Local.FirstOrDefault(x =>
            x.TenantId == tenantId &&
            x.BranchId == purchase.BranchId &&
            x.Ayar == ayar &&
            x.MalTanimNorm == malN &&
            x.TedarikciFirmaNorm == firmaN &&
            x.BirimMaliyet == birim);

        if (havuz is null)
        {
            havuz = await _db.DepoStokHavuzlar
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == purchase.BranchId &&
                x.Ayar == ayar &&
                x.MalTanimNorm == malN &&
                x.TedarikciFirmaNorm == firmaN &&
                x.BirimMaliyet == birim, ct);
        }

        if (havuz is null)
        {
            havuz = new DepoStokHavuz
            {
                TenantId = tenantId,
                BranchId = purchase.BranchId,
                Ayar = ayar,
                MalTanimNorm = malN,
                TedarikciFirmaNorm = firmaN,
                BirimMaliyet = birim,
                TotalGram = 0,
                BarcodedGram = 0,
                UnbarcodedGram = 0,
                UpdatedAt = DateTime.UtcNow
            };
            _db.DepoStokHavuzlar.Add(havuz);
        }
        else if (havuz.IsDeleted)
        {
            // Aynı üçlü+birim anahtarda soft-delete edilmiş satır varsa tekrar aktif et.
            havuz.IsDeleted = false;
            havuz.UpdatedAt = DateTime.UtcNow;
        }

        if (!havuz.OnBarcodedProductReturned(gram))
            throw new InvalidOperationException($"DepoStokHavuz barkodlu girişi yapılamadı (ayar {ayar}, mal:{malN}, {gram:0.###} g).");
    }

    private async Task ApplyCustomerFinanceForPurchaseAsync(
        Purchase purchase,
        bool isToptanci,
        Guid tenantId,
        string? paymentUnit,
        decimal? paymentUnitAmount,
        CancellationToken ct)
    {
        if (isToptanci) return;
        if (!purchase.CustomerId.HasValue) return;

        var isEmanet = purchase.PaymentMethod == PurchasePaymentMethod.Emanet;
        var isVeresiye = purchase.PaymentMethod == PurchasePaymentMethod.Veresiye;
        if (!isEmanet && !isVeresiye) return;

        var customerId = purchase.CustomerId.Value;
        var hurdaIslemi = purchase.Items.Any(i => i.Kind == ItemKind.Scrap);

        if (isEmanet && !hurdaIslemi)
        {
            // Emanet/Cari: ürün müşteri finansında işçilikli hareket olarak görünür,
            // döviz/birim bakiyelerine dokunulmaz.
            foreach (var it in purchase.Items)
            {
                var itemName = string.IsNullOrWhiteSpace(it.ProductName) ? it.ProductCode : it.ProductName;
                await CustomerFinanceHelper.AddTransactionAsync(
                    _db,
                    tenantId,
                    customerId,
                    purchase.BranchId,
                    groupCode: "ISCILIKLI",
                    itemName: itemName ?? "",
                    itemType: "Yeni",
                    quantity: it.Quantity,
                    direction: +1,
                    gram: it.Quantity,
                    ayar: it.Karat,
                    milyem: CustomerFinanceHelper.MilyemFromAyar(it.Karat),
                    hasEq: it.OdenecekToplamHas,
                    unitPriceTl: it.UnitCost,
                    totalPriceTl: it.LineTotal,
                    refType: "PURCHASE",
                    refId: purchase.Id,
                    note: $"Alis L{it.LineNo}",
                    txDate: purchase.Date,
                    ct: ct,
                    cariDurumOverride: "Emanet");
            }
        }

        if (isVeresiye || (isEmanet && hurdaIslemi))
        {
            // Veresiye: sadece seçili birimde bakiye paneline borç (bizim borcumuz) yaz.
            // İşçilikli/Ziynet hareket üretme.
            var birim = NormalizeUnitCode(paymentUnit);
            var birimTutar = paymentUnitAmount.HasValue && paymentUnitAmount.Value > 0
                ? paymentUnitAmount.Value
                : purchase.GrandTotal;
            var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
            switch (birim)
            {
                case "USD":
                    bal.BalanceUSD += birimTutar;
                    break;
                case "EUR":
                    bal.BalanceEUR += birimTutar;
                    break;
                case "GBP":
                    bal.BalanceGBP += birimTutar;
                    break;
                case "HAS":
                    bal.BalanceHAS += birimTutar;
                    break;
                default:
                    bal.BalanceTL += birimTutar;
                    break;
            }
            bal.UpdatedAt = DateTime.UtcNow;

            await CustomerFinanceHelper.AddTransactionAsync(
                _db,
                tenantId,
                customerId,
                purchase.BranchId,
                groupCode: "DOVIZ",
                itemName: birim,
                itemType: null,
                quantity: birimTutar,
                direction: +1,
                gram: null,
                ayar: null,
                milyem: null,
                hasEq: null,
                unitPriceTl: birim == "TL" ? 1m : null,
                totalPriceTl: purchase.GrandTotal,
                refType: "PURCHASE",
                refId: purchase.Id,
                note: isVeresiye ? "Musteriden hurda alisi veresiye" : "Musteriden hurda alisi emanet/cari",
                txDate: purchase.Date,
                ct: ct,
                cariDurumOverride: "Alacakli");
        }
    }

    private async Task AddPurchaseRecentAuditRowsAsync(Purchase purchase, Guid tenantId, CancellationToken ct)
    {
        var totalRounded = Math.Round(Math.Abs(purchase.GrandTotal), 2, MidpointRounding.AwayFromZero);

        if (purchase.CustomerId.HasValue)
        {
            await CustomerFinanceHelper.AddTransactionAsync(
                _db,
                tenantId,
                purchase.CustomerId.Value,
                purchase.BranchId,
                groupCode: "AUDIT",
                itemName: "PURCHASE_EVENT",
                itemType: null,
                quantity: totalRounded,
                direction: +1,
                gram: null,
                ayar: "TL",
                milyem: null,
                hasEq: null,
                unitPriceTl: 1m,
                totalPriceTl: totalRounded,
                refType: "PURCHASE",
                refId: purchase.Id,
                note: $"Alış işlemi kaydı (PURCHASE {purchase.Id})",
                txDate: purchase.Date,
                ct: ct,
                cariDurumOverride: "Finans");
        }

        if (purchase.SupplierId.HasValue)
        {
            var txType = purchase.PaymentMethod == PurchasePaymentMethod.Veresiye
                ? "COLLECTION"
                : "PAYMENT";

            _db.SupplierTransactions.Add(new SupplierTransaction
            {
                TenantId = tenantId,
                SupplierId = purchase.SupplierId.Value,
                BranchId = purchase.BranchId,
                TxType = txType,
                SourceUnit = "TL",
                SourceAmount = totalRounded,
                TargetUnit = "TL",
                TargetAmount = totalRounded,
                IsConverted = false,
                SourceUnitTlRate = 1m,
                TargetUnitTlRate = 1m,
                Description = $"Alış işlemi kaydı (PURCHASE {purchase.Id})",
                TxDate = purchase.Date
            });
        }
    }

    // ================== LIST ==================
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] Guid? customerId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();

        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

        var q = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.Date)
            .AsQueryable();

        var softProp = typeof(Purchase).GetProperty("IsDeleted");
        if (softProp != null)
            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
        if (from.HasValue) q = q.Where(x => x.Date >= from.Value);
        if (to.HasValue) q = q.Where(x => x.Date < to.Value);

        var total = await q.CountAsync(ct);
        var items = await q.Include(x => x.Items)
                           .Skip((page - 1) * pageSize)
                           .Take(pageSize)
                           .ToListAsync(ct);

        var userNames = await UserDisplayNames.BuildUserNameMapAsync(
            _db, tenantId, items.Select(x => x.UserId).ToList(), ct);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items = items.Select(p => ToDto(p) with
            {
                Kullanici = userNames.TryGetValue(p.UserId, out var n) ? n : ""
            })
        });
    }

    // ================== GET BY ID ==================
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var q = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        var softProp = typeof(Purchase).GetProperty("IsDeleted");
        if (softProp != null)
            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

        var p = await q.Include(x => x.Items)
                       .FirstOrDefaultAsync(x => x.Id == id, ct);

        return p is null ? NotFound() : Ok(ToDto(p));
    }

    /// <summary>Kayıtlı alışın ödeme satırlarını döner (karma ödeme sonrası kontrol / rapor).</summary>
    [HttpGet("{id:guid}/payments")]
    public async Task<IActionResult> GetPayments(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var q = _db.Purchases.AsNoTracking().Where(x => x.Id == id && x.TenantId == tenantId);
        var softProp = typeof(Purchase).GetProperty("IsDeleted");
        if (softProp != null)
            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);
        if (!await q.AnyAsync(ct))
            return NotFound();

        var rows = await _db.PurchasePayments.AsNoTracking()
            .Where(x => x.PurchaseId == id && x.TenantId == tenantId && !x.IsDeleted)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new PurchasePaymentRowDto(
                x.Id,
                (int)x.PaymentType,
                x.Amount,
                x.UnitCode,
                x.UnitAmount,
                x.GoldWeight,
                x.SilverWeight,
                x.GoldKarat,
                x.GoldPrice,
                x.BankName,
                x.IBAN,
                x.DueDate,
                x.CashAccount,
                x.CreatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    // ================== UPDATE ==================
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePurchaseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // GÜNCELLEME İŞLEMİNDE STOKTA TUTARSIZLIK OLMAMASI İÇİN DAHA KAPSAMLI BİR LOGİK GEREKİR.
        // Basitleştirme: Sadece başlık ve kalemleri güncelliyoruz, stok hareketi eklemiyoruz.

        var p = await _db.Purchases.Include(x => x.Items)
                                   .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (p is null) return NotFound();

        // branch tenant doğrula
        var branchOk = await _db.Branches.AsNoTracking()
            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        p.BranchId = req.BranchId;
        p.CustomerId = req.CustomerId;
        p.Date = req.Date ?? p.Date;
        p.DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim();
        p.PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
        p.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();

        // **DİKKAT:** Stok hareketlerini güncellemek karmaşıktır. Basit UPDATE yapısında,
        // sadece faturayı güncelliyoruz. Gerçek uygulamada eski stok hareketlerinin tersine çevrilmesi gerekir.

        p.Items.Clear();

        int line = 1;
        foreach (var it in (req.Items ?? new()))
        {
            p.Items.Add(new PurchaseItem
            {
                TenantId = tenantId,
                LineNo = it.LineNo ?? line++,
                Kind = it.Kind,
                ProductCode = it.ProductCode?.Trim() ?? "",
                ProductName = it.ProductName?.Trim() ?? "",
                Karat = it.Karat?.Trim() ?? "",
                Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
                Quantity = it.Quantity,
                UnitCost = it.UnitCost,
                Discount = it.Discount,
                TaxRate = it.TaxRate,
                BirimIscilikHas = it.BirimIscilikHas,
                OdenecekToplamHas = it.OdenecekToplamHas
            });
        }

        RecalcTotals(p);
        await _db.SaveChangesAsync(ct);

        var fresh = await _db.Purchases.AsNoTracking()
            .Include(x => x.Items)
            .FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);

        return Ok(ToDto(fresh));
    }

    // ================== DELETE ==================
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // Satın alma silindiğinde, PurchaseItem ve ProductItem'ların da silinmesi gerekir.
        // Ayrıca stok hareketlerinin tersine çevrilmesi gerekir.
        var p = await _db.Purchases.Include(x => x.Items)
                                   .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (p is null) return NotFound();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var softProp = typeof(Purchase).GetProperty("IsDeleted");
            if (softProp != null) softProp.SetValue(p, true);
            else _db.Purchases.Remove(p); // Hard delete

            await _db.SaveChangesAsync(ct);

            // Stok hareketlerini tersine çevir
            foreach (var item in p.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(pr => pr.TenantId == tenantId && pr.BranchId == p.BranchId && pr.ProductCode == item.ProductCode, ct);
                if (product is not null)
                {
                    // Stoktan ÇIKIŞ (-) hareketi (Girişi tersine çevirme)
                    await _stock.AdjustAsync(
                        branchId: p.BranchId,
                        productId: product.Id,
                        productItemId: null, // Tekil parçaları toplu yönetmiyoruz
                        deltaQuantity: -item.Quantity, // (-) çıkış
                        refKind: StockRefKind.Purchase,
                        refId: p.Id,
                        note: $"Purchase deleted (revert)",
                        ct: ct
                    );
                }
            }

            // ProductItem'ları sil (Satın alma kalemi silinince ProductItem'ın da silinmesi gerekir)
            var itemIdsToDelete = await _db.ProductItems
                .Where(pi => pi.BranchId == p.BranchId && pi.CreatedAt > p.Date.AddMinutes(-5) && pi.CreatedAt < p.Date.AddMinutes(5)) // kaba bir zaman aralığı
                .Select(pi => pi.Id)
                .ToListAsync(ct);

            // Daha kesin: Satın alma hareketindeki ProductMovement'ları bulup ProductItem'ları silmek daha iyidir.
            // Bu basit kodda, ProductItem'ları manuel silmeyi atlıyoruz, çünkü EF'nin cascade delete'i veya ayrı bir lojik ile yönetilmesi gerekir.

            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================== Basit Alım (auto product) ==================
    public sealed record SimplePurchaseItemReq(
        string? ProductCode,
        string ProductName,
        string Karat,
        string? Category,
        decimal Weight,
        decimal UnitCost,
        string? Serial,
        string? Barcode
    );

    public sealed record SimplePurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        DateTime? Date,
        string? DocumentNo,
        string? PartnerName,
        string? Note,
        List<SimplePurchaseItemReq> Items
    );
    [HttpPost("simple")]
    public async Task<IActionResult> CreateSimple([FromBody] SimplePurchaseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "En az bir kalem gerekli." });

        // branch check
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        var purchase = new Purchase
        {
            TenantId = tenantId,
            BranchId = branch.Id,
            UserId = userId,
            CustomerId = req.CustomerId,
            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
            DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim(),
            PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim(),
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),

        };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            int lineNo = 0;
            decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;
            var newProductItems = new List<ProductItem>(); // Yeni oluşan tekil parçaları tut

            foreach (var it in req.Items)
            {
                var code = (it.ProductCode ?? "").Trim();
                Product? product = null;

                if (!string.IsNullOrEmpty(code))
                {
                    product = await _db.Products.AsNoTracking()
                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == purchase.BranchId && p.ProductCode == code, ct);
                }

                if (product is null)
                {
                    var safeCat = (it.Category ?? "URUN").ToUpperInvariant().Replace(' ', '-');
                    var safeName = it.ProductName.Trim().ToUpperInvariant().Replace(' ', '-');
                    var generatedCode = $"{safeCat}-{it.Karat}-{safeName}";

                    product = new Product
                    {
                        TenantId = tenantId,
                        BranchId = purchase.BranchId,
                        ProductCode = generatedCode,
                        Name = it.ProductName.Trim(),
                        Category = it.Category?.Trim(),
                        Karat = it.Karat.Trim()
                    };

                    _db.Products.Add(product);
                    await _db.SaveChangesAsync(ct); // Id & code
                    code = product.ProductCode;
                }

                var lineSub = it.Weight * it.UnitCost;
                var lineTot = Math.Round(lineSub, 2);

                // PurchaseItem oluşturulurken 'it' (SimplePurchaseItemReq) kullanılır
                purchase.Items.Add(new PurchaseItem
                {
                    TenantId = tenantId,
                    LineNo = ++lineNo,
                    Kind = ItemKind.Finished,
                    ProductCode = code,
                    ProductName = it.ProductName.Trim(),
                    Karat = it.Karat.Trim(),
                    Category = it.Category?.Trim(),
                    Quantity = it.Weight,
                    UnitCost = it.UnitCost,
                    Discount = 0m,
                    TaxRate = 0m,
                    LineTotal = lineTot
                });

                subtotal += lineSub;
                grandTotal += lineTot;

                // Yeni ProductItem oluşturma lojiği
                var reqItem = req.Items[lineNo - 1]; // Bu aslında 'it' ile aynıdır
                var barcode = string.IsNullOrWhiteSpace(reqItem.Barcode) ? GenerateBarcode() : reqItem.Barcode!.Trim();

                // ProductItem oluşturulurken yine 'it' (SimplePurchaseItemReq) kullanılır
                var newPi = new ProductItem
                {
                    TenantId = tenantId,
                    ProductId = product.Id,
                    BranchId = purchase.BranchId,
                    Serial = string.IsNullOrWhiteSpace(reqItem.Serial) ? "" : reqItem.Serial!.Trim(),
                    Barcode = barcode,
                    Karat = it.Karat,       // <-- Düzeltildi: 'it.Karat' kullanıldı
                    Weight = it.Weight,     // <-- Düzeltildi: 'it.Weight' kullanıldı
                    IsInStock = true
                };
                newProductItems.Add(newPi);
                _db.ProductItems.Add(newPi); // EF'e ekle
            } // foreach (var it in req.Items) döngüsü biter

            purchase.Subtotal = Math.Round(subtotal, 2);
            purchase.DiscountTotal = Math.Round(discountTotal, 2);
            purchase.TaxTotal = Math.Round(taxTotal, 2);
            purchase.GrandTotal = Math.Round(grandTotal, 2);
            purchase.TotalAmount = purchase.GrandTotal;

            _db.Purchases.Add(purchase);
            await _db.SaveChangesAsync(ct); // Tüm Id'ler oluştu

            // stok + tekil parça hareketlerini kaydet
            foreach (var pi in purchase.Items) // Bu döngüdeki 'pi' artık PurchaseItem'dır (doğru)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == purchase.BranchId && p.ProductCode == pi.ProductCode, ct);
                if (product is null) continue;

                // RelatedProductItem, yeni oluşturulan ProductItem'lar listesinden bulunur
                var relatedProductItem = newProductItems.FirstOrDefault(i => i.ProductId == product.Id);

                // AdjustAsync çağrısı güncellendi:
                await _stock.AdjustAsync(
                    branchId: purchase.BranchId,
                    productId: product.Id,
                    productItemId: relatedProductItem?.Id, // << ProductItem ID kullanıldı
                    deltaQuantity: +pi.Quantity,
                    refKind: StockRefKind.Purchase,
                    refId: purchase.Id,
                    note: $"Purchase {purchase.Id} (simple) - Item {relatedProductItem?.Barcode}",
                    ct: ct
                );
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var saved = await _db.Purchases.AsNoTracking()
                .Include(x => x.Items)
                .FirstAsync(x => x.Id == purchase.Id && x.TenantId == tenantId, ct);

            return CreatedAtAction(nameof(GetById), new { id = saved.Id }, ToDto(saved));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ================== Helpers ==================
    private static void RecalcTotals(Purchase p)
    {
        foreach (var i in p.Items)
        {
            var lineBase = i.Quantity * i.UnitCost;
            var afterDiscount = lineBase - i.Discount;
            var tax = afterDiscount * i.TaxRate;
            i.LineTotal = decimal.Round(afterDiscount + tax, 2);
        }

        p.Subtotal = decimal.Round(p.Items.Sum(i => i.Quantity * i.UnitCost), 2);
        p.DiscountTotal = decimal.Round(p.Items.Sum(i => i.Discount), 2);
        p.TaxTotal = decimal.Round(p.Items.Sum(i =>
        {
            var baseAmt = i.Quantity * i.UnitCost;
            var after = baseAmt - i.Discount;
            return after * i.TaxRate;
        }), 2);
        p.GrandTotal = decimal.Round(p.Subtotal - p.DiscountTotal + p.TaxTotal, 2);
        p.TotalAmount = p.Subtotal; // mevcut davranış
    }

    private static string GenerateBarcode()
    {
        var ts = DateTime.UtcNow.ToString("yyMMddHHmm");
        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                    .Replace("=", "").Replace("+", "").Replace("/", "")
                    .ToUpperInvariant();
        return $"PI-{ts}-{rnd[..6]}";
    }

    private static string NormalizeAyar(string? karat)
    {
        if (string.IsNullOrWhiteSpace(karat)) return "";
        var k = karat.Trim().ToUpperInvariant().Replace("K", "").Replace("AYAR", "").Replace("(", "").Replace(")", "").Replace(" ", "").Trim();
        if (decimal.TryParse(k, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 14 && v <= 24)
            return $"{(int)v}K";
        if (karat.Contains("14") || karat.Contains("585")) return "14K";
        if (karat.Contains("18") || karat.Contains("750")) return "18K";
        if (karat.Contains("22") || karat.Contains("916")) return "22K";
        return karat.Trim().Length > 0 ? karat.Trim() : "";
    }

    private static string NormalizeUnitCode(string? raw)
    {
        var u = (raw ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "TL" => "TL",
            "USD" => "USD",
            "EUR" => "EUR",
            "HAS" => "HAS",
            "GOLD" => "HAS",
            "GUMUS" => "GUMUS",
            "GÜMÜŞ" => "GUMUS",
            "SILVER" => "GUMUS",
            "GBP" => "GBP",
            "POUND" => "GBP",
            _ => "TL"
        };
    }

    private static bool IsGramAltinHasZiynet(PurchaseItem item)
    {
        var name = (item.ProductName ?? "").Trim().ToUpperInvariant();
        var category = (item.Category ?? "").Trim().ToUpperInvariant();
        var karat = (item.Karat ?? "").Trim().ToUpperInvariant();
        var hasAltinText =
            name.Contains("HAS ALTIN") || name.Contains("HASALTIN") ||
            category.Contains("HAS ALTIN") || category.Contains("HASALTIN");
        var gramText = name.Contains("GRAM") || category.Contains("GRAM");
        if (hasAltinText && !gramText)
            return true;

        if (!gramText &&
            (name.Contains("HAS") || category.Contains("HAS")) &&
            (karat.Contains("24") || karat.Contains("999")))
            return true;

        return false;
    }

    private static string NormalizeZiynetAd(string? productName, string? category, string? productCode)
    {
        var raw = !string.IsNullOrWhiteSpace(productName)
            ? productName
            : (!string.IsNullOrWhiteSpace(category) ? category : (productCode ?? ""));
        var val = (raw ?? "").Trim().ToUpperInvariant();
        if (val.Contains("KÜLÇE") || val.Contains("KULCE"))
            return "GRAM ALTIN(KÜLÇE)";
        if ((val.Contains("22 AYAR") || val.Contains("22AYAR")) &&
            (val.Contains("GR") || val.Contains("GRAM")))
            return "22 AYAR(GR)";
        if (val == "GRAM" || val.Contains("GRAM ALTIN (HAS)") || val.Contains("GRAM ALTIN(HAS)"))
            return "GRAM ALTIN(KÜLÇE)";
        return val;
    }

    private static string ResolveZiynetTip(PurchaseItem item, IDictionary<string, string> olcuByCode)
    {
        var code = (item.ProductCode ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(code) &&
            olcuByCode.TryGetValue(code, out var olcu) &&
            !string.IsNullOrWhiteSpace(olcu))
            return olcu.Trim();

        return "Yeni";
    }

    private static string NormalizeZiynetFinanceTip(string itemName, string? rawTip)
    {
        var name = (itemName ?? "").Trim().ToUpperInvariant();
        if (name == "GRAM ALTIN(KÜLÇE)" || name == "GRAM ALTIN(KULCE)")
            return "Yeni";
        return string.IsNullOrWhiteSpace(rawTip) ? "Yeni" : rawTip.Trim();
    }

    private static string BuildSupplierZiynetDescription(string ad, string tip, decimal adet, string reference)
    {
        static string Safe(string? raw) => (raw ?? "").Replace("|", "/").Replace(";", ",").Trim();
        return $"[ZIYNET]|AD={Safe(ad)}|TIP={Safe(tip)}|ADET={adet.ToString("0.###", CultureInfo.InvariantCulture)}|REF={Safe(reference)}";
    }

    private static List<PurchasePayment> BuildHeaderPayments(Purchase purchase, CreatePurchaseReq req)
    {
        if (purchase.GrandTotal <= 0) return new List<PurchasePayment>();
        if (purchase.PaymentMethod == PurchasePaymentMethod.Veresiye ||
            purchase.PaymentMethod == PurchasePaymentMethod.Emanet ||
            purchase.PaymentMethod == PurchasePaymentMethod.NakitHavaleEft)
            return new List<PurchasePayment>();

        var type = purchase.PaymentMethod == PurchasePaymentMethod.Nakit
            ? PurchasePaymentType.Cash
            : PurchasePaymentType.Bank;

        return new List<PurchasePayment>
        {
            new()
            {
                TenantId = purchase.TenantId,
                PurchaseId = purchase.Id,
                PaymentType = type,
                Amount = purchase.GrandTotal,
                UnitCode = "TL"
            }
        };
    }

    private static PurchaseDto ToDto(Purchase p) =>
        new(
            p.Id,
            p.BranchId,
            p.UserId,
            p.CustomerId,
            p.SupplierId,
            (int)p.PurchaseType,
            (int)p.PaymentMethod,
            p.TotalHas,
            p.Date,
            p.DocumentNo,
            p.PartnerName,
            p.Note,
            p.Subtotal,
            p.DiscountTotal,
            p.TaxTotal,
            p.GrandTotal,
            p.TotalAmount,
            (p.Items ?? new List<PurchaseItem>()).Select(ToItemDto).ToList()
        );

    private static PurchaseItemDto ToItemDto(PurchaseItem i) =>
        new(
            i.Id,
            i.PurchaseId,
            i.LineNo,
            i.Kind,
            i.ProductCode,
            i.ProductName,
            i.Karat,
            i.Category,
            i.Quantity,
            i.UnitCost,
            i.Discount,
            i.TaxRate,
            i.LineTotal,
            i.BirimIscilikHas,
            i.OdenecekToplamHas
        );
}

//using System.Security.Claims;
//using kuyumcu_application.Abstractions;
//using kuyumcu_domain.Entities;
//using kuyumcu_infrastructure.Persistence;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using kuyumcu_domain.Enums; // StockRefKind, ItemKind
//using KuyumcuPricee_Service;
//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/[controller]")]
//[Authorize] // JWT zorunlu
//public class PurchasesController : ControllerBase
//{
//    private readonly AppDbContext _db;
//    private readonly IStockService _stock;
//    public PurchasesController(AppDbContext db, IStockService stock)
//    {
//        _db = db;
//        _stock = stock;
//    }

//    // ---------------- Tenant helper ----------------
//    private Guid GetTenantId()
//    {
//        var claim = User?.Claims?.FirstOrDefault(c =>
//            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;

//        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
//            return fromJwt;

//        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
//            Guid.TryParse(hdr.ToString(), out var fromHdr))
//            return fromHdr;

//        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
//    }

//    // ================== DTO'lar ==================
//    // Create
//    public sealed record CreatePurchaseItemReq(
//        int? LineNo,
//        ItemKind Kind,
//        string ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Quantity,
//        decimal UnitCost,
//        decimal Discount,
//        decimal TaxRate
//    );

//    public sealed record CreatePurchaseReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        DateTime? Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        List<CreatePurchaseItemReq> Items
//    );

//    // Update
//    public sealed record UpdatePurchaseItemReq(
//        int? LineNo,
//        ItemKind Kind,
//        string ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Quantity,
//        decimal UnitCost,
//        decimal Discount,
//        decimal TaxRate
//    );

//    public sealed record UpdatePurchaseReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        DateTime? Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        List<UpdatePurchaseItemReq> Items
//    );

//    // Response
//    public sealed record PurchaseItemDto(
//        Guid Id,
//        Guid PurchaseId,
//        int LineNo,
//        ItemKind Kind,
//        string ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Quantity,
//        decimal UnitCost,
//        decimal Discount,
//        decimal TaxRate,
//        decimal LineTotal
//    );

//    public sealed record PurchaseDto(
//        Guid Id,
//        Guid BranchId,
//        Guid UserId,
//        Guid? CustomerId,
//        DateTime Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        decimal Subtotal,
//        decimal DiscountTotal,
//        decimal TaxTotal,
//        decimal GrandTotal,
//        decimal TotalAmount,
//        List<PurchaseItemDto> Items
//    );

//    // ================== CREATE ==================
//    [HttpPost]
//    public async Task<IActionResult> Create([FromBody] CreatePurchaseReq req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        // user
//        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(uidStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        if (req.Items is null || req.Items.Count == 0)
//            return BadRequest(new { error = "En az bir kalem (Items) gereklidir." });

//        // Branch tenant doğrula
//        var branch = await _db.Branches.AsNoTracking()
//            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (branch is null)
//            return BadRequest(new { error = "Geçersiz BranchId veya branch bu tenant'a ait değil." });

//        // Ürün kodlarını tenant bazında doğrula
//        var missingCodes = new List<string>();
//        foreach (var it in req.Items)
//        {
//            if (string.IsNullOrWhiteSpace(it.ProductCode)) { missingCodes.Add("(boş kod)"); continue; }

//            var exists = await _db.Products.AsNoTracking()
//                .AnyAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//            if (!exists) missingCodes.Add(it.ProductCode);
//        }
//        if (missingCodes.Count > 0)
//        {
//            return BadRequest(new
//            {
//                error = "Aşağıdaki ProductCode(lar) bu tenant’ta yok:",
//                codes = missingCodes.Distinct().ToArray()
//            });
//        }

//        // Başlık
//        var purchase = new Purchase
//        {
//            TenantId = tenantId,
//            BranchId = branch.Id,
//            UserId = userId,
//            CustomerId = req.CustomerId,
//            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
//            DocumentNo = req.DocumentNo?.Trim(),
//            PartnerName = req.PartnerName?.Trim(),
//            Note = req.Note?.Trim(),

//        };

//        // Toplamlar
//        decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;
//        int lineNo = 0;

//        foreach (var it in req.Items)
//        {
//            var lineSub = Math.Round(it.Quantity * it.UnitCost, 2);
//            var lineDisc = Math.Round(it.Discount, 2);
//            var taxable = lineSub - lineDisc;
//            var lineTax = Math.Round(taxable * (it.TaxRate / 100m), 2);
//            var lineTot = Math.Round(taxable + lineTax, 2);

//            subtotal += lineSub;
//            discountTotal += lineDisc;
//            taxTotal += lineTax;
//            grandTotal += lineTot;

//            purchase.Items.Add(new PurchaseItem
//            {
//                TenantId = tenantId,
//                LineNo = ++lineNo,
//                Kind = it.Kind,
//                ProductCode = it.ProductCode?.Trim() ?? "",
//                ProductName = it.ProductName?.Trim() ?? "",
//                Karat = it.Karat?.Trim() ?? "",
//                Category = it.Category?.Trim(),
//                Quantity = it.Quantity,
//                UnitCost = it.UnitCost,
//                Discount = it.Discount,
//                TaxRate = it.TaxRate,
//                LineTotal = lineTot
//            });
//        }

//        purchase.Subtotal = Math.Round(subtotal, 2);
//        purchase.DiscountTotal = Math.Round(discountTotal, 2);
//        purchase.TaxTotal = Math.Round(taxTotal, 2);
//        purchase.GrandTotal = Math.Round(grandTotal, 2);
//        purchase.TotalAmount = purchase.GrandTotal;

//        // ---- TRANSACTION ----
//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            _db.Purchases.Add(purchase);
//            await _db.SaveChangesAsync(ct); // Id’ler oluştu

//            // STOK GİRİŞİ (+) ve tekil parça
//            foreach (var it in purchase.Items)
//            {
//                if (string.IsNullOrWhiteSpace(it.ProductCode)) continue;

//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//                if (product is null) continue;

//                // stok +
//                await _stock.AdjustAsync(
//                    branchId: purchase.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +it.Quantity,
//                    refKind: StockRefKind.Purchase,
//                    refId: purchase.Id,
//                    note: $"Purchase {purchase.Id}",
//                    ct: ct
//                );

//                // hurda değilse tekil ProductItem
//                var isScrap = it.Kind.ToString().Equals("Scrap", StringComparison.OrdinalIgnoreCase);
//                if (!isScrap)
//                {
//                    _db.ProductItems.Add(new ProductItem
//                    {
//                        TenantId = tenantId,
//                        ProductId = product.Id,
//                        BranchId = purchase.BranchId,
//                        Barcode = GenerateBarcode(),
//                        Serial = Guid.NewGuid().ToString("N")[..8].ToUpper(),
//                        Karat = it.Karat ?? "",
//                        Weight = it.Quantity,
//                        IsInStock = true
//                    });
//                }
//            }

//            await _db.SaveChangesAsync(ct);
//            await tx.CommitAsync(ct);
//            return CreatedAtAction(nameof(GetById), new { id = purchase.Id }, ToDto(purchase));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }

//    // ================== LIST ==================
//    [HttpGet]
//    public async Task<IActionResult> List(
//        [FromQuery] Guid? branchId,
//        [FromQuery] Guid? customerId,
//        [FromQuery] DateTime? from,
//        [FromQuery] DateTime? to,
//        [FromQuery] int page = 1,
//        [FromQuery] int pageSize = 20,
//        CancellationToken ct = default)
//    {
//        var tenantId = GetTenantId();

//        if (page <= 0) page = 1;
//        if (pageSize <= 0 || pageSize > 200) pageSize = 20;

//        var q = _db.Purchases.AsNoTracking()
//            .Where(x => x.TenantId == tenantId)
//            .OrderByDescending(x => x.Date)
//            .AsQueryable();

//        var softProp = typeof(Purchase).GetProperty("IsDeleted");
//        if (softProp != null)
//            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

//        if (branchId.HasValue) q = q.Where(x => x.BranchId == branchId.Value);
//        if (customerId.HasValue) q = q.Where(x => x.CustomerId == customerId.Value);
//        if (from.HasValue) q = q.Where(x => x.Date >= from.Value);
//        if (to.HasValue) q = q.Where(x => x.Date < to.Value);

//        var total = await q.CountAsync(ct);
//        var items = await q.Include(x => x.Items)
//                           .Skip((page - 1) * pageSize)
//                           .Take(pageSize)
//                           .ToListAsync(ct);

//        return Ok(new
//        {
//            total,
//            page,
//            pageSize,
//            items = items.Select(ToDto)
//        });
//    }

//    // ================== GET BY ID ==================
//    [HttpGet("{id:guid}")]
//    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var q = _db.Purchases.AsNoTracking()
//            .Where(x => x.TenantId == tenantId);

//        var softProp = typeof(Purchase).GetProperty("IsDeleted");
//        if (softProp != null)
//            q = q.Where(x => EF.Property<bool>(x, "IsDeleted") == false);

//        var p = await q.Include(x => x.Items)
//                       .FirstOrDefaultAsync(x => x.Id == id, ct);

//        return p is null ? NotFound() : Ok(ToDto(p));
//    }

//    // ================== UPDATE ==================
//    [HttpPut("{id:guid}")]
//    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePurchaseReq req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var p = await _db.Purchases.Include(x => x.Items)
//                                   .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (p is null) return NotFound();

//        // branch tenant doğrula
//        var branchOk = await _db.Branches.AsNoTracking()
//            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        p.BranchId = req.BranchId;
//        p.CustomerId = req.CustomerId;
//        p.Date = req.Date ?? p.Date;
//        p.DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim();
//        p.PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim();
//        p.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();

//        p.Items.Clear();

//        int line = 1;
//        foreach (var it in (req.Items ?? new()))
//        {
//            p.Items.Add(new PurchaseItem
//            {
//                TenantId = tenantId,
//                LineNo = it.LineNo ?? line++,
//                Kind = it.Kind,
//                ProductCode = it.ProductCode?.Trim() ?? "",
//                ProductName = it.ProductName?.Trim() ?? "",
//                Karat = it.Karat?.Trim() ?? "",
//                Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
//                Quantity = it.Quantity,
//                UnitCost = it.UnitCost,
//                Discount = it.Discount,
//                TaxRate = it.TaxRate
//            });
//        }

//        RecalcTotals(p);
//        await _db.SaveChangesAsync(ct);

//        var fresh = await _db.Purchases.AsNoTracking()
//            .Include(x => x.Items)
//            .FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);

//        return Ok(ToDto(fresh));
//    }

//    // ================== DELETE ==================
//    [HttpDelete("{id:guid}")]
//    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var p = await _db.Purchases.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (p is null) return NotFound();

//        var softProp = typeof(Purchase).GetProperty("IsDeleted");
//        if (softProp != null) softProp.SetValue(p, true);
//        else _db.Purchases.Remove(p);

//        await _db.SaveChangesAsync(ct);
//        return NoContent();
//    }

//    // ================== Basit Alım (auto product) ==================
//    public sealed record SimplePurchaseItemReq(
//        string? ProductCode,
//        string ProductName,
//        string Karat,
//        string? Category,
//        decimal Weight,
//        decimal UnitCost,
//        string? Serial,
//        string? Barcode
//    );

//    public sealed record SimplePurchaseReq(
//        Guid BranchId,
//        Guid? CustomerId,
//        DateTime? Date,
//        string? DocumentNo,
//        string? PartnerName,
//        string? Note,
//        List<SimplePurchaseItemReq> Items
//    );

//    [HttpPost("simple")]
//    public async Task<IActionResult> CreateSimple([FromBody] SimplePurchaseReq req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(uidStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        if (req.Items is null || req.Items.Count == 0)
//            return BadRequest(new { error = "En az bir kalem gerekli." });

//        // branch check
//        var branch = await _db.Branches.AsNoTracking()
//            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        var purchase = new Purchase
//        {
//            TenantId = tenantId,
//            BranchId = branch.Id,
//            UserId = userId,
//            CustomerId = req.CustomerId,
//            Date = req.Date?.ToUniversalTime() ?? DateTime.UtcNow,
//            DocumentNo = string.IsNullOrWhiteSpace(req.DocumentNo) ? null : req.DocumentNo.Trim(),
//            PartnerName = string.IsNullOrWhiteSpace(req.PartnerName) ? null : req.PartnerName.Trim(),
//            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),

//        };

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            int lineNo = 0;
//            decimal subtotal = 0m, discountTotal = 0m, taxTotal = 0m, grandTotal = 0m;

//            foreach (var it in req.Items)
//            {
//                var code = (it.ProductCode ?? "").Trim();
//                Product? product = null;

//                if (!string.IsNullOrEmpty(code))
//                {
//                    product = await _db.Products.AsNoTracking()
//                        .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == code, ct);
//                }

//                if (product is null)
//                {
//                    var safeCat = (it.Category ?? "URUN").ToUpperInvariant().Replace(' ', '-');
//                    var safeName = it.ProductName.Trim().ToUpperInvariant().Replace(' ', '-');
//                    var generatedCode = $"{safeCat}-{it.Karat}-{safeName}";

//                    product = new Product
//                    {
//                        TenantId = tenantId,
//                        ProductCode = generatedCode,
//                        Name = it.ProductName.Trim(),
//                        Category = it.Category?.Trim(),
//                        Karat = it.Karat.Trim()
//                    };

//                    _db.Products.Add(product);
//                    await _db.SaveChangesAsync(ct); // Id & code
//                    code = product.ProductCode;
//                }

//                var lineSub = it.Weight * it.UnitCost;
//                var lineTot = Math.Round(lineSub, 2);

//                purchase.Items.Add(new PurchaseItem
//                {
//                    TenantId = tenantId,
//                    LineNo = ++lineNo,
//                    Kind = ItemKind.Finished,
//                    ProductCode = code,
//                    ProductName = it.ProductName.Trim(),
//                    Karat = it.Karat.Trim(),
//                    Category = it.Category?.Trim(),
//                    Quantity = it.Weight,
//                    UnitCost = it.UnitCost,
//                    Discount = 0m,
//                    TaxRate = 0m,
//                    LineTotal = lineTot
//                });

//                subtotal += lineSub;
//                grandTotal += lineTot;
//            }

//            purchase.Subtotal = Math.Round(subtotal, 2);
//            purchase.DiscountTotal = Math.Round(discountTotal, 2);
//            purchase.TaxTotal = Math.Round(taxTotal, 2);
//            purchase.GrandTotal = Math.Round(grandTotal, 2);
//            purchase.TotalAmount = purchase.GrandTotal;

//            _db.Purchases.Add(purchase);
//            await _db.SaveChangesAsync(ct);

//            // stok + tekil parça
//            foreach (var pi in purchase.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == pi.ProductCode, ct);
//                if (product is null) continue;

//                await _stock.AdjustAsync(
//                    branchId: purchase.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +pi.Quantity,
//                    refKind: StockRefKind.Purchase,
//                    refId: purchase.Id,
//                    note: $"Purchase {purchase.Id} (simple)",
//                    ct: ct
//                );

//                var reqItem = req.Items[pi.LineNo - 1];
//                var barcode = string.IsNullOrWhiteSpace(reqItem.Barcode) ? GenerateBarcode() : reqItem.Barcode!.Trim();

//                _db.ProductItems.Add(new ProductItem
//                {
//                    TenantId = tenantId,
//                    ProductId = product.Id,
//                    BranchId = purchase.BranchId,
//                    Serial = string.IsNullOrWhiteSpace(reqItem.Serial) ? "" : reqItem.Serial!.Trim(),
//                    Barcode = barcode,
//                    Karat = pi.Karat,
//                    Weight = pi.Quantity,
//                    IsInStock = true
//                });
//            }

//            await _db.SaveChangesAsync(ct);
//            await tx.CommitAsync(ct);

//            var saved = await _db.Purchases.AsNoTracking()
//                .Include(x => x.Items)
//                .FirstAsync(x => x.Id == purchase.Id && x.TenantId == tenantId, ct);

//            return CreatedAtAction(nameof(GetById), new { id = saved.Id }, ToDto(saved));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }

//    // ================== Helpers ==================
//    private static void RecalcTotals(Purchase p)
//    {
//        foreach (var i in p.Items)
//        {
//            var lineBase = i.Quantity * i.UnitCost;
//            var afterDiscount = lineBase - i.Discount;
//            var tax = afterDiscount * i.TaxRate;
//            i.LineTotal = decimal.Round(afterDiscount + tax, 2);
//        }

//        p.Subtotal = decimal.Round(p.Items.Sum(i => i.Quantity * i.UnitCost), 2);
//        p.DiscountTotal = decimal.Round(p.Items.Sum(i => i.Discount), 2);
//        p.TaxTotal = decimal.Round(p.Items.Sum(i =>
//        {
//            var baseAmt = i.Quantity * i.UnitCost;
//            var after = baseAmt - i.Discount;
//            return after * i.TaxRate;
//        }), 2);
//        p.GrandTotal = decimal.Round(p.Subtotal - p.DiscountTotal + p.TaxTotal, 2);
//        p.TotalAmount = p.Subtotal; // mevcut davranış
//    }

//    private static string GenerateBarcode()
//    {
//        var ts = DateTime.UtcNow.ToString("yyMMddHHmm");
//        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
//                    .Replace("=", "").Replace("+", "").Replace("/", "")
//                    .ToUpperInvariant();
//        return $"PI-{ts}-{rnd[..6]}";
//    }

//    private static PurchaseDto ToDto(Purchase p) =>
//        new(
//            p.Id,
//            p.BranchId,
//            p.UserId,
//            p.CustomerId,
//            p.Date,
//            p.DocumentNo,
//            p.PartnerName,
//            p.Note,
//            p.Subtotal,
//            p.DiscountTotal,
//            p.TaxTotal,
//            p.GrandTotal,
//            p.TotalAmount,
//            (p.Items ?? new List<PurchaseItem>()).Select(ToItemDto).ToList()
//        );

//    private static PurchaseItemDto ToItemDto(PurchaseItem i) =>
//        new(
//            i.Id,
//            i.PurchaseId,
//            i.LineNo,
//            i.Kind,
//            i.ProductCode,
//            i.ProductName,
//            i.Karat,
//            i.Category,
//            i.Quantity,
//            i.UnitCost,
//            i.Discount,
//            i.TaxRate,
//            i.LineTotal
//        );
//}
