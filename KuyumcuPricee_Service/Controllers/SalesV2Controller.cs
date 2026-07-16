using System.Security.Claims;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq; // LINQ için eklendi
using KUYUMCU.Price_Service.Services;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/sales/v2")]
[Authorize]
public sealed class SalesV2Controller : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IStockService _stock;
    private readonly ExchangeRateService _rates;
    private readonly IFinanceService _finance;
    private readonly IAccountingJournalService _accounting;
    private readonly IScrapService _scrap;
    private readonly IEInvoiceWorkflowService _eInvoiceWorkflow;
    private readonly TransactionReversalService _reversal;

    public SalesV2Controller(
        AppDbContext db,
        IStockService stock,
        ExchangeRateService rates,
        IFinanceService finance,
        IAccountingJournalService accounting,
        IScrapService scrap,
        IEInvoiceWorkflowService eInvoiceWorkflow,
        TransactionReversalService reversal)
    {
        _db = db;
        _stock = stock;
        _rates = rates;
        _finance = finance;
        _accounting = accounting;
        _scrap = scrap;
        _eInvoiceWorkflow = eInvoiceWorkflow;
        _reversal = reversal;
    }

    // -------- Tenant helper --------
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
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSaleReqV2 req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        // user
        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(uidStr, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

        // branch (tenant ile)
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == req.BranchId && x.TenantId == tenantId, ct);
        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        // items
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "En az bir satış kalemi zorunludur." });

        // müşteri (opsiyonel) tenant kontrol
        if (req.CustomerId is Guid cid)
        {
            var custOk = await _db.Customers.AsNoTracking()
                .AnyAsync(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
        }

        var fallbackPaymentType = string.IsNullOrWhiteSpace(req.PaymentType) ? "Nakit" : req.PaymentType.Trim();
        var deliveryType = NormalizeDeliveryType(req.DeliveryType);
        var sale = new Sale
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            BranchId = branch.Id,
            UserId = userId,
            CustomerId = req.CustomerId,
            PaymentType = fallbackPaymentType,
            DeliveryType = deliveryType,
            Items = new List<SaleItem>()
        };


        int lineNo = 0;
        decimal subtotal = 0m, discTot = 0m, taxTot = 0m, grandTot = 0m;
        var deliveredQtyByLineNo = new Dictionary<int, decimal>();

        foreach (var it in req.Items)
        {
            // EMANET delivery + ZIYNET kategori kombinasyonunda ProductCode opsiyoneldir
            // (müşteri hesabına alacak kaydı; fiziksel stok hareketi olmaz).
            var isZiynetEmanetItem =
                deliveryType == "EMANET" &&
                string.Equals((it.Category ?? "").Trim(), "ZIYNET", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(it.ProductCode) && !isZiynetEmanetItem)
                return BadRequest(new { error = "ProductCode zorunludur." });

            var isVirtualForexLine = IsVirtualForexSaleItem(it.ProductCode ?? "", it.Category);
            var isDovizItem = string.Equals((it.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase)
                              || isVirtualForexLine;
            var isGumusItem = string.Equals((it.Category ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase);
            if ((isDovizItem || isGumusItem) && it.DeliveredQuantity.HasValue)
            {
                var delivered = Math.Round(it.DeliveredQuantity.Value, 4, MidpointRounding.AwayFromZero);
                if (delivered < 0 || delivered > it.Quantity)
                    return BadRequest(new { error = "Teslim miktarı 0 ile satır miktarı arasında olmalıdır." });
                if (delivered < it.Quantity - 0.0001m && !req.CustomerId.HasValue)
                    return BadRequest(new { error = "Teslim edilmeyen kısım için müşteri seçilmelidir." });
            }

            Product? product = null;
            if (!string.IsNullOrWhiteSpace(it.ProductCode))
            {
                product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == branch.Id && p.ProductCode == it.ProductCode, ct);
                if (!isVirtualForexLine && !isZiynetEmanetItem && product is null)
                    return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });
            }


            // --- Tekil (ProductItem) / Ziynet (adet) Kontrolü ---
            if (it.ProductItemId.HasValue)
            {
                // Tekil parça: miktar = parça ağırlığı, satışta Durum = Satildi
                var itemId = it.ProductItemId.Value;
                var itemEntity = await _db.ProductItems
                    .FirstOrDefaultAsync(pi => pi.Id == itemId && pi.TenantId == tenantId, ct);

                if (itemEntity is null)
                    return BadRequest(new { error = $"Geçersiz ProductItemId: {itemId}" });

                if (itemEntity.BranchId != branch.Id)
                    return BadRequest(new { error = $"ProductItemId {itemId} bu şubeye ait değil." });

                if (!itemEntity.IsInStock)
                    return BadRequest(new { error = $"ProductItemId {itemId} stokta değil/satılmış." });

                if (it.Quantity != itemEntity.Weight)
                    return BadRequest(new { error = $"Tekil parça (ID: {itemId}) satılırken miktar ({it.Quantity}) parça ağırlığına ({itemEntity.Weight}) eşit olmalıdır." });
            }
            else
            {
                var isZiynet = product is not null &&
                               (product.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet;
                var isSpecial = product?.IsSpecialProduct == true;
                // EMANET teslimde (teslim edilmeyen kalem) fiziksel stok hareketi olmaz;
                // müşteri hesabına alacak yazılır. Bu nedenle stok-adet doğrulaması atlanır.
                var isEmanetTeslim = deliveryType == "EMANET";
                // ProductItemId olmayan satırlarda sadece adet bazlı ürünlerde (ziynet/özel) stok-adet doğrulaması yap.
                // Tekil ürünlerde mevcut akışta gram bazlı satış desteklenir.
                if ((isZiynet || isSpecial) && !isEmanetTeslim)
                {
                    var adet = (int)Math.Round(it.Quantity);
                    var stok = product.StokMiktari ?? 0;
                    if (adet <= 0 || stok < adet)
                    {
                        var tur = isSpecial ? "Özel" : "Ziynet";
                        return BadRequest(new { error = $"{tur} ürün {it.ProductCode} için yetersiz stok (mevcut: {stok}, istenen: {adet})." });
                    }
                }
            }
            // --- Kontrol SONU ---

            var lineBase = it.UnitPrice * it.Quantity;
            var afterDisc = lineBase - it.Discount;
            var tax = afterDisc * it.TaxRate;
            var lineTotal = Math.Round(afterDisc + tax, 2);

            subtotal += Math.Round(lineBase, 2);
            discTot += Math.Round(it.Discount, 2);
            taxTot += Math.Round(tax, 2);
            grandTot += lineTotal;

            var assignedLineNo = it.LineNo ?? ++lineNo;
            if ((isDovizItem || isGumusItem) && it.DeliveredQuantity.HasValue)
                deliveredQtyByLineNo[assignedLineNo] = Math.Round(it.DeliveredQuantity.Value, 4, MidpointRounding.AwayFromZero);

            sale.Items.Add(new SaleItem
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SaleId = sale.Id,
                LineNo = assignedLineNo,
                Kind = ResolveSaleItemKind(isVirtualForexLine, product, it.Category, it.Karat, it.ProductName),
                ProductCode = it.ProductCode.Trim(),
                ProductName = it.ProductName?.Trim() ?? "",
                Karat = it.Karat?.Trim() ?? "",
                Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
                Quantity = it.Quantity,
                DeliveredQuantity = (isDovizItem || isGumusItem) && it.DeliveredQuantity.HasValue
                    ? Math.Round(it.DeliveredQuantity.Value, 4, MidpointRounding.AwayFromZero)
                    : null,
                UnitPrice = it.UnitPrice,
                Discount = it.Discount,
                TaxRate = it.TaxRate,
                LineTotal = lineTotal,
                ProductItemId = it.ProductItemId // << Bağlantı Eklendi
            });

        }

        var unitRates = _rates.GetUnitToTlRates();
        var sellUnitRates = _rates.GetUnitToTlSellRates();
        var payments = NormalizePayments(req.Payments, fallbackPaymentType, grandTot, unitRates, sellUnitRates);
        if (payments.Count == 0)
            return BadRequest(new { error = "En az bir ödeme kalemi girilmelidir." });
        if (payments.Any(p => p.Amount <= 0 || p.AmountTl <= 0))
            return BadRequest(new { error = "Ödeme kalemlerinde tutar 0'dan büyük olmalıdır." });
        var paymentTotal = Math.Round(payments.Sum(x => x.AmountTl), 2, MidpointRounding.AwayFromZero);
        var grandRounded = Math.Round(grandTot, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(paymentTotal - grandRounded) > 0.01m)
            return BadRequest(new { error = $"Ödeme toplamı ({paymentTotal:N2}) satış toplamına ({grandRounded:N2}) eşit olmalıdır." });

        var veresiyeToplam = payments
            .Where(x => string.Equals(x.Method, "Veresiye", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.AmountTl);
        if (veresiyeToplam > 0 && !req.CustomerId.HasValue)
            return BadRequest(new { error = "Veresiye işlemi için müşteri seçilmelidir." });

        var tedarikciVeresiyeToplam = payments
            .Where(x => string.Equals(x.Method, "TedarikciVeresiye", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.AmountTl);
        if (tedarikciVeresiyeToplam > 0.01m)
        {
            if (!req.SupplierIdForTedarikciVeresiye.HasValue || req.SupplierIdForTedarikciVeresiye.Value == Guid.Empty)
                return BadRequest(new { error = "Tedarikçi veresiyesi için tedarikçi seçilmelidir." });
            var supOk = await _db.Suppliers.AsNoTracking()
                .AnyAsync(s => s.Id == req.SupplierIdForTedarikciVeresiye.Value && s.TenantId == tenantId && !s.IsDeleted, ct);
            if (!supOk)
                return BadRequest(new { error = "Geçersiz tedarikçi (Tedarikçi veresiye)." });
        }
        if (deliveryType == "EMANET" && !req.CustomerId.HasValue)
            return BadRequest(new { error = "Emanet işlemi için müşteri seçilmelidir." });

        var takasToplam = payments
            .Where(x => string.Equals(x.Method, "Takas", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Amount);
        if (takasToplam > 0)
        {
            if (!req.CustomerId.HasValue || req.CustomerId.Value == Guid.Empty)
                return BadRequest(new { error = "Takas işlemi için müşteri seçilmelidir." });
            if (req.TakasHammadde is null || req.TakasHammadde.Gram <= 0 || string.IsNullOrWhiteSpace(req.TakasHammadde.Ayar))
                return BadRequest(new { error = "Takas seçildiğinde takas hammadde bilgisi (ayar/gram) zorunludur." });
            if (req.TakasHammadde.BirimMaliyet <= 0)
                return BadRequest(new { error = "Takas için birim maliyet 0'dan büyük olmalıdır." });
        }
        sale.PaymentType = payments.OrderByDescending(x => x.Amount).First().Method;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var batchId = Guid.NewGuid();
            _db.Sales.Add(sale);
            _db.SaleItems.AddRange(sale.Items);
            await _db.SaveChangesAsync(ct);

            // Döviz ürün (USD/EUR/GBP) satışları e-fatura taslağına düşmez.
            var hasBillableItems = sale.Items.Any(x => !IsForexSaleItemLine(x));
            // Profil ayarları (otomatik taslak kuralları, işçilik kuralları) IsActive durumundan
            // bağımsız uygulanır; aksi halde pasif profilde varsayılanlara düşer ve şube ayarları yok sayılır.
            var profile = await _db.EInvoiceProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == sale.BranchId, ct);

            if (hasBillableItems)
            {
                // Her ödeme yöntemi için ayrı taslak fatura oluşturma:
                // GetQualifyingPaymentsForDraft, profil ayarlarına uyan ödeme kalemlerini döner.
                // Tek ödeme → 1 fatura (ratio=1.0), çoklu ödeme → N fatura (her biri kendi oranıyla).
                var qualifyingPayments = EInvoiceProfileSettingsCodec.GetQualifyingPaymentsForDraft(
                    profile,
                    payments.Select(x => (x.Method, x.AmountTl)).ToList(),
                    grandTot);

                if (qualifyingPayments.Count > 0)
                {
                    var customer = sale.CustomerId.HasValue
                        ? await _db.Customers.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == sale.CustomerId.Value && x.TenantId == tenantId, ct)
                        : null;

                    foreach (var (method, amountTl) in qualifyingPayments)
                    {
                        var splitRatio = grandTot > 0m
                            ? Math.Round(amountTl / grandTot, 10, MidpointRounding.AwayFromZero)
                            : 1m;
                        // Oran toplamının 1.0'ı aşmaması için güvenlik sınırı.
                        splitRatio = Math.Min(1m, Math.Max(0m, splitRatio));

                        var invoice = new Invoice
                        {
                            Id = Guid.NewGuid(),
                            TenantId = tenantId,
                            SaleId = sale.Id,
                            BranchId = sale.BranchId,
                            CustomerId = sale.CustomerId,
                            InvoiceDate = DateTime.UtcNow,
                            GrandTotal = Math.Round(amountTl, 2, MidpointRounding.AwayFromZero),
                            PaymentType = method,
                            PaymentSplitRatio = splitRatio,
                            IsExported = false
                        };
                        _db.Invoices.Add(invoice);
                        await _db.SaveChangesAsync(ct);
                        await _eInvoiceWorkflow.QueueInvoiceAsync(invoice, customer, ct);
                    }
                }
            }

            var salePaymentRows = new List<SalePayment>();
            foreach (var p in payments)
            {
                var row = new SalePayment
                {
                    TenantId = tenantId,
                    SaleId = sale.Id,
                    BranchId = sale.BranchId,
                    Method = p.Method,
                    Currency = p.Currency,
                    // Dövizli satışlarda kasa/vault defteri seçilen para birimi tutarıyla yürümelidir.
                    // TL karşılığı (AmountTl) yalnızca toplam doğrulama ve belge kontrolü için kullanılır.
                    Amount = p.Amount,
                    Direction = "Gelir",
                    LedgerType = ResolveLedgerType(p.Method),
                    Account = p.Account,
                    Note = $"Sale {sale.Id}"
                };
                salePaymentRows.Add(row);
                _db.SalePayments.Add(row);
            }

            await _finance.ApplySalePaymentsAsync(tenantId, sale.BranchId, sale.Id, salePaymentRows, ct);
            await _accounting.RecordSaleAsync(sale, sale.Items, salePaymentRows, ct);

            if (tedarikciVeresiyeToplam > 0.01m && req.SupplierIdForTedarikciVeresiye.HasValue)
                await ApplyTedarikciVeresiyeToSupplierAsync(
                    tenantId,
                    req.SupplierIdForTedarikciVeresiye!.Value,
                    sale.BranchId,
                    payments.Where(x => string.Equals(x.Method, "TedarikciVeresiye", StringComparison.OrdinalIgnoreCase)).ToList(),
                    sale.Id,
                    ct);

            var soldTekilProductIds = new HashSet<Guid>();

            // STOK ÇIKIŞLARI: Tekil = ProductItem IsInStock=false; Ziynet = Product.StokMiktari -= adet
            // EMANET teslimde fiziksel stok hareketi yapılmaz (teslim edilmeyen kalem; müşteriye alacak yazılır).
            foreach (var si in sale.Items)
            {
                if (deliveryType == "EMANET") continue;

                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == sale.BranchId && p.ProductCode == si.ProductCode, ct);
                if (product is null) continue;

                await _stock.AdjustAsync(
                    branchId: sale.BranchId,
                    productId: product.Id,
                    productItemId: si.ProductItemId,
                    deltaQuantity: -si.Quantity,
                    refKind: StockRefKind.Sale,
                    refId: sale.Id,
                    note: $"Sale {sale.Id} L{si.LineNo}",
                    ct: ct
                );

                // Tekil: parçayı satıldı yap + depo barkodlu/toplam düşümü
                if (si.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == si.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = false;
                        pItem.UpdatedAt = DateTime.UtcNow;
                        soldTekilProductIds.Add(pItem.ProductId);
                        var (depoOk, depoErr) = await DepoStokGramHelper.TryApplyBarcodedProductSoldAsync(_db, tenantId, sale.BranchId, pItem, ct);
                        if (!depoOk)
                        {
                            await tx.RollbackAsync(ct);
                            return BadRequest(new { error = depoErr });
                        }
                    }
                }
                // Ziynet: ürün stok adedini düşür
                else
                {
                    var prodTracked = await _db.Products.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == sale.BranchId && p.ProductCode == si.ProductCode, ct);
                    if (prodTracked is not null &&
                        ((prodTracked.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet
                        || prodTracked.IsSpecialProduct))
                    {
                        var adet = (int)Math.Round(si.Quantity);
                        prodTracked.StokMiktari = Math.Max(0, (prodTracked.StokMiktari ?? 0) - adet);
                    }
                }
            }

            // Döviz satışı: kasadaki ilgili döviz (Vault USD/EUR) bakiyesinden düş.
            await ApplyForexVaultAdjustmentAsync(
                tenantId,
                sale.BranchId,
                sale.Id,
                sale.Items,
                sign: -1m,
                description: "Doviz satisi kasa cikisi",
                ct: ct,
                deliveredQtyByLineNo: deliveredQtyByLineNo.Count > 0 ? deliveredQtyByLineNo : null);

            if (deliveryType != "EMANET" && sale.CustomerId.HasValue && deliveredQtyByLineNo.Count > 0)
            {
                await ApplyUndeliveredMetalCustomerCreditAsync(
                    tenantId,
                    sale.CustomerId.Value,
                    sale.BranchId,
                    sale.Id,
                    sale.Items,
                    deliveredQtyByLineNo,
                    req.EmanetDovizLedgerByUnit,
                    batchId,
                    sale.CreatedAt,
                    ct);
            }

            await ApplySilverVaultAdjustmentAsync(
                tenantId,
                sale.BranchId,
                sale.Id,
                sale.Items,
                sign: -1m,
                description: "Gumus satisi kasa cikisi",
                ct: ct,
                deliveredQtyByLineNo: deliveredQtyByLineNo.Count > 0 ? deliveredQtyByLineNo : null);

            // Satılan tekil barkodlu ürünün stokta açık parçası kalmadıysa ürün kartından düş.
            foreach (var productId in soldTekilProductIds)
            {
                var prodToHide = await _db.Products.FirstOrDefaultAsync(x =>
                    x.TenantId == tenantId &&
                    x.BranchId == sale.BranchId &&
                    x.Id == productId &&
                    !x.IsDeleted, ct);
                if (prodToHide is null) continue;

                var inv = prodToHide.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil;
                if (inv == kuyumcu_domain.Enums.InventoryType.Tekil)
                {
                    prodToHide.IsDeleted = true;
                }
            }

            var onlyVeresiye = payments.Count == 1 && string.Equals(payments[0].Method, "Veresiye", StringComparison.OrdinalIgnoreCase);
            if (sale.CustomerId.HasValue)
            {
                var customerId = sale.CustomerId.Value;
                if (onlyVeresiye)
                {
                    // Sadece veresiye:
                    // - Ürün hareketleri ilgili panelde (ZIYNET/ISCILIKLI) izlenir.
                    // - Ziynet/işçilikli satışlarda DOVIZ paneline ayrıca borç yazılmaz
                    //   (çift yansıma olmasın diye). Karma ödemede veresiye kısmı DOVIZ'e yazılır.
                    var hasRate = unitRates.TryGetValue("HAS", out var h) && h > 0 ? h : 0m;
                    var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
                    var productMap = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
                    foreach (var code in sale.Items.Select(x => x.ProductCode).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var prod = await _db.Products.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == sale.BranchId && x.ProductCode == code, ct);
                        if (prod != null)
                            productMap[code] = prod;
                    }
                    foreach (var si in sale.Items)
                    {
                        productMap.TryGetValue(si.ProductCode, out var prod);
                        if (await TryApplyHasAltinSaleLedgerAsync(
                                _db, tenantId, customerId, sale, si, prod, bal,
                                direction: -1, noteSuffix: "ziynet has veresiye", batchId, ct))
                            continue;

                        var isDoviz = string.Equals((si.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase)
                                      || string.Equals((prod?.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase);
                        if (isDoviz) continue;

                            var isZiynet =
                                ((prod != null &&
                                  ((prod.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet))
                                 || IsZiynetCategoryToken(si.Category)
                                 || IsZiynetCategoryToken(prod?.Category)
                                 || IsZiynetCategoryToken(si.ProductName)
                                 || IsZiynetCategoryToken(prod?.Name)
                                 || IsZiynetCategoryToken(prod?.ZiynetTipi))
                                && !IsHasAltinZiynetSaleItem(prod, si);
                        if (isZiynet)
                        {
                            await CustomerFinanceHelper.AddTransactionAsync(
                                _db,
                                tenantId,
                                customerId,
                                sale.BranchId,
                                groupCode: "ZIYNET",
                                itemName: NormalizeZiynetName(prod, si.ProductName),
                                itemType: NormalizeZiynetFinanceTip(
                                    NormalizeZiynetName(prod, si.ProductName),
                                    string.IsNullOrWhiteSpace(prod?.Olcu) ? "Yeni" : prod!.Olcu!.Trim()),
                                quantity: Math.Abs(si.Quantity),
                                direction: -1,
                                gram: null,
                                ayar: si.Karat,
                                milyem: null,
                                hasEq: null,
                                unitPriceTl: si.UnitPrice,
                                totalPriceTl: si.LineTotal,
                                refType: "SALE",
                                refId: sale.Id,
                                note: $"Satis L{si.LineNo} (ziynet veresiye)",
                                txDate: sale.CreatedAt,
                                ct: ct,
                                batchId: batchId);
                            continue;
                        }

                        var milyem = CustomerFinanceHelper.MilyemFromAyar(si.Karat);
                        var hasEq = hasRate > 0 ? Math.Round(si.LineTotal / hasRate, 6, MidpointRounding.AwayFromZero) : Math.Round(si.Quantity * milyem, 6);
                        await CustomerFinanceHelper.AddTransactionAsync(
                            _db,
                            tenantId,
                            customerId,
                            sale.BranchId,
                            groupCode: "ISCILIKLI",
                            itemName: string.IsNullOrWhiteSpace(si.ProductName) ? si.ProductCode : si.ProductName,
                            itemType: null,
                            quantity: si.Quantity,
                            direction: -1,
                            gram: si.Quantity,
                            ayar: si.Karat,
                            milyem: milyem,
                            hasEq: hasEq,
                            unitPriceTl: si.UnitPrice,
                            totalPriceTl: si.LineTotal,
                            refType: "SALE",
                            refId: sale.Id,
                            note: $"Satis L{si.LineNo} (yalniz veresiye)",
                            txDate: sale.CreatedAt,
                            ct: ct,
                            batchId: batchId);
                    }

                    var hasNonForexItem = sale.Items.Any(si =>
                    {
                        productMap.TryGetValue(si.ProductCode, out var prod);
                        return !string.Equals((si.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase)
                               && !string.Equals((prod?.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase);
                    });

                    // Sadece döviz satışı veresiye ise DOVIZ paneline yazmaya devam et.
                    if (!hasNonForexItem)
                    {
                        foreach (var vp in payments.Where(x => string.Equals(x.Method, "Veresiye", StringComparison.OrdinalIgnoreCase)))
                        {
                            await ApplyVeresiyePaymentToCustomerAsync(
                                bal, tenantId, customerId, sale.BranchId, sale.Id, vp,
                                note: "Yalniz veresiye odeme birimi",
                                txDate: sale.CreatedAt, batchId, ct);
                        }
                    }
                    bal.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    if (deliveryType == "EMANET" && !req.SkipEmanetCustomerLedger)
                    {
                        var hasRate = unitRates.TryGetValue("HAS", out var h) && h > 0 ? h : 0m;
                        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
                        // Ziynet tipini (Yeni/Eski) request kaleminden taşı (katalog eşleşmesi olmayan satırlar için).
                        var reqOlcuByLine = (req.Items ?? new())
                            .Where(x => x.LineNo.HasValue)
                            .GroupBy(x => x.LineNo!.Value)
                            .ToDictionary(g => g.Key, g => g.First().Olcu);
                        var productMap = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
                        foreach (var code in sale.Items.Select(x => x.ProductCode).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var prod = await _db.Products.AsNoTracking()
                                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == sale.BranchId && x.ProductCode == code, ct);
                            if (prod != null)
                                productMap[code] = prod;
                        }
                        foreach (var si in sale.Items)
                        {
                            productMap.TryGetValue(si.ProductCode, out var prod);
                            if (await TryApplyHasAltinSaleLedgerAsync(
                                    _db, tenantId, customerId, sale, si, prod, bal,
                                    direction: +1, noteSuffix: "ziynet has emanet", batchId, ct))
                                continue;

                            var isDoviz = string.Equals((si.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals((prod?.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase);
                            if (isDoviz)
                            {
                                var unit = NormalizeForexCurrency(string.IsNullOrWhiteSpace(si.Karat) ? si.ProductName : si.Karat).ToUpperInvariant();
                                if (string.IsNullOrWhiteSpace(unit)) unit = "TL";
                                var amount = Math.Round(Math.Abs(si.Quantity), 4, MidpointRounding.AwayFromZero);
                                if (amount > 0m)
                                {
                                    string? ledgerSide = null;
                                    req.EmanetDovizLedgerByUnit?.TryGetValue(unit, out ledgerSide);
                                    await CustomerFinanceHelper.ApplyEmanetDovizLegAsync(
                                        _db, bal, tenantId, customerId, sale.BranchId,
                                        unit, amount, ledgerSide,
                                        unitPriceTl: si.UnitPrice,
                                        totalPriceTl: si.LineTotal,
                                        gram: null, ayar: unit, hasEq: null,
                                        refType: "SALE", refId: sale.Id,
                                        note: $"Satis L{si.LineNo} (emanet doviz)",
                                        txDate: sale.CreatedAt, batchId: batchId, ct: ct,
                                        applyBalanceDelta: ApplyBalanceDeltaByUnit);
                                }
                                continue;
                            }

                            var isZiynet =
                                ((prod != null &&
                                  ((prod.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet))
                                 || IsZiynetCategoryToken(si.Category)
                                 || IsZiynetCategoryToken(prod?.Category)
                                 || IsZiynetCategoryToken(si.ProductName)
                                 || IsZiynetCategoryToken(prod?.Name)
                                 || IsZiynetCategoryToken(prod?.ZiynetTipi))
                                && !IsHasAltinZiynetSaleItem(prod, si);
                            if (isZiynet)
                            {
                                // Tip önceliği: request Olcu (Yeni/Eski) → katalog ürünü Olcu → "Yeni".
                                var rawTip = reqOlcuByLine.TryGetValue(si.LineNo, out var reqOlcu) && !string.IsNullOrWhiteSpace(reqOlcu)
                                    ? reqOlcu!.Trim()
                                    : (string.IsNullOrWhiteSpace(prod?.Olcu) ? "Yeni" : prod!.Olcu!.Trim());
                                await CustomerFinanceHelper.AddTransactionAsync(
                                    _db,
                                    tenantId,
                                    customerId,
                                    sale.BranchId,
                                    groupCode: "ZIYNET",
                                    itemName: NormalizeZiynetName(prod, si.ProductName),
                                    itemType: NormalizeZiynetFinanceTip(
                                        NormalizeZiynetName(prod, si.ProductName),
                                        rawTip),
                                    quantity: Math.Abs(si.Quantity),
                                    direction: +1,
                                    gram: null,
                                    ayar: si.Karat,
                                    milyem: null,
                                    hasEq: null,
                                    unitPriceTl: si.UnitPrice,
                                    totalPriceTl: si.LineTotal,
                                    refType: "SALE",
                                    refId: sale.Id,
                                    note: $"Satis L{si.LineNo} (ziynet teslim edilmeyen)",
                                    txDate: sale.CreatedAt,
                                    ct: ct,
                                    batchId: batchId);
                                continue;
                            }

                            var milyem = CustomerFinanceHelper.MilyemFromAyar(si.Karat);
                            var hasEq = hasRate > 0 ? Math.Round(si.LineTotal / hasRate, 6, MidpointRounding.AwayFromZero) : Math.Round(si.Quantity * milyem, 6);
                            await CustomerFinanceHelper.AddTransactionAsync(
                                _db,
                                tenantId,
                                customerId,
                                sale.BranchId,
                                groupCode: "ISCILIKLI",
                                itemName: string.IsNullOrWhiteSpace(si.ProductName) ? si.ProductCode : si.ProductName,
                                itemType: null,
                                quantity: si.Quantity,
                                direction: +1,
                                gram: si.Quantity,
                                ayar: si.Karat,
                                milyem: milyem,
                                hasEq: hasEq,
                                unitPriceTl: si.UnitPrice,
                                totalPriceTl: si.LineTotal,
                                refType: "SALE",
                                refId: sale.Id,
                                note: $"Satis L{si.LineNo} (emanet)",
                                txDate: sale.CreatedAt,
                                ct: ct,
                                batchId: batchId);
                        }
                        bal.UpdatedAt = DateTime.UtcNow;
                    }

                    if (veresiyeToplam > 0)
                    {
                        // Karma ödeme + veresiye: sadece doviz bakiyesine borç yaz.
                        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
                        foreach (var vp in payments.Where(x => string.Equals(x.Method, "Veresiye", StringComparison.OrdinalIgnoreCase)))
                        {
                            await ApplyVeresiyePaymentToCustomerAsync(
                                bal, tenantId, customerId, sale.BranchId, sale.Id, vp,
                                note: "Karma odeme veresiye kismi",
                                txDate: sale.CreatedAt, batchId, ct);
                        }
                        bal.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            if (sale.CustomerId.HasValue)
            {
                await AddCustomerRecentSaleAuditAsync(
                    tenantId,
                    sale.CustomerId.Value,
                    sale.BranchId,
                    sale.Id,
                    grandTot,
                    sale.CreatedAt,
                    ct);
            }

            if (takasToplam > 0 && req.TakasHammadde is not null)
            {
                var takasMusteriAdi = await _db.Customers.AsNoTracking()
                    .Where(c => c.Id == sale.CustomerId!.Value && c.TenantId == tenantId && !c.IsDeleted)
                    .Select(c => c.FullName)
                    .FirstOrDefaultAsync(ct);
                var satisNoKisa = sale.Id.ToString("N")[..8].ToUpperInvariant();
                var takasNote = $"Takas hurda alis | Musteri: {(string.IsNullOrWhiteSpace(takasMusteriAdi) ? "-" : takasMusteriAdi)} | Satis No: {satisNoKisa}";

                // Karma ödemede de takas hurdası mutlaka hurda alış kaydına (Purchase + hurda stok/defter) yazılır.
                var takasScrap = await _scrap.RecordCustomerScrapPurchaseAsync(
                    tenantId,
                    sale.BranchId,
                    sale.UserId,
                    sale.CustomerId!.Value,
                    req.TakasHammadde.Ayar,
                    req.TakasHammadde.Gram,
                    req.TakasHammadde.BirimMaliyet,
                    (int)kuyumcu_domain.Enums.PurchasePaymentMethod.Emanet,
                    takasNote,
                    ct);
                if (!takasScrap.ok)
                    return BadRequest(new { error = takasScrap.error ?? "Takas hurdası kaydedilemedi." });

                var ayar = req.TakasHammadde.Ayar.Trim().ToUpperInvariant();
                var depo = await _db.DepoStoklar
                    .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == sale.BranchId && x.Ayar == ayar && !x.IsDeleted, ct);
                if (depo is null)
                {
                    depo = new DepoStok
                    {
                        TenantId = tenantId,
                        BranchId = sale.BranchId,
                        Ayar = ayar,
                        TotalGram = 0m,
                        BarcodedGram = 0m,
                        UnbarcodedGram = 0m,
                        OrtalamaMaliyet = req.TakasHammadde.BirimMaliyet
                    };
                    _db.DepoStoklar.Add(depo);
                }
                depo.Add(req.TakasHammadde.Gram, req.TakasHammadde.BirimMaliyet);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            CalcTotals(sale, out subtotal, out discTot, out taxTot, out grandTot); // Toplamları hesapla
            return CreatedAtAction(nameof(GetById), new { id = sale.Id }, ToDto(sale, subtotal, discTot, taxTot, grandTot));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw; // CS0161 hatasını çözer.

        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var s = await _db.Sales.AsNoTracking()
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);

        if (s is null) return NotFound();

        CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
        return Ok(ToDto(s, sub, disc, tax, grand));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleReqV2 req, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var s = await _db.Sales.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (s is null) return NotFound();

        var branchOk = await _db.Branches.AsNoTracking()
            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

        if (req.CustomerId.HasValue)
        {
            var custOk = await _db.Customers.AsNoTracking()
                .AnyAsync(c => c.Id == req.CustomerId.Value && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Eski kalemleri stoktan geri al; Tekil = ProductItem stoğa, Ziynet = StokMiktari +=
            var previousBranchId = s.BranchId;
            foreach (var old in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == previousBranchId && p.ProductCode == old.ProductCode, ct);
                if (product is null) continue;

                await _stock.AdjustAsync(
                    branchId: previousBranchId,
                    productId: product.Id,
                    productItemId: old.ProductItemId,
                    deltaQuantity: +old.Quantity,
                    refKind: StockRefKind.Sale,
                    refId: s.Id,
                    note: "Sale update (revert old)",
                    ct: ct
                );

                if (old.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == old.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = true;
                        pItem.UpdatedAt = DateTime.UtcNow;
                        var (undoOk, undoErr) = await DepoStokGramHelper.TryUndoBarcodedProductSoldAsync(_db, tenantId, previousBranchId, pItem, ct);
                        if (!undoOk)
                        {
                            await tx.RollbackAsync(ct);
                            return BadRequest(new { error = undoErr });
                        }
                    }
                }
                else
                {
                    var prodTracked = await _db.Products.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == previousBranchId && p.ProductCode == old.ProductCode, ct);
                    if (prodTracked is not null &&
                        ((prodTracked.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet
                        || prodTracked.IsSpecialProduct))
                        prodTracked.StokMiktari = (prodTracked.StokMiktari ?? 0) + (int)Math.Round(old.Quantity);
                }
            }

            // Update'te eski döviz etkisini geri ekle.
            await ApplyForexVaultAdjustmentAsync(
                tenantId,
                previousBranchId,
                s.Id,
                s.Items,
                sign: +1m,
                description: "Doviz satisi geri alim (update)",
                ct: ct,
                useCashTransactionHistory: true);

            await ApplySilverVaultAdjustmentAsync(
                tenantId,
                previousBranchId,
                s.Id,
                s.Items,
                sign: +1m,
                description: "Gumus satisi geri alim (update)",
                ct: ct,
                useCashTransactionHistory: true);

            s.BranchId = req.BranchId;
            s.CustomerId = req.CustomerId;

            s.Items.Clear();
            int lineNo = 0;
            // 2. Yeni kalemleri oluştur
            foreach (var it in (req.Items ?? new()))
            {
                if (string.IsNullOrWhiteSpace(it.ProductCode))
                    return BadRequest(new { error = "ProductCode zorunludur." });

                var isVirtualForexLine = IsVirtualForexSaleItem(it.ProductCode, it.Category);
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == req.BranchId && p.ProductCode == it.ProductCode, ct);
                if (!isVirtualForexLine && product is null)
                    return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });

                // --- ProductItem Kontrolü ---
                if (it.ProductItemId.HasValue)
                {
                    var itemId = it.ProductItemId.Value;
                    var itemEntity = await _db.ProductItems
                        .FirstOrDefaultAsync(pi => pi.Id == itemId && pi.TenantId == tenantId, ct);

                    if (itemEntity is null || itemEntity.BranchId != req.BranchId || !itemEntity.IsInStock)
                        return BadRequest(new { error = $"Geçersiz veya stokta olmayan ProductItemId: {itemId}" });

                    if (it.Quantity != itemEntity.Weight)
                        return BadRequest(new { error = $"Tekil parça (ID: {itemId}) satılırken miktar ({it.Quantity}) parça ağırlığına ({itemEntity.Weight}) eşit olmalıdır." });
                }
                // --- Kontrol Sonu ---


                var lineBase = it.UnitPrice * it.Quantity;
                var afterDisc = lineBase - it.Discount;
                var lineTax = afterDisc * it.TaxRate;
                var lineTotal = Math.Round(afterDisc + lineTax, 2);

                s.Items.Add(new SaleItem
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SaleId = s.Id,
                    LineNo = it.LineNo ?? ++lineNo,
                    Kind = ResolveSaleItemKind(isVirtualForexLine, product, it.Category, it.Karat, it.ProductName),
                    ProductCode = it.ProductCode.Trim(),
                    ProductName = it.ProductName?.Trim() ?? "",
                    Karat = it.Karat?.Trim() ?? "",
                    Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
                    Quantity = it.Quantity,
                    UnitPrice = it.UnitPrice,
                    Discount = it.Discount,
                    TaxRate = it.TaxRate,
                    LineTotal = lineTotal,
                    ProductItemId = it.ProductItemId
                });
            }

            await _db.SaveChangesAsync(ct);

            // 3. Yeni kalemleri stoktan düş; Tekil = ProductItem satıldı, Ziynet = StokMiktari -=
            foreach (var si in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == s.BranchId && p.ProductCode == si.ProductCode, ct);
                if (product is null) continue;

                await _stock.AdjustAsync(
                    branchId: s.BranchId,
                    productId: product.Id,
                    productItemId: si.ProductItemId,
                    deltaQuantity: -si.Quantity,
                    refKind: StockRefKind.Sale,
                    refId: s.Id,
                    note: "Sale update (apply)",
                    ct: ct
                );

                if (si.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == si.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = false;
                        pItem.UpdatedAt = DateTime.UtcNow;
                        var (depoOk, depoErr) = await DepoStokGramHelper.TryApplyBarcodedProductSoldAsync(_db, tenantId, s.BranchId, pItem, ct);
                        if (!depoOk)
                        {
                            await tx.RollbackAsync(ct);
                            return BadRequest(new { error = depoErr });
                        }
                    }
                }
                else
                {
                    var prodTracked = await _db.Products.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == s.BranchId && p.ProductCode == si.ProductCode, ct);
                    if (prodTracked is not null &&
                        ((prodTracked.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet
                        || prodTracked.IsSpecialProduct))
                        prodTracked.StokMiktari = Math.Max(0, (prodTracked.StokMiktari ?? 0) - (int)Math.Round(si.Quantity));
                }
            }

            // Update'te yeni döviz etkisini tekrar uygula.
            await ApplyForexVaultAdjustmentAsync(
                tenantId,
                s.BranchId,
                s.Id,
                s.Items,
                sign: -1m,
                description: "Doviz satisi kasa cikisi (update)",
                ct: ct);

            await ApplySilverVaultAdjustmentAsync(
                tenantId,
                s.BranchId,
                s.Id,
                s.Items,
                sign: -1m,
                description: "Gumus satisi kasa cikisi (update)",
                ct: ct);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
            return Ok(ToDto(s, sub, disc, tax, grand));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    [HttpPost("{id:guid}/reverse")]
    public async Task<IActionResult> Reverse(Guid id, [FromBody] ReverseSaleReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var sale = await _db.Sales.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (sale is null) return NotFound();
        var (userId, userName) = ResolveUser();
        var result = await _reversal.ReverseSaleAsync(
            tenantId, id, sale.CustomerId, null, req.Reason ?? "", userId, userName, ct);
        if (!result.Ok) return BadRequest(new { error = result.Error });
        return Ok(new { ok = true, reversalLogId = result.ReversalLogId, batchId = result.BatchId });
    }

    public sealed record ReverseSaleReq(string Reason);

    private (Guid? userId, string? userName) ResolveUser()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals(ClaimTypes.NameIdentifier, StringComparison.OrdinalIgnoreCase) ||
            c.Type.Equals("sub", StringComparison.OrdinalIgnoreCase))?.Value;
        Guid? userId = Guid.TryParse(claim, out var g) ? g : null;
        var name = User?.Claims?.FirstOrDefault(c => c.Type.Equals("full_name", StringComparison.OrdinalIgnoreCase))?.Value
                   ?? User?.Identity?.Name;
        return (userId, name);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();

        var s = await _db.Sales.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (s is null) return NotFound();
        if (s.IsDeleted) return NoContent();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            s.IsDeleted = true;
            await _db.SaveChangesAsync(ct);

            foreach (var it in s.Items)
            {
                var product = await _db.Products.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == s.BranchId && p.ProductCode == it.ProductCode, ct);
                if (product is null) continue;

                await _stock.AdjustAsync(
                    branchId: s.BranchId,
                    productId: product.Id,
                    productItemId: it.ProductItemId,
                    deltaQuantity: +it.Quantity,
                    refKind: StockRefKind.Sale,
                    refId: s.Id,
                    note: "Sale deleted",
                    ct: ct
                );

                if (it.ProductItemId.HasValue)
                {
                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == it.ProductItemId.Value, ct);
                    if (pItem is not null)
                    {
                        pItem.IsInStock = true;
                        pItem.UpdatedAt = DateTime.UtcNow;
                        var (undoOk, undoErr) = await DepoStokGramHelper.TryUndoBarcodedProductSoldAsync(_db, tenantId, s.BranchId, pItem, ct);
                        if (!undoOk)
                        {
                            await tx.RollbackAsync(ct);
                            return BadRequest(new { error = undoErr });
                        }
                    }
                }
                else
                {
                    var prodTracked = await _db.Products.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.BranchId == s.BranchId && p.ProductCode == it.ProductCode, ct);
                    if (prodTracked is not null &&
                        ((prodTracked.InventoryType ?? kuyumcu_domain.Enums.InventoryType.Tekil) == kuyumcu_domain.Enums.InventoryType.Ziynet
                        || prodTracked.IsSpecialProduct))
                        prodTracked.StokMiktari = (prodTracked.StokMiktari ?? 0) + (int)Math.Round(it.Quantity);
                }
            }

            // Satış silinince döviz kasası eski haline gelir.
            await ApplyForexVaultAdjustmentAsync(
                tenantId,
                s.BranchId,
                s.Id,
                s.Items,
                sign: +1m,
                description: "Doviz satisi geri alim (delete)",
                ct: ct,
                useCashTransactionHistory: true);

            await ApplySilverVaultAdjustmentAsync(
                tenantId,
                s.BranchId,
                s.Id,
                s.Items,
                sign: +1m,
                description: "Gumus satisi geri alim (delete)",
                ct: ct,
                useCashTransactionHistory: true);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return NoContent();
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>Tedarikçi veresiye: kasa/banka hareketi yok; tedarikçi cari bakiyesine yazılır (SupplierTransactionsController ile uyumlu işaret).</summary>
    private async Task ApplyTedarikciVeresiyeToSupplierAsync(
        Guid tenantId,
        Guid supplierId,
        Guid branchId,
        List<NormalizedPayment> tedarikciVeresiyeRows,
        Guid saleId,
        CancellationToken ct)
    {
        if (tedarikciVeresiyeRows.Count == 0) return;

        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(s => s.Id == supplierId && s.TenantId == tenantId && !s.IsDeleted, ct);
        if (supplier is null) return;

        var rates = _rates.GetUnitToTlRates();
        if (!rates.TryGetValue("TL", out var tlRate) || tlRate <= 0)
            tlRate = 1m;

        foreach (var p in tedarikciVeresiyeRows)
        {
            var targetAmount = decimal.Round(p.AmountTl, 6, MidpointRounding.AwayFromZero);
            if (targetAmount <= 0) continue;

            var bal = await _db.SupplierBalances
                .FirstOrDefaultAsync(x => x.SupplierId == supplier.Id && x.TenantId == tenantId && !x.IsDeleted, ct);
            if (bal is null)
            {
                bal = new SupplierBalance
                {
                    TenantId = tenantId,
                    SupplierId = supplier.Id
                };
                _db.SupplierBalances.Add(bal);
            }

            // COLLECTION: SupplierTransactionsController — tedarikçiden tahsilat yönü (+BalanceTL).
            bal.BalanceTL += targetAmount;
            bal.UpdatedAt = DateTime.UtcNow;

            _db.SupplierTransactions.Add(new SupplierTransaction
            {
                TenantId = tenantId,
                SupplierId = supplier.Id,
                BranchId = branchId,
                TxType = "COLLECTION",
                SourceUnit = "TL",
                SourceAmount = targetAmount,
                TargetUnit = "TL",
                TargetAmount = targetAmount,
                IsConverted = false,
                SourceUnitTlRate = tlRate,
                TargetUnitTlRate = tlRate,
                Description = $"Satış tedarikçi veresiye (SALE {saleId})",
                TxDate = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private sealed class NormalizedPayment
    {
        public string Method { get; init; } = "";
        /// <summary>Seçilen birimdeki miktar (USD/EUR/HAS/TL).</summary>
        public decimal Amount { get; init; }
        /// <summary>TL karşılığı.</summary>
        public decimal AmountTl { get; init; }
        public string Currency { get; init; } = "TL";
        public string? Account { get; init; }
        public string? LedgerSide { get; init; }
    }

    private static List<NormalizedPayment> NormalizePayments(
        List<SalePaymentReq>? raw,
        string fallbackMethod,
        decimal grandTot,
        IReadOnlyDictionary<string, decimal> unitRates,
        IReadOnlyDictionary<string, decimal> sellUnitRates)
    {
        var list = new List<NormalizedPayment>();
        if (raw != null)
        {
            foreach (var p in raw)
            {
                var method = NormalizeMethod(p.Method);
                var currency = NormalizeCurrency(p.Currency, method);
                var rate = unitRates.TryGetValue(currency, out var r) && r > 0 ? r : 1m;
                var amountTl = Math.Round(p.TlAmount.GetValueOrDefault(), 2, MidpointRounding.AwayFromZero);
                var amount = Math.Round(p.Amount, 4, MidpointRounding.AwayFromZero);
                // Öncelik her zaman client'tan gelen TL tutarında (toplam doğrulaması bununla yapılır).
                if (amountTl <= 0 && amount > 0)
                    amountTl = Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
                if (amount <= 0 && amountTl > 0)
                    amount = currency == "TL"
                        ? amountTl
                        : Math.Round(amountTl / rate, 4, MidpointRounding.AwayFromZero);

                // Veresiye seçili birim TL değilse, müşteri finansına yazılacak miktar
                // daima seçili birimin satış kuruna göre (TL / satış kuru) hesaplanır.
                if (string.Equals(method, "Veresiye", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(currency, "TL", StringComparison.OrdinalIgnoreCase) &&
                    amountTl > 0m)
                {
                    var sellRate = sellUnitRates.TryGetValue(currency, out var sr) && sr > 0m ? sr : rate;
                    if (sellRate > 0m)
                        amount = Math.Round(amountTl / sellRate, 4, MidpointRounding.AwayFromZero);
                }

                list.Add(new NormalizedPayment
                {
                    Method = method,
                    Amount = amount,
                    AmountTl = amountTl,
                    Currency = currency,
                    Account = string.IsNullOrWhiteSpace(p.Account) ? null : p.Account.Trim(),
                    LedgerSide = CustomerFinanceHelper.NormalizeLedgerSide(p.LedgerSide)
                });
            }
        }

        if (list.Count == 0 && grandTot > 0)
        {
            var method = NormalizeMethod(fallbackMethod);
            list.Add(new NormalizedPayment
            {
                Method = method,
                Amount = Math.Round(grandTot, 2, MidpointRounding.AwayFromZero),
                AmountTl = Math.Round(grandTot, 2, MidpointRounding.AwayFromZero),
                Currency = NormalizeCurrency(null, method),
                Account = null
            });
        }
        return list;
    }

    private static string NormalizeMethod(string? method)
    {
        var m = (method ?? "").Trim().ToUpperInvariant();
        return m switch
        {
            "NAKIT" => "Nakit",
            "KART" or "KREDI KARTI" or "KREDİ KARTI" => "Kart",
            "VERESIYE" or "VERESİYE" => "Veresiye",
            "IBAN" => "IBAN",
            "TAKAS" => "Takas",
            "EURO" or "EUR" => "Euro",
            "USD" => "USD",
            "GBP" or "POUND" => "GBP",
            "TEDARIKCIVERESIYE" or "TEDARİKÇİVERESİYE" or "TEDARIKCI_VERESIYE" => "TedarikciVeresiye",
            _ => string.IsNullOrWhiteSpace(method) ? "Nakit" : method.Trim()
        };
    }

    private static string NormalizeDeliveryType(string? raw)
    {
        var v = (raw ?? "").Trim().ToUpperInvariant();
        return v switch
        {
            "EMANET" => "EMANET",
            _ => "TESLIM"
        };
    }

    private static string NormalizeCurrency(string? currency, string method)
    {
        var c = (currency ?? "").Trim().ToUpperInvariant();
        if (method == "USD") return "USD";
        if (method == "Euro") return "EUR";
        if (method == "GBP") return "GBP";
        return c switch
        {
            "USD" => "USD",
            "EUR" or "EURO" => "EUR",
            "GBP" or "POUND" => "GBP",
            "HAS" or "GOLD" => "HAS",
            "GUMUS" or "GÜMÜŞ" or "SILVER" => "GUMUS",
            _ => "TL"
        };
    }

    private static string ResolveLedgerType(string method)
    {
        return method switch
        {
            "Nakit" => "Kasa",
            "Kart" => "PosBanka",
            "IBAN" => "Banka",
            "USD" or "Euro" or "GBP" => "Vault",
            "Veresiye" => "Veresiye",
            "Takas" => "Takas",
            "TedarikciVeresiye" => "TedarikciVeresiye",
            _ => "Kasa"
        };
    }

    private static string NormalizeZiynetName(Product? product, string fallbackProductName)
    {
        var source = (product?.ZiynetTipi ?? product?.Name ?? fallbackProductName ?? "").Trim().ToUpperInvariant();
        var fallback = (fallbackProductName ?? "").Trim().ToUpperInvariant();
        if (source.Contains("KÜLÇE") || source.Contains("KULCE"))
            return "GRAM ALTIN(KÜLÇE)";
        if ((source.Contains("22 AYAR") || source.Contains("22AYAR")) &&
            (source.Contains("GR") || source.Contains("GRAM")))
            return "22 AYAR(GR)";
        if (fallback.Contains("KÜLÇE") || fallback.Contains("KULCE"))
            return "GRAM ALTIN(KÜLÇE)";
        if (source == "GRAM" || source.Contains("GRAM ALTIN (HAS)") || source.Contains("GRAM ALTIN(HAS)"))
            return "GRAM ALTIN(KÜLÇE)";
        if (source.Contains("ÇEYREK") || source.Contains("CEYREK")) return "ÇEYREK";
        if (source.Contains("YARIM")) return "YARIM";
        if (source.Contains("TAM")) return "TAM";
        if (source.Contains("GRAM")) return "GRAM";
        if (source.Contains("ATA5")) return "ATA5";
        if (source.Contains("ATA")) return "ATA";
        if (source.Contains("CUMHURIYET") || source.Contains("CUMHURİYET")) return "CUMHURIYET";
        return string.IsNullOrWhiteSpace(source) ? "ZIYNET" : source;
    }

    private static string NormalizeZiynetFinanceTip(string itemName, string? rawTip)
    {
        var name = (itemName ?? "").Trim().ToUpperInvariant();
        if (name == "GRAM ALTIN(KÜLÇE)" || name == "GRAM ALTIN(KULCE)")
            return "Yeni";
        return string.IsNullOrWhiteSpace(rawTip) ? "Yeni" : rawTip.Trim();
    }

    private static bool IsHasAltinZiynetSaleItem(Product? product, SaleItem item)
    {
        if (CustomerFinanceHelper.ShouldRouteHasAltinToDovizBalance(
                item.ProductName,
                item.Category,
                item.Karat,
                product?.Name,
                product?.Category,
                product?.Karat,
                product?.ZiynetTipi))
            return true;

        var normalized = NormalizeZiynetName(product, item.ProductName);
        return CustomerFinanceHelper.IsHasAltinZiynetAd(normalized);
    }

    private static async Task<bool> TryApplyHasAltinSaleLedgerAsync(
        AppDbContext db,
        Guid tenantId,
        Guid customerId,
        Sale sale,
        SaleItem si,
        Product? prod,
        CustomerBalance bal,
        int direction,
        string noteSuffix,
        Guid batchId,
        CancellationToken ct)
    {
        if (!IsHasAltinZiynetSaleItem(prod, si))
            return false;

        var hasQty = Math.Abs(si.Quantity);
        if (hasQty <= 0m)
            return true;

        bal.BalanceHAS += direction * hasQty;
        await CustomerFinanceHelper.AddTransactionAsync(
            db,
            tenantId,
            customerId,
            sale.BranchId,
            groupCode: "DOVIZ",
            itemName: "HAS",
            itemType: null,
            quantity: hasQty,
            direction: direction >= 0 ? +1 : -1,
            gram: hasQty,
            ayar: "HAS",
            milyem: CustomerFinanceHelper.MilyemFromAyar(si.Karat),
            hasEq: hasQty,
            unitPriceTl: si.UnitPrice,
            totalPriceTl: si.LineTotal,
            refType: "SALE",
            refId: sale.Id,
            note: $"Satis L{si.LineNo} ({noteSuffix})",
            txDate: sale.CreatedAt,
            ct: ct,
            batchId: batchId);
        return true;
    }

    private async Task AddCustomerRecentSaleAuditAsync(
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        Guid saleId,
        decimal grandTotalTl,
        DateTime txDate,
        CancellationToken ct)
    {
        var totalRounded = Math.Round(Math.Abs(grandTotalTl), 2, MidpointRounding.AwayFromZero);
        await CustomerFinanceHelper.AddTransactionAsync(
            _db,
            tenantId,
            customerId,
            branchId,
            groupCode: "AUDIT",
            itemName: "SALE_EVENT",
            itemType: null,
            quantity: totalRounded,
            direction: -1,
            gram: null,
            ayar: "TL",
            milyem: null,
            hasEq: null,
            unitPriceTl: 1m,
            totalPriceTl: totalRounded,
            refType: "SALE",
            refId: saleId,
            note: $"Satış işlemi kaydı (SALE {saleId})",
            txDate: txDate,
            ct: ct,
            cariDurumOverride: "Finans");
    }

    private async Task ApplyVeresiyePaymentToCustomerAsync(
        CustomerBalance bal,
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        Guid saleId,
        NormalizedPayment vp,
        string note,
        DateTime txDate,
        Guid batchId,
        CancellationToken ct)
    {
        var unit = NormalizeForexCurrency(vp.Currency).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(unit)) unit = "TL";
        var amount = Math.Round(vp.Amount, 4, MidpointRounding.AwayFromZero);
        if (amount <= 0m) return;

        var ledgerSide = CustomerFinanceHelper.NormalizeLedgerSide(vp.LedgerSide);
        if (string.IsNullOrEmpty(ledgerSide))
        {
            var (grossBorc, grossAlacak) = await GetCustomerDovizGrossAsync(tenantId, customerId, branchId, unit, ct);
            ledgerSide = CustomerFinanceHelper.ResolveVeresiyeLedgerSideAuto(grossBorc, grossAlacak);
        }

        if (CustomerFinanceHelper.IsLedgerAlacak(ledgerSide))
        {
            var (direction, refType, balanceDelta) = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, amount);
            ApplyBalanceDeltaByUnit(bal, unit, balanceDelta);
            await CustomerFinanceHelper.AddTransactionAsync(
                _db,
                tenantId,
                customerId,
                branchId,
                groupCode: "DOVIZ",
                itemName: unit,
                itemType: null,
                quantity: amount,
                direction: direction,
                gram: null,
                ayar: unit == "TL" ? null : unit,
                milyem: null,
                hasEq: null,
                unitPriceTl: unit == "TL" ? 1m : null,
                totalPriceTl: vp.AmountTl,
                refType: refType,
                refId: saleId,
                note: note,
                txDate: txDate,
                ct: ct,
                cariDurumOverride: "Alacakli",
                batchId: batchId);
        }
        else
        {
            var (direction, cariDurum, balanceDelta) = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, amount);
            ApplyBalanceDeltaByUnit(bal, unit, balanceDelta);
            await CustomerFinanceHelper.AddTransactionAsync(
                _db,
                tenantId,
                customerId,
                branchId,
                groupCode: "DOVIZ",
                itemName: unit,
                itemType: null,
                quantity: amount,
                direction: direction,
                gram: null,
                ayar: unit == "TL" ? null : unit,
                milyem: null,
                hasEq: null,
                unitPriceTl: unit == "TL" ? 1m : null,
                totalPriceTl: vp.AmountTl,
                refType: "SALE",
                refId: saleId,
                note: note,
                txDate: txDate,
                ct: ct,
                cariDurumOverride: cariDurum,
                batchId: batchId);
        }
    }

    private async Task<(decimal Borc, decimal Alacak)> GetCustomerDovizGrossAsync(
        Guid tenantId, Guid customerId, Guid branchId, string unit, CancellationToken ct)
    {
        var normalizedUnit = NormalizeForexCurrency(unit).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedUnit)) normalizedUnit = "TL";

        var rows = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                        && !x.IsDeleted && !x.IsReversed && x.GroupCode == "DOVIZ"
                        && x.ItemName == normalizedUnit)
            .ToListAsync(ct);

        if (normalizedUnit == "HAS")
        {
            var hasZiynetRows = await _db.CustomerTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                            && !x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET")
                .ToListAsync(ct);
            var misclassified = hasZiynetRows
                .Where(x => CustomerFinanceHelper.IsHasAltinZiynetAd(
                    CustomerFinanceHelper.NormalizeZiynetItemName(x.ItemName)))
                .ToList();
            rows = rows.Concat(misclassified).ToList();
        }

        return CustomerFinanceHelper.ComputeGrossColumns(rows);
    }

    private static void ApplyBalanceDeltaByUnit(CustomerBalance bal, string unit, decimal delta)
    {
        var u = (unit ?? "TL").Trim().ToUpperInvariant();
        switch (u)
        {
            case "USD":
                bal.BalanceUSD += delta;
                break;
            case "GBP":
            case "POUND":
            case "STERLIN":
            case "STERLİN":
                bal.BalanceGBP += delta;
                break;
            case "EUR":
            case "EURO":
                bal.BalanceEUR += delta;
                break;
            case "HAS":
                bal.BalanceHAS += delta;
                break;
            case "GUMUS":
                // CustomerBalance tablosunda henüz ayrı GUMUS kolonu yok.
                // Gümüş bakiye DOVIZ işlem satırlarından hesaplanır.
                break;
            default:
                bal.BalanceTL += delta;
                break;
        }
    }

    private async Task ApplyUndeliveredMetalCustomerCreditAsync(
        Guid tenantId,
        Guid customerId,
        Guid branchId,
        Guid saleId,
        IEnumerable<SaleItem> items,
        IReadOnlyDictionary<int, decimal> deliveredQtyByLineNo,
        IReadOnlyDictionary<string, string>? emanetDovizLedgerByUnit,
        Guid batchId,
        DateTime txDate,
        CancellationToken ct)
    {
        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
        var changed = false;
        foreach (var si in items)
        {
            if (!deliveredQtyByLineNo.TryGetValue(si.LineNo, out var delivered))
                continue;

            var isDoviz = string.Equals((si.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase);
            var isGumus = string.Equals((si.Category ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase)
                          || si.Kind == ItemKind.Silver;
            if (!isDoviz && !isGumus)
                continue;

            var total = Math.Round(Math.Abs(si.Quantity), 4, MidpointRounding.AwayFromZero);
            var teslim = Math.Max(0m, Math.Min(delivered, total));
            var kalan = Math.Round(total - teslim, 4, MidpointRounding.AwayFromZero);
            if (kalan <= 0m)
                continue;

            string unit;
            string note;
            if (isGumus)
            {
                unit = "GUMUS";
                note = $"Satis L{si.LineNo} (gumus teslim edilmeyen)";
            }
            else
            {
                unit = NormalizeForexCurrency(string.IsNullOrWhiteSpace(si.Karat) ? si.ProductName : si.Karat).ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(unit) || unit == "TL")
                    continue;
                note = $"Satis L{si.LineNo} (doviz teslim edilmeyen)";
            }

            string? ledgerSide = null;
            emanetDovizLedgerByUnit?.TryGetValue(unit, out ledgerSide);
            if (string.IsNullOrEmpty(ledgerSide))
            {
                var (grossBorc, grossAlacak) = await GetCustomerDovizGrossAsync(tenantId, customerId, branchId, unit, ct);
                ledgerSide = CustomerFinanceHelper.ResolveEmanetLedgerSideAuto(grossBorc, grossAlacak);
            }

            await CustomerFinanceHelper.ApplyEmanetDovizLegAsync(
                _db, bal, tenantId, customerId, branchId,
                unit, kalan, ledgerSide,
                unitPriceTl: si.UnitPrice,
                totalPriceTl: Math.Round(si.UnitPrice * kalan, 2, MidpointRounding.AwayFromZero),
                gram: isGumus ? kalan : null,
                ayar: isGumus ? "Gümüş" : unit,
                hasEq: null,
                refType: "SALE", refId: saleId, note: note,
                txDate: txDate, batchId: batchId, ct: ct,
                applyBalanceDelta: ApplyBalanceDeltaByUnit);
            changed = true;
        }

        if (changed)
            bal.UpdatedAt = DateTime.UtcNow;
    }

    private async Task ApplySilverVaultAdjustmentAsync(
        Guid tenantId,
        Guid branchId,
        Guid saleId,
        IEnumerable<SaleItem> items,
        decimal sign,
        string description,
        CancellationToken ct,
        IReadOnlyDictionary<int, decimal>? deliveredQtyByLineNo = null,
        bool useCashTransactionHistory = false)
    {
        decimal qty;
        if (useCashTransactionHistory)
        {
            qty = await _db.CashTransactions.AsNoTracking()
                .Where(x =>
                    x.TenantId == tenantId &&
                    x.BranchId == branchId &&
                    x.RefType == "SALE" &&
                    x.RefId == saleId &&
                    x.SourceModule == "Sale" &&
                    x.TxType == "Expense" &&
                    x.Currency == "GUMUS" &&
                    x.Description != null &&
                    x.Description.Contains("Gumus satisi"))
                .SumAsync(x => x.Amount, ct);
        }
        else
        {
            qty = items
                .Where(x => string.Equals((x.Category ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase)
                            || x.Kind == ItemKind.Silver)
                .Sum(x =>
                {
                    var total = Math.Abs(x.Quantity);
                    if (deliveredQtyByLineNo != null && deliveredQtyByLineNo.TryGetValue(x.LineNo, out var delivered))
                        return Math.Max(0m, Math.Min(delivered, total));
                    return total;
                });
        }

        if (qty <= 0m) return;

        var account = await GetOrCreateVaultAccountAsync(tenantId, branchId, "GUMUS", ct);
        var delta = sign * qty;
        account.CurrentBalance += delta;

        _db.CashTransactions.Add(new CashTransaction
        {
            TenantId = tenantId,
            BranchId = branchId,
            CashAccountId = account.Id,
            TxType = delta >= 0 ? "Income" : "Expense",
            SourceModule = "Sale",
            Currency = "GUMUS",
            Amount = qty,
            TxDate = DateTime.UtcNow,
            RefType = "SALE",
            RefId = saleId,
            Description = $"{description}: GUMUS"
        });
    }

    private async Task ApplyForexVaultAdjustmentAsync(
        Guid tenantId,
        Guid branchId,
        Guid saleId,
        IEnumerable<SaleItem> items,
        decimal sign,
        string description,
        CancellationToken ct,
        IReadOnlyDictionary<int, decimal>? deliveredQtyByLineNo = null,
        bool useCashTransactionHistory = false)
    {
        List<(string Currency, decimal Quantity)> dovizRows;
        if (useCashTransactionHistory)
        {
            dovizRows = await _db.CashTransactions.AsNoTracking()
                .Where(x =>
                    x.TenantId == tenantId &&
                    x.BranchId == branchId &&
                    x.RefType == "SALE" &&
                    x.RefId == saleId &&
                    x.SourceModule == "Sale" &&
                    x.TxType == "Expense" &&
                    x.Description != null &&
                    x.Description.Contains("Doviz satisi"))
                .GroupBy(x => x.Currency.ToUpperInvariant())
                .Select(g => new ValueTuple<string, decimal>(g.Key, g.Sum(x => x.Amount)))
                .ToListAsync(ct);
        }
        else
        {
            dovizRows = items
                .Where(x => string.Equals((x.Category ?? "").Trim(), "DOVIZ", StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                {
                    var total = Math.Abs(x.Quantity);
                    var vaultQty = total;
                    if (deliveredQtyByLineNo != null && deliveredQtyByLineNo.TryGetValue(x.LineNo, out var delivered))
                        vaultQty = Math.Max(0m, Math.Min(delivered, total));
                    return new
                    {
                        Currency = NormalizeForexCurrency(x.Karat),
                        Quantity = vaultQty
                    };
                })
                .Where(x => (x.Currency == "USD" || x.Currency == "EUR" || x.Currency == "GBP" || x.Currency == "GUMUS") && x.Quantity > 0)
                .GroupBy(x => x.Currency, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ValueTuple<string, decimal>(g.Key.ToUpperInvariant(), g.Sum(i => i.Quantity)))
                .ToList();
        }

        foreach (var row in dovizRows)
        {
            if (row.Quantity <= 0m) continue;
            var account = await GetOrCreateVaultAccountAsync(tenantId, branchId, row.Currency, ct);
            var delta = sign * row.Quantity;
            account.CurrentBalance += delta;

            _db.CashTransactions.Add(new CashTransaction
            {
                TenantId = tenantId,
                BranchId = branchId,
                CashAccountId = account.Id,
                TxType = delta >= 0 ? "Income" : "Expense",
                SourceModule = "Sale",
                Currency = row.Currency,
                Amount = row.Quantity,
                TxDate = DateTime.UtcNow,
                RefType = "SALE",
                RefId = saleId,
                Description = $"{description}: {row.Currency}"
            });
        }
    }

    private async Task<CashAccount> GetOrCreateVaultAccountAsync(Guid tenantId, Guid branchId, string currency, CancellationToken ct)
    {
        var cur = NormalizeForexCurrency(currency);
        var name = $"Vault {cur}";
        var acc = await _db.CashAccounts
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.AccountType == "Vault" &&
                x.Currency == cur &&
                x.Name == name &&
                !x.IsDeleted, ct);
        if (acc is not null) return acc;

        acc = new CashAccount
        {
            TenantId = tenantId,
            BranchId = branchId,
            AccountType = "Vault",
            Currency = cur,
            Name = name,
            CurrentBalance = 0m
        };
        _db.CashAccounts.Add(acc);
        return acc;
    }

    private static string NormalizeForexCurrency(string? raw)
    {
        var c = (raw ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            "EURO" => "EUR",
            _ => c
        };
    }

    private static bool IsVirtualForexSaleItem(string? productCode, string? category)
    {
        var code = (productCode ?? "").Trim().ToUpperInvariant();
        var cat = (category ?? "").Trim().ToUpperInvariant();
        return code.StartsWith("FX-", StringComparison.OrdinalIgnoreCase)
               || cat == "DOVIZ"
               || cat == "DÖVIZ"
               || cat == "DÖVİZ";
    }

    private static bool IsForexSaleItemLine(SaleItem item)
    {
        if (item.Kind == ItemKind.Forex) return true;
        if (IsVirtualForexSaleItem(item.ProductCode, item.Category)) return true;
        return IsForexCurrencyToken(item.Karat) || IsForexCurrencyToken(item.ProductName);
    }

    private static ItemKind ResolveSaleItemKind(
        bool isVirtualForexLine,
        Product? product,
        string? category,
        string? karat,
        string? productName)
    {
        if (isVirtualForexLine || IsForexCurrencyToken(karat) || IsForexCurrencyToken(productName))
            return ItemKind.Forex;

        if (product is not null)
        {
            if ((product.InventoryType ?? InventoryType.Tekil) == InventoryType.Ziynet)
                return ItemKind.Ziynet;
            if (product.IsSpecialProduct)
                return ItemKind.Product;
        }

        // Katalog eşleşmesi olmayan ziynet satırları (ör. teslim=0 emanet akışı, fallback kod)
        // kategori token'ına göre Ziynet sayılır → Raporlar Ziynet sekmesine düşer.
        if (IsZiynetCategoryToken(category) || IsZiynetCategoryToken(productName))
            return ItemKind.Ziynet;

        var cat = (category ?? product?.Category ?? string.Empty).Trim().ToUpperInvariant();
        if (cat.Contains("GUMUS", StringComparison.OrdinalIgnoreCase) || cat.Contains("GÜMÜŞ", StringComparison.OrdinalIgnoreCase))
            return ItemKind.Silver;

        var k = (karat ?? product?.Karat ?? string.Empty).Trim().ToUpperInvariant();
        if (k.Contains("HAS", StringComparison.OrdinalIgnoreCase) || k.Contains("24") || k.Contains("999"))
            return ItemKind.GramGold;
        if (k.Contains("22") || k.Contains("18") || k.Contains("14") || k.Contains("8"))
            return ItemKind.CraftedGold;

        return ItemKind.Product;
    }

    private static bool IsForexCurrencyToken(string? raw)
    {
        var v = NormalizeForexCurrency(raw);
        return v == "USD" || v == "EUR" || v == "GBP";
    }

    private static bool IsZiynetCategoryToken(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToUpperInvariant();
        if (v.Contains("ZIYNET") || v.Contains("ZİYNET")) return true;
        if (v.Contains("ÇEYREK") || v.Contains("CEYREK")) return true;
        if (v.Contains("YARIM")) return true;
        if (v.Contains("TAM")) return true;
        if (v.Contains("ATA5")) return true;
        if (v.Contains("ATA")) return true;
        if (v.Contains("GREMSE")) return true;
        if (v.Contains("22 AYAR") && (v.Contains("GR") || v.Contains("GRAM"))) return true;
        if (v.Contains("KÜLÇE") || v.Contains("KULCE")) return true;
        if (v == "GRAM" || v.Contains("GRAM ALTIN")) return true;
        if (v.Contains("HAS ALTIN") || v.Contains("HASALTIN"))
        {
            // Saf has altın ziynet adet defterine değil DOVIZ/HAS'a gider.
            if (!v.Contains("GRAM") && !v.Contains("KULCE") && !v.Contains("KÜLÇE")
                && !((v.Contains("22 AYAR") || v.Contains("22AYAR")) && (v.Contains("GR") || v.Contains("GRAM"))))
                return false;
        }
        return false;
    }

    // helpers
    private static void CalcTotals(Sale s, out decimal subtotal, out decimal disc, out decimal tax, out decimal grand)
    {
        subtotal = 0m; disc = 0m; tax = 0m; grand = 0m;
        foreach (var i in (s.Items ?? new List<SaleItem>()))
        {
            var lineBase = i.UnitPrice * i.Quantity;
            var afterDisc = lineBase - i.Discount;
            var t = afterDisc * i.TaxRate;
            var lineTot = Math.Round(afterDisc + t, 2);

            subtotal += Math.Round(lineBase, 2);
            disc += Math.Round(i.Discount, 2);
            tax += Math.Round(t, 2);
            grand += lineTot;
        }
    }

    // (DTO tanımı burada bulunmamaktadır, ancak ToDto metodu SaleItemDtoV2'ye bağlıdır)
    // SaleItemDtoV2'nin ProductItemId alanını döndürdüğünden emin olun
    private static SaleDtoV2 ToDto(Sale s, decimal subtotal, decimal disc, decimal tax, decimal grand)
        => new(
            s.Id, s.BranchId, s.UserId, s.CustomerId,
            Subtotal: subtotal,
            DiscountTotal: disc,
            TaxTotal: tax,
            GrandTotal: grand,
            CreatedAt: s.CreatedAt,
            Items: (s.Items ?? new()).Select(i =>
                new SaleItemDtoV2(
                    i.Id, i.SaleId, i.LineNo, i.ProductCode, i.ProductName, i.Karat, i.Category,
                    i.Quantity, i.UnitPrice, i.Discount, i.TaxRate, i.LineTotal,
                    i.ProductItemId // << Güncellendi
                )
            ).ToList()
        );
}
//using System.Security.Claims;
//using kuyumcu_application.Abstractions;
//using kuyumcu_domain.Entities;
//using kuyumcu_domain.Enums;
//using kuyumcu_infrastructure.Persistence;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;

//namespace KUYUMCU.Price_Service.Controllers;

//[ApiController]
//[Route("api/sales/v2")]
//[Authorize]
//public sealed class SalesV2Controller : ControllerBase
//{
//    private readonly AppDbContext _db;
//    private readonly IStockService _stock;

//    public SalesV2Controller(AppDbContext db, IStockService stock)
//    {
//        _db = db;
//        _stock = stock;
//    }

//    // -------- Tenant helper --------
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
//    [HttpPost]
//    public async Task<IActionResult> Create([FromBody] CreateSaleReqV2 req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        // user
//        var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//        if (!Guid.TryParse(uidStr, out var userId))
//            return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//        // branch (tenant ile)
//        var branch = await _db.Branches.AsNoTracking()
//            .FirstOrDefaultAsync(x => x.Id == req.BranchId && x.TenantId == tenantId, ct);
//        if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        // items
//        if (req.Items is null || req.Items.Count == 0)
//            return BadRequest(new { error = "En az bir satış kalemi zorunludur." });

//        // müşteri (opsiyonel) tenant kontrol
//        if (req.CustomerId is Guid cid)
//        {
//            var custOk = await _db.Customers.AsNoTracking()
//                .AnyAsync(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted, ct);
//            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
//        }

//        var sale = new Sale
//        {
//            Id = Guid.NewGuid(),
//            TenantId = tenantId,
//            BranchId = branch.Id,
//            UserId = userId,
//            CustomerId = req.CustomerId,
//            Items = new List<SaleItem>()
//        };


//        int lineNo = 0;
//        decimal subtotal = 0m, discTot = 0m, taxTot = 0m, grandTot = 0m;

//        foreach (var it in req.Items)
//        {
//            // ... Product/ProductCode kontrolü ...

//            // --- ProductItem / Barcode Kontrolü (YENİ EKLENDİ) ---
//            if (it.ProductItemId.HasValue)
//            {
//                var itemId = it.ProductItemId.Value;
//                var itemEntity = await _db.ProductItems
//                    .FirstOrDefaultAsync(pi => pi.Id == itemId && pi.TenantId == tenantId, ct);

//                if (itemEntity is null)
//                    return BadRequest(new { error = $"Geçersiz ProductItemId: {itemId}" });

//                if (itemEntity.BranchId != branch.Id)
//                    return BadRequest(new { error = $"ProductItemId {itemId} bu şubeye ait değil." });

//                if (!itemEntity.IsInStock)
//                    return BadRequest(new { error = $"ProductItemId {itemId} stokta değil/satılmış." });

//                // Tekil parça satılıyorsa, miktar parça ağırlığına eşit olmalıdır.
//                if (it.Quantity != itemEntity.Weight)
//                    return BadRequest(new { error = $"Tekil parça (ID: {itemId}) satılırken miktar ({it.Quantity}) parça ağırlığına ({itemEntity.Weight}) eşit olmalıdır." });
//            }
//            // --- ProductItem / Barcode Kontrolü SONU ---

//            // ... (Fiyat hesaplama) ...

//            sale.Items.Add(new SaleItem
//            {
//                // ... (diğer alanlar) ...
//                ProductItemId = it.ProductItemId // << Bağlantı Eklendi
//            });

//        }

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            _db.Sales.Add(sale);
//            _db.SaleItems.AddRange(sale.Items); // SaleItem'ları toplu ekleme
//            await _db.SaveChangesAsync(ct);

//            // STOK ÇIKIŞLARI ve ProductItem güncelleme
//            foreach (var si in sale.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
//                if (product is null) continue;

//                // Stok AdjustAsync çağrısı - ProductItemId gönderiliyor
//                await _stock.AdjustAsync(
//                    branchId: sale.BranchId,
//                    productId: product.Id,
//                    productItemId: si.ProductItemId, // << ProductItemId gönderildi
//                    deltaQuantity: -si.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: sale.Id,
//                    note: $"Sale {sale.Id} L{si.LineNo}",
//                    ct: ct
//                );

//                // ProductItem'ı satıldı olarak işaretle (YENİ EKLENDİ)
//                if (si.ProductItemId.HasValue)
//                {
//                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == si.ProductItemId.Value, ct);
//                    if (pItem is not null)
//                    {
//                        pItem.IsInStock = false;
//                        pItem.UpdatedAt = DateTime.UtcNow;
//                    }
//                }
//            }

//            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
//            await tx.CommitAsync(ct);

//            // ... (Response DTO dönme) ...
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;

//        }
//        return Ok();
//    }
//    //[HttpPost]
//    //public async Task<IActionResult> Create([FromBody] CreateSaleReqV2 req, CancellationToken ct)
//    //{
//    //    var tenantId = GetTenantId();

//    //    // user
//    //    var uidStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
//    //    if (!Guid.TryParse(uidStr, out var userId))
//    //        return Unauthorized(new { error = "Kullanıcı kimliği bulunamadı." });

//    //    // branch (tenant ile)
//    //    var branch = await _db.Branches.AsNoTracking()
//    //        .FirstOrDefaultAsync(x => x.Id == req.BranchId && x.TenantId == tenantId, ct);
//    //    if (branch is null) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//    //    // items
//    //    if (req.Items is null || req.Items.Count == 0)
//    //        return BadRequest(new { error = "En az bir satış kalemi zorunludur." });

//    //    // müşteri (opsiyonel) tenant kontrol
//    //    if (req.CustomerId is Guid cid)
//    //    {
//    //        var custOk = await _db.Customers.AsNoTracking()
//    //            .AnyAsync(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted, ct);
//    //        if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
//    //    }

//    //    var sale = new Sale
//    //    {
//    //        Id = Guid.NewGuid(),
//    //        TenantId = tenantId,
//    //        BranchId = branch.Id,
//    //        UserId = userId,
//    //        CustomerId = req.CustomerId,
//    //        ProductCode = "",
//    //        ProductName = "",
//    //        Karat = "",
//    //        Quantity = 0,
//    //        UnitPrice = 0,
//    //        TotalPrice = 0,
//    //        Items = new List<SaleItem>()
//    //    };

//    //    int lineNo = 0;
//    //    decimal subtotal = 0m, discTot = 0m, taxTot = 0m, grandTot = 0m;

//    //    foreach (var it in req.Items)
//    //    {
//    //        if (string.IsNullOrWhiteSpace(it.ProductCode))
//    //            return BadRequest(new { error = "ProductCode zorunludur." });

//    //        var product = await _db.Products.AsNoTracking()
//    //            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//    //        if (product is null)
//    //            return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });

//    //        var lineBase = it.UnitPrice * it.Quantity;
//    //        var afterDisc = lineBase - it.Discount;
//    //        var tax = afterDisc * it.TaxRate;
//    //        var lineTotal = Math.Round(afterDisc + tax, 2);

//    //        subtotal += Math.Round(lineBase, 2);
//    //        discTot += Math.Round(it.Discount, 2);
//    //        taxTot += Math.Round(tax, 2);
//    //        grandTot += lineTotal;

//    //        sale.Items.Add(new SaleItem
//    //        {
//    //            Id = Guid.NewGuid(),
//    //            TenantId = tenantId,
//    //            SaleId = sale.Id,
//    //            LineNo = it.LineNo ?? ++lineNo,
//    //            ProductCode = it.ProductCode.Trim(),
//    //            ProductName = it.ProductName?.Trim() ?? "",
//    //            Karat = it.Karat?.Trim() ?? "",
//    //            Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
//    //            Quantity = it.Quantity,
//    //            UnitPrice = it.UnitPrice,
//    //            Discount = it.Discount,
//    //            TaxRate = it.TaxRate,
//    //            LineTotal = lineTotal
//    //        });
//    //    }

//    //    await using var tx = await _db.Database.BeginTransactionAsync(ct);
//    //    try
//    //    {
//    //        _db.Sales.Add(sale);
//    //        await _db.SaveChangesAsync(ct);

//    //        // STOK ÇIKIŞLARI
//    //        foreach (var si in sale.Items)
//    //        {
//    //            var product = await _db.Products.AsNoTracking()
//    //                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
//    //            if (product is null) continue;

//    //            await _stock.AdjustAsync(
//    //                branchId: sale.BranchId,
//    //                productId: product.Id,
//    //                deltaQuantity: -si.Quantity,
//    //                refKind: StockRefKind.Sale,
//    //                refId: sale.Id,
//    //                note: $"Sale {sale.Id} L{si.LineNo}",
//    //                ct: ct
//    //            );
//    //        }

//    //        await tx.CommitAsync(ct);

//    //        return CreatedAtAction(nameof(GetById), new { id = sale.Id }, ToDto(sale, subtotal, discTot, taxTot, grandTot));
//    //    }
//    //    catch
//    //    {
//    //        await tx.RollbackAsync(ct);
//    //        throw;
//    //    }
//    //}

//    [HttpGet("{id:guid}")]
//    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var s = await _db.Sales.AsNoTracking()
//            .Include(x => x.Items)
//            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);

//        if (s is null) return NotFound();

//        CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
//        return Ok(ToDto(s, sub, disc, tax, grand));
//    }

//    [HttpPut("{id:guid}")]
//    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSaleReqV2 req, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var s = await _db.Sales.Include(x => x.Items)
//            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (s is null) return NotFound();

//        var branchOk = await _db.Branches.AsNoTracking()
//            .AnyAsync(b => b.Id == req.BranchId && b.TenantId == tenantId, ct);
//        if (!branchOk) return BadRequest(new { error = "Geçersiz BranchId/tenant." });

//        if (req.CustomerId.HasValue)
//        {
//            var custOk = await _db.Customers.AsNoTracking()
//                .AnyAsync(c => c.Id == req.CustomerId.Value && c.TenantId == tenantId && !c.IsDeleted, ct);
//            if (!custOk) return BadRequest(new { error = "Geçersiz CustomerId/tenant." });
//        }

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            // eski kalemleri stoktan geri al
//            foreach (var old in s.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == old.ProductCode, ct);
//                if (product is null) continue;

//                await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: +old.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale update (revert old)",
//                    ct: ct
//                );
//            }

//            s.BranchId = req.BranchId;
//            s.CustomerId = req.CustomerId;

//            s.Items.Clear();
//            int lineNo = 0;
//            foreach (var it in (req.Items ?? new()))
//            {
//                if (string.IsNullOrWhiteSpace(it.ProductCode))
//                    return BadRequest(new { error = "ProductCode zorunludur." });

//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//                if (product is null)
//                    return BadRequest(new { error = $"Geçersiz ProductCode: {it.ProductCode}" });

//                var lineBase = it.UnitPrice * it.Quantity;
//                var afterDisc = lineBase - it.Discount;
//                var lineTax = afterDisc * it.TaxRate;
//                var lineTotal = Math.Round(afterDisc + lineTax, 2);

//                s.Items.Add(new SaleItem
//                {
//                    Id = Guid.NewGuid(),
//                    TenantId = tenantId,
//                    SaleId = s.Id,
//                    LineNo = it.LineNo ?? ++lineNo,
//                    ProductCode = it.ProductCode.Trim(),
//                    ProductName = it.ProductName?.Trim() ?? "",
//                    Karat = it.Karat?.Trim() ?? "",
//                    Category = string.IsNullOrWhiteSpace(it.Category) ? null : it.Category.Trim(),
//                    Quantity = it.Quantity,
//                    UnitPrice = it.UnitPrice,
//                    Discount = it.Discount,
//                    TaxRate = it.TaxRate,
//                    LineTotal = lineTotal
//                });
//            }

//            await _db.SaveChangesAsync(ct);

//            // yeni kalemleri stoktan düş
//            foreach (var si in s.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == si.ProductCode, ct);
//                if (product is null) continue;

//                await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    deltaQuantity: -si.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale update (apply)",
//                    ct: ct
//                );
//            }

//            await tx.CommitAsync(ct);

//            CalcTotals(s, out var sub, out var disc, out var tax, out var grand);
//            return Ok(ToDto(s, sub, disc, tax, grand));
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }
//    [HttpDelete("{id:guid}")]
//    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    {
//        var tenantId = GetTenantId();

//        var s = await _db.Sales.Include(x => x.Items)
//            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//        if (s is null) return NotFound();
//        if (s.IsDeleted) return NoContent();

//        await using var tx = await _db.Database.BeginTransactionAsync(ct);
//        try
//        {
//            s.IsDeleted = true;
//            await _db.SaveChangesAsync(ct);

//            foreach (var it in s.Items)
//            {
//                var product = await _db.Products.AsNoTracking()
//                    .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//                if (product is null) continue;
//                           await _stock.AdjustAsync(
//                    branchId: s.BranchId,
//                    productId: product.Id,
//                    productItemId: it.ProductItemId, // << ProductItemId gönderildi
//                    deltaQuantity: +it.Quantity,
//                    refKind: StockRefKind.Sale,
//                    refId: s.Id,
//                    note: "Sale deleted",
//                    ct: ct
//                );

//                // ProductItem'ı stoğa geri al (YENİ EKLENDİ)
//                if (it.ProductItemId.HasValue)
//                {
//                    var pItem = await _db.ProductItems.FirstOrDefaultAsync(pi => pi.Id == it.ProductItemId.Value, ct);
//                    if (pItem is not null)
//                    {
//                        pItem.IsInStock = true;
//                        pItem.UpdatedAt = DateTime.UtcNow;
//                    }
//                }
//            }

//            await _db.SaveChangesAsync(ct); // ProductItem güncellemelerini kaydet
//            await tx.CommitAsync(ct);
//            return NoContent();
//        }
//        catch
//        {
//            await tx.RollbackAsync(ct);
//            throw;
//        }
//    }
//    //[HttpDelete("{id:guid}")]
//    //public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
//    //{
//    //    var tenantId = GetTenantId();

//    //    var s = await _db.Sales.Include(x => x.Items)
//    //        .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
//    //    if (s is null) return NotFound();
//    //    if (s.IsDeleted) return NoContent();

//    //    await using var tx = await _db.Database.BeginTransactionAsync(ct);
//    //    try
//    //    {
//    //        s.IsDeleted = true;
//    //        await _db.SaveChangesAsync(ct);

//    //        foreach (var it in s.Items)
//    //        {
//    //            var product = await _db.Products.AsNoTracking()
//    //                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ProductCode == it.ProductCode, ct);
//    //            if (product is null) continue;

//    //            await _stock.AdjustAsync(
//    //                branchId: s.BranchId,
//    //                productId: product.Id,
//    //                deltaQuantity: +it.Quantity,
//    //                refKind: StockRefKind.Sale,
//    //                refId: s.Id,
//    //                note: "Sale deleted",
//    //                ct: ct
//    //            );
//    //        }

//    //        await tx.CommitAsync(ct);
//    //        return NoContent();
//    //    }
//    //    catch
//    //    {
//    //        await tx.RollbackAsync(ct);
//    //        throw;
//    //    }
//    //}

//    // helpers
//    private static void CalcTotals(Sale s, out decimal subtotal, out decimal disc, out decimal tax, out decimal grand)
//    {
//        subtotal = 0m; disc = 0m; tax = 0m; grand = 0m;
//        foreach (var i in (s.Items ?? new List<SaleItem>()))
//        {
//            var lineBase = i.UnitPrice * i.Quantity;
//            var afterDisc = lineBase - i.Discount;
//            var t = afterDisc * i.TaxRate;
//            var lineTot = Math.Round(afterDisc + t, 2);

//            subtotal += Math.Round(lineBase, 2);
//            disc += Math.Round(i.Discount, 2);
//            tax += Math.Round(t, 2);
//            grand += lineTot;
//        }
//    }

//    private static SaleDtoV2 ToDto(Sale s, decimal subtotal, decimal disc, decimal tax, decimal grand)
//        => new(
//            s.Id, s.BranchId, s.UserId, s.CustomerId,
//            Subtotal: subtotal,
//            DiscountTotal: disc,
//            TaxTotal: tax,
//            GrandTotal: grand,
//            CreatedAt: s.CreatedAt,
//            Items: (s.Items ?? new()).Select(i =>
//                new SaleItemDtoV2(
//                    i.Id, i.SaleId, i.LineNo, i.ProductCode, i.ProductName, i.Karat, i.Category,
//                    i.Quantity, i.UnitPrice, i.Discount, i.TaxRate, i.LineTotal,
//                    /* ProductItemId */ null
//                )
//            ).ToList()
//        );
//}
