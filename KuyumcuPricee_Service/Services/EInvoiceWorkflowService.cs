using System.Text.Json;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Services;

public interface IEInvoiceWorkflowService
{
    Task<EInvoiceDocument> QueueInvoiceAsync(Invoice invoice, Customer? customer, CancellationToken ct);
    Task<EInvoiceDocument?> QueueManualSendAsync(Guid tenantId, Guid invoiceId, ManualEInvoiceDraft? manualDraft, CancellationToken ct);
    Task<EInvoiceDocument?> TryProcessPendingImmediatelyAsync(Guid tenantId, Guid invoiceId, CancellationToken ct);
    Task<ManualEInvoiceDraft?> BuildManualDraftAsync(Guid tenantId, Guid invoiceId, CancellationToken ct);
    Task<bool> CancelDocumentAsync(Guid tenantId, Guid documentId, string reason, CancellationToken ct);
    Task<WebhookProcessResult> ProcessWebhookAsync(Guid tenantId, Guid branchId, string providerCode, string signature, string payload, Dictionary<string, string> headers, CancellationToken ct);
}

public sealed record WebhookProcessResult(bool IsSuccess, Guid? LogId, string Message);

public sealed class EInvoiceWorkflowService : IEInvoiceWorkflowService
{
    private readonly AppDbContext _db;
    private readonly IEInvoiceProviderResolver _providerResolver;
    private readonly IUblInvoiceBuilder _ublBuilder;
    private readonly ExchangeRateService _rates;
    private readonly IConfiguration _config;

    public EInvoiceWorkflowService(
        AppDbContext db,
        IEInvoiceProviderResolver providerResolver,
        IUblInvoiceBuilder ublBuilder,
        ExchangeRateService rates,
        IConfiguration config)
    {
        _db = db;
        _providerResolver = providerResolver;
        _ublBuilder = ublBuilder;
        _rates = rates;
        _config = config;
    }

    public async Task<EInvoiceDocument> QueueInvoiceAsync(Invoice invoice, Customer? customer, CancellationToken ct)
    {
        var existing = await _db.EInvoiceDocuments.FirstOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id, ct);
        if (existing is not null)
            return existing;

        var docType = ResolveDocumentType(customer);
        var invoiceNo = await BuildInvoiceNumberAsync(invoice.TenantId, invoice.BranchId, docType, invoice.InvoiceDate, invoice.Id, ct);

        var doc = new EInvoiceDocument
        {
            TenantId = invoice.TenantId,
            BranchId = invoice.BranchId,
            InvoiceId = invoice.Id,
            CustomerId = invoice.CustomerId,
            Direction = "Outgoing",
            DocumentType = docType,
            Scenario = "TemelFatura",
            Status = "Draft",
            InvoiceNumber = invoiceNo,
            Currency = "TRY",
            GrandTotal = invoice.GrandTotal
        };
        _db.EInvoiceDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<EInvoiceDocument?> QueueManualSendAsync(Guid tenantId, Guid invoiceId, ManualEInvoiceDraft? manualDraft, CancellationToken ct)
    {
        var doc = await _db.EInvoiceDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, ct);
        if (doc is null) return null;

        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.InvoiceId, ct);
        if (invoice is null) return null;
        var customer = doc.CustomerId.HasValue
            ? await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.CustomerId.Value, ct)
            : null;

        doc.Status = "Queued";
        doc.LastError = null;
        var payload = await _db.EInvoiceOutboxes.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DocumentId == doc.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);

        if (manualDraft is not null)
        {
            doc.DocumentType = string.Equals(manualDraft.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase) ? "EArsiv" : "EFatura";
            payload = await BuildPayloadJsonFromDraftAsync(invoice, doc, manualDraft, ct);
        }
        else if (string.IsNullOrWhiteSpace(payload) || payload == "{}")
        {
            payload = await BuildPayloadJsonAsync(invoice, customer, doc.InvoiceNumber, doc.DocumentType, ct);
        }

        _db.EInvoiceOutboxes.Add(new EInvoiceOutbox
        {
            TenantId = doc.TenantId,
            BranchId = doc.BranchId,
            DocumentId = doc.Id,
            InvoiceId = doc.InvoiceId,
            Operation = "Send",
            Status = "Pending",
            PayloadJson = payload,
            NextAttemptAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<ManualEInvoiceDraft?> BuildManualDraftAsync(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, ct);
        if (doc is null) return null;
        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoiceId, ct);
        if (invoice is null) return null;
        var customer = doc.CustomerId.HasValue
            ? await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.CustomerId.Value, ct)
            : null;
        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == invoice.BranchId && x.IsActive, ct);
        var profileSettings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);

        var saleItems = await _db.SaleItems
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SaleId == invoice.SaleId)
            .OrderBy(x => x.LineNo)
            .ToListAsync(ct);

        // Eski SQL Server compatibility level ortamlarında list.Contains(...) ifadesi
        // OPENJSON ... WITH üretebildiği için tüm branch ürünlerini çekip filtreyi bellekte yapıyoruz.
        var productItemIdSet = saleItems
            .Where(x => x.ProductItemId.HasValue)
            .Select(x => x.ProductItemId!.Value)
            .ToHashSet();
        var productItems = productItemIdSet.Count == 0
            ? new List<ProductItem>()
            : (await _db.ProductItems.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.BranchId == invoice.BranchId)
                .ToListAsync(ct))
                .Where(x => productItemIdSet.Contains(x.Id))
                .ToList();
        var itemMap = productItems.ToDictionary(x => x.Id, x => x);

        var productIdSet = productItems
            .Select(x => x.ProductId)
            .ToHashSet();
        var products = productIdSet.Count == 0
            ? new List<Product>()
            : (await _db.Products.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.BranchId == invoice.BranchId)
                .ToListAsync(ct))
                .Where(x => productIdSet.Contains(x.Id))
                .ToList();
        var productMap = products.ToDictionary(x => x.Id, x => x);

        var lines = new List<ManualEInvoiceLineDraft>();
        var adjustedSellRates = await _rates.GetAdjustedSellRatesByCodeAsync(tenantId, invoice.BranchId, ct);
        var salesVatRatePercent = EInvoiceProfileSettingsCodec.NormalizeVatPercent(profileSettings.SalesInvoiceVatRatePercent);
        var salesVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(profileSettings.SalesInvoiceVatRatePercent);
        var specialZiynetVatRatePercent = EInvoiceProfileSettingsCodec.NormalizeVatPercent(profileSettings.SpecialMatrahZiynetVatRatePercent);
        var specialZiynetVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(profileSettings.SpecialMatrahZiynetVatRatePercent);
        var specialCraftedVatRatePercent = EInvoiceProfileSettingsCodec.NormalizeVatPercent(profileSettings.SpecialMatrahCraftedVatRatePercent);
        var specialCraftedVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(profileSettings.SpecialMatrahCraftedVatRatePercent);
        var workmanshipRules = profileSettings.WorkmanshipRules;
        var lineNo = 1;
        foreach (var item in saleItems)
        {
            if (item.Kind == ItemKind.Forex)
                continue;

            itemMap.TryGetValue(item.ProductItemId ?? Guid.Empty, out var pItem);
            Product? product = null;
            if (pItem is not null)
                productMap.TryGetValue(pItem.ProductId, out product);

            var qty = item.Quantity <= 0 ? 1 : item.Quantity;
            var price = item.UnitPrice < 0 ? 0 : item.UnitPrice;
            var kdvRate = item.TaxRate > 1 ? item.TaxRate : item.TaxRate * 100m;
            var lineBase = Math.Max(0, qty * price - Math.Max(0, item.Discount));
            var kdv = Math.Round(lineBase * (kdvRate / 100m), 2, MidpointRounding.AwayFromZero);
            var total = item.LineTotal > 0 ? item.LineTotal : lineBase + kdv;
            var normalizedKarat = !string.IsNullOrWhiteSpace(item.Karat) ? item.Karat : pItem?.Karat ?? product?.Karat;
            var productCode = string.IsNullOrWhiteSpace(item.ProductCode) ? null : item.ProductCode.Trim();
            var workmanshipHas = product?.BirimSatisIscilikHas ?? 0m;
            var baseCostHas = product?.Cost ?? 0m;
            var normalizedCategory = string.IsNullOrWhiteSpace(item.Category) ? (product?.Category ?? string.Empty) : item.Category.Trim();
            var isZiynetSale = IsZiynetSarrafiye(item.ProductName, normalizedCategory, product);
            var isSpecialProductSale = IsSpecialProductSale(item.ProductName, normalizedCategory, product);
            var grossTotal = item.LineTotal > 0 ? item.LineTotal : total;
            var workmanshipProductType = isZiynetSale
                ? EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet
                : EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted;
            var workmanshipSelector = isZiynetSale
                ? ResolveZiynetRuleSelector(item.ProductName, product?.ZiynetTipi, normalizedCategory)
                : normalizedKarat;
            var workmanshipComparisonValue = isZiynetSale ? qty : grossTotal;
            var matchedWorkmanshipRule = EInvoiceProfileSettingsCodec.ResolveWorkmanshipRule(
                workmanshipRules,
                workmanshipProductType,
                workmanshipSelector,
                workmanshipComparisonValue);

            if (isSpecialProductSale && matchedWorkmanshipRule is null)
            {
                var specialGross = total > 0m ? total : lineBase;
                if (specialGross < 0m) specialGross = 0m;
                var specialNet = salesVatRateRatio > 0m
                    ? Math.Round(specialGross / (1m + salesVatRateRatio), 2, MidpointRounding.AwayFromZero)
                    : Math.Round(specialGross, 2, MidpointRounding.AwayFromZero);
                var specialVat = Math.Round(specialGross - specialNet, 2, MidpointRounding.AwayFromZero);
                var specialTotal = specialGross;
                var specialUnitPrice = qty > 0m
                    ? Math.Round(specialNet / qty, 2, MidpointRounding.AwayFromZero)
                    : specialNet;

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    string.IsNullOrWhiteSpace(item.ProductName) ? "Özel Ürün" : item.ProductName.Trim(),
                    pItem?.Barcode ?? product?.Barcode,
                    productCode,
                    qty,
                    "NIU",
                    specialUnitPrice,
                    salesVatRatePercent,
                    specialVat,
                    specialTotal,
                    qty,
                    normalizedKarat,
                    0m,
                    "Özel Ürün",
                    null,
                    product?.MalTanim,
                    pItem?.Serial));
                continue;
            }

            if (isZiynetSale)
            {
                var ziynetName = ResolveZiynetDisplayName(item.ProductName, product?.ZiynetTipi);
                var ziynetUnitGram = ResolveZiynetUnitGram(ziynetName, product?.Olcu, product?.MalTanim);
                var karatMilyem = JewelrySpecialBaseCalculator.MilyemFromKarat(normalizedKarat);
                if (karatMilyem <= 0m) karatMilyem = 0.916m;
                var hasSell = _rates.GetKaratGramSellPrice("HAS", _config["EInvoice:GoldPriceReferenceSource"], adjustedSellRates);
                if (hasSell <= 0m)
                {
                    var normalizedKaratKey = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipKarat(normalizedKarat) ?? normalizedKarat;
                    hasSell = _rates.GetKaratGramSellPrice(normalizedKaratKey, _config["EInvoice:GoldPriceReferenceSource"], adjustedSellRates);
                }

                var firstUnit = Math.Round(ziynetUnitGram * karatMilyem * hasSell, 2, MidpointRounding.AwayFromZero);
                var saleGross = grossTotal > 0m ? grossTotal : Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);
                var firstTotal = Math.Round(firstUnit * qty, 2, MidpointRounding.AwayFromZero);
                var firstQuantity = qty;
                if (matchedWorkmanshipRule is not null)
                {
                    var ruleGross = ResolveRuleWorkmanshipGross(saleGross, matchedWorkmanshipRule.Percentage);
                    firstTotal = Math.Max(0m, Math.Round(saleGross - ruleGross, 2, MidpointRounding.AwayFromZero));
                }
                else if (firstTotal >= saleGross && saleGross > 0m)
                {
                    firstTotal = Math.Max(0m, saleGross - 0.01m);
                }
                var firstUnitAdjusted = firstQuantity > 0m
                    ? Math.Round(firstTotal / firstQuantity, 2, MidpointRounding.AwayFromZero)
                    : firstTotal;
                var secondGross = Math.Max(0m, Math.Round(saleGross - firstTotal, 2, MidpointRounding.AwayFromZero));
                var secondNet = specialZiynetVatRateRatio > 0m
                    ? Math.Round(secondGross / (1m + specialZiynetVatRateRatio), 2, MidpointRounding.AwayFromZero)
                    : Math.Round(secondGross, 2, MidpointRounding.AwayFromZero);
                var secondTax = Math.Round(secondGross - secondNet, 2, MidpointRounding.AwayFromZero);
                var secondUnitNet = firstQuantity > 0m
                    ? Math.Round(secondNet / firstQuantity, 2, MidpointRounding.AwayFromZero)
                    : secondNet;

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    ziynetName,
                    pItem?.Barcode ?? product?.Barcode,
                    productCode,
                    firstQuantity,
                    "NIU",
                    firstUnitAdjusted,
                    0m,
                    0m,
                    firstTotal,
                    ziynetUnitGram * firstQuantity,
                    normalizedKarat,
                    0m,
                    "Özel Matrah",
                    ziynetUnitGram * karatMilyem * firstQuantity,
                    product?.MalTanim,
                    pItem?.Serial));

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    $"{ziynetName} İşçiliği",
                    pItem?.Barcode ?? product?.Barcode,
                    string.IsNullOrWhiteSpace(productCode) ? null : $"{productCode}-ISCILIK",
                    firstQuantity,
                    "NIU",
                    secondUnitNet,
                    specialZiynetVatRatePercent,
                    secondTax,
                    secondGross,
                    ziynetUnitGram * firstQuantity,
                    normalizedKarat,
                    0m,
                    "Özel Matrah İşçilik",
                    0m,
                    product?.MalTanim,
                    pItem?.Serial));
                continue;
            }

            if (!isZiynetSale && matchedWorkmanshipRule is not null)
            {
                var saleGross = grossTotal > 0m ? grossTotal : Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);
                var ruleGross = ResolveRuleWorkmanshipGross(saleGross, matchedWorkmanshipRule.Percentage);
                var goldTotal = Math.Max(0m, Math.Round(saleGross - ruleGross, 2, MidpointRounding.AwayFromZero));

                var karatKey = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipKarat(normalizedKarat) ?? normalizedKarat;
                var goldUnitPrice = _rates.GetKaratGramSellPrice(karatKey, _config["EInvoice:GoldPriceReferenceSource"], adjustedSellRates);
                if (goldUnitPrice <= 0m)
                    goldUnitPrice = price > 0m ? price : (qty > 0m ? Math.Round(lineBase / qty, 2, MidpointRounding.AwayFromZero) : 0m);
                if (goldUnitPrice <= 0m)
                    goldUnitPrice = 1m;

                var goldQty = Math.Max(0m, Math.Round(goldTotal / goldUnitPrice, 6, MidpointRounding.AwayFromZero));
                var karatMilyem = JewelrySpecialBaseCalculator.MilyemFromKarat(normalizedKarat);
                if (karatMilyem <= 0m) karatMilyem = 0.916m;

                var goldLabel = JewelrySpecialBaseCalculator.BuildGoldLineName(normalizedKarat);
                var workmanshipLabel = JewelrySpecialBaseCalculator.BuildWorkmanshipLineName(normalizedKarat);
                var workmanshipCodeSuffix = JewelrySpecialBaseCalculator.BuildWorkmanshipCodeSuffix(normalizedKarat);

                var craftedNet = specialCraftedVatRateRatio > 0m
                    ? Math.Round(ruleGross / (1m + specialCraftedVatRateRatio), 2, MidpointRounding.AwayFromZero)
                    : ruleGross;
                var craftedVat = Math.Round(ruleGross - craftedNet, 2, MidpointRounding.AwayFromZero);
                var craftedUnitNet = goldQty > 0m
                    ? Math.Round(craftedNet / goldQty, 2, MidpointRounding.AwayFromZero)
                    : craftedNet;

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    goldLabel,
                    pItem?.Barcode ?? product?.Barcode,
                    productCode,
                    goldQty,
                    "NIU",
                    goldUnitPrice,
                    0m,
                    0m,
                    goldTotal,
                    goldQty,
                    normalizedKarat,
                    0m,
                    "Özel Matrah",
                    Math.Round(goldQty * karatMilyem, 6, MidpointRounding.AwayFromZero),
                    product?.MalTanim,
                    pItem?.Serial));

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    workmanshipLabel,
                    pItem?.Barcode ?? product?.Barcode,
                    string.IsNullOrWhiteSpace(productCode) ? null : $"{productCode}-{workmanshipCodeSuffix}",
                    goldQty,
                    "NIU",
                    craftedUnitNet,
                    specialCraftedVatRatePercent,
                    craftedVat,
                    ruleGross,
                    goldQty,
                    normalizedKarat,
                    workmanshipHas,
                    "Özel Matrah İşçilik",
                    0m,
                    product?.MalTanim,
                    pItem?.Serial));
                continue;
            }

            if (!isZiynetSale && JewelrySpecialBaseCalculator.TryBuild(
                    qty,
                    normalizedKarat,
                    ResolveGoldLineBaseAmount(qty, normalizedKarat, lineBase, adjustedSellRates),
                    baseCostHas,
                    workmanshipHas,
                    total,
                    kdvRate,
                    out var special) &&
                special.KdvMatrahi > 0m &&
                special.AltinBedeli > 0m)
            {
                var goldLabel = JewelrySpecialBaseCalculator.BuildGoldLineName(normalizedKarat);
                var workmanshipLabel = JewelrySpecialBaseCalculator.BuildWorkmanshipLineName(normalizedKarat);
                var workmanshipCodeSuffix = JewelrySpecialBaseCalculator.BuildWorkmanshipCodeSuffix(normalizedKarat);
                var goldLineTotal = special.AltinBedeli;
                var goldLineQuantity = qty;
                var hasEquivalent = special.SafHasGram;
                var configuredCraftedTotal = Math.Round(special.KdvMatrahi + Math.Round(special.KdvMatrahi * specialCraftedVatRateRatio, 2, MidpointRounding.AwayFromZero), 2, MidpointRounding.AwayFromZero);

                if (matchedWorkmanshipRule is not null)
                {
                    var saleGross = grossTotal > 0m ? grossTotal : Math.Round(qty * price, 2, MidpointRounding.AwayFromZero);
                    var ruleGross = ResolveRuleWorkmanshipGross(saleGross, matchedWorkmanshipRule.Percentage);
                    goldLineTotal = Math.Max(0m, Math.Round(saleGross - ruleGross, 2, MidpointRounding.AwayFromZero));
                    if (special.AltinBirimFiyat > 0m)
                        goldLineQuantity = Math.Max(0m, Math.Round(goldLineTotal / special.AltinBirimFiyat, 6, MidpointRounding.AwayFromZero));
                    hasEquivalent = qty > 0m
                        ? Math.Round((special.SafHasGram / qty) * goldLineQuantity, 6, MidpointRounding.AwayFromZero)
                        : special.SafHasGram;
                    configuredCraftedTotal = ruleGross;
                }

                var configuredCraftedNet = specialCraftedVatRateRatio > 0m
                    ? Math.Round(configuredCraftedTotal / (1m + specialCraftedVatRateRatio), 2, MidpointRounding.AwayFromZero)
                    : Math.Round(configuredCraftedTotal, 2, MidpointRounding.AwayFromZero);
                var configuredCraftedVat = Math.Round(configuredCraftedTotal - configuredCraftedNet, 2, MidpointRounding.AwayFromZero);
                var configuredCraftedUnit = goldLineQuantity > 0m
                    ? Math.Round(configuredCraftedNet / goldLineQuantity, 2, MidpointRounding.AwayFromZero)
                    : configuredCraftedNet;

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    goldLabel,
                    pItem?.Barcode ?? product?.Barcode,
                    productCode,
                    goldLineQuantity,
                    "NIU",
                    special.AltinBirimFiyat,
                    0m,
                    0m,
                    goldLineTotal,
                    goldLineQuantity,
                    normalizedKarat,
                    0m,
                    "Özel Matrah",
                    hasEquivalent,
                    product?.MalTanim,
                    pItem?.Serial));

                lines.Add(new ManualEInvoiceLineDraft(
                    lineNo++,
                    workmanshipLabel,
                    pItem?.Barcode ?? product?.Barcode,
                    string.IsNullOrWhiteSpace(productCode) ? null : $"{productCode}-{workmanshipCodeSuffix}",
                    goldLineQuantity,
                    "NIU",
                    configuredCraftedUnit,
                    specialCraftedVatRatePercent,
                    configuredCraftedVat,
                    configuredCraftedTotal,
                    goldLineQuantity,
                    normalizedKarat,
                    workmanshipHas,
                    "Özel Matrah İşçilik",
                    0m,
                    product?.MalTanim,
                    pItem?.Serial));
                continue;
            }

            lines.Add(new ManualEInvoiceLineDraft(
                lineNo++,
                string.IsNullOrWhiteSpace(item.ProductName) ? "Ürün" : item.ProductName.Trim(),
                pItem?.Barcode ?? product?.Barcode,
                productCode,
                qty,
                "NIU",
                price,
                kdvRate,
                kdv,
                total,
                qty,
                normalizedKarat,
                workmanshipHas,
                item.Category ?? product?.Category,
                null,
                product?.MalTanim,
                pItem?.Serial));
        }

        return new ManualEInvoiceDraft(
            doc.DocumentType,
            customer?.FullName?.Trim() ?? string.Empty,
            NormalizeTaxNo(customer?.NationalId),
            customer?.Address,
            customer?.City,
            customer?.District,
            ResolvePostalCodeFromText(customer?.Address),
            invoice.InvoiceDate.ToLocalTime().ToString("dd.MM.yyyy"),
            invoice.InvoiceDate.ToLocalTime().ToString("HH:mm:ss"),
            customer?.Email,
            "TRY",
            lines);
    }

    public async Task<EInvoiceDocument?> TryProcessPendingImmediatelyAsync(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var doc = await _db.EInvoiceDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, ct);
        if (doc is null) return null;

        var now = DateTime.UtcNow;
        var lockStealAfterSeconds = 20;
        var outbox = await _db.EInvoiceOutboxes
            .Where(x => x.TenantId == tenantId &&
                        x.DocumentId == doc.Id &&
                        x.Status == "Pending" &&
                        (x.NextAttemptAt <= now || x.RetryCount == 0))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (outbox is null) return doc;
        if (outbox.LockedAt.HasValue && outbox.LockedAt.Value > now.AddSeconds(-lockStealAfterSeconds))
            return doc;

        outbox.LockedAt = now;
        await _db.SaveChangesAsync(ct);

        try
        {
            var profile = await _db.EInvoiceProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId && x.IsActive, ct);
            var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
            var adapter = _providerResolver.Resolve(providerCode);

            var (buyerName, buyerTaxNo) = ParsePayload(outbox.PayloadJson);
            var sendReq = new EInvoiceSendRequest(
                doc.TenantId,
                doc.BranchId,
                doc.Id,
                doc.DocumentType,
                doc.InvoiceNumber,
                doc.CreatedAt,
                doc.GrandTotal,
                doc.Currency,
                buyerName,
                buyerTaxNo,
                outbox.PayloadJson,
                profile?.IntegratorUsername,
                profile?.IntegratorSecretRef);

            var sendResult = await adapter.SendOutgoingAsync(sendReq, ct);
            if (!sendResult.IsSuccess)
            {
                outbox.RetryCount++;
                outbox.Status = outbox.RetryCount >= 8 ? "DeadLetter" : "Pending";
                outbox.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, outbox.RetryCount)));
                outbox.LastError = ToDbSafeError(sendResult.ErrorMessage ?? "Provider send failed.");

                doc.Status = "Failed";
                doc.RetryCount = outbox.RetryCount;
                doc.LastError = outbox.LastError;
            }
            else
            {
                doc.Status = NormalizeStatus(sendResult.ProviderStatus, "Sent");
                doc.IntegratorDocumentId = sendResult.IntegratorDocumentId ?? doc.IntegratorDocumentId;
                doc.Uuid = sendResult.Uuid ?? doc.Uuid;
                doc.Ettn = sendResult.Ettn ?? doc.Ettn;
                doc.RawLastResponse = sendResult.RawResponse;
                doc.LastError = null;
                doc.SubmittedAt ??= DateTime.UtcNow;
                if (doc.Status == "Delivered")
                    doc.DeliveredAt ??= DateTime.UtcNow;

                outbox.Status = "Done";
                outbox.ProcessedAt = DateTime.UtcNow;
                outbox.LastError = null;
            }
        }
        catch (Exception ex)
        {
            outbox.RetryCount++;
            outbox.Status = outbox.RetryCount >= 8 ? "DeadLetter" : "Pending";
            outbox.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, outbox.RetryCount)));
            outbox.LastError = ToDbSafeError(ex.Message);
            doc.Status = "Failed";
            doc.RetryCount = outbox.RetryCount;
            doc.LastError = outbox.LastError;
        }
        finally
        {
            outbox.LockedAt = null;
            await _db.SaveChangesAsync(ct);
        }

        return doc;
    }

    public async Task<bool> CancelDocumentAsync(Guid tenantId, Guid documentId, string reason, CancellationToken ct)
    {
        var doc = await _db.EInvoiceDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId, ct);
        if (doc is null)
            return false;
        var integratorRef = !string.IsNullOrWhiteSpace(doc.IntegratorDocumentId)
            ? doc.IntegratorDocumentId
            : (!string.IsNullOrWhiteSpace(doc.Uuid) ? doc.Uuid : doc.Ettn);
        if (string.IsNullOrWhiteSpace(integratorRef))
            return false;

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId && x.IsActive, ct);
        var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
        var adapter = _providerResolver.Resolve(providerCode);
        var result = await adapter.CancelAsync(new EInvoiceCancelRequest(
            doc.TenantId,
            doc.BranchId,
            doc.Id,
            integratorRef!,
            string.IsNullOrWhiteSpace(reason) ? "Iptal talebi" : reason.Trim(),
            doc.Uuid,
            profile?.IntegratorUsername,
            profile?.IntegratorSecretRef), ct);

        if (!result.IsSuccess)
        {
            doc.Status = "CancelPending";
            doc.LastError = result.ErrorMessage;
        }
        else
        {
            doc.Status = NormalizeStatus(result.ProviderStatus, "Cancelled");
            doc.CancelledAt = DateTime.UtcNow;
            doc.LastError = null;
            doc.RawLastResponse = result.RawResponse;
        }

        await _db.SaveChangesAsync(ct);
        return result.IsSuccess;
    }

    public async Task<WebhookProcessResult> ProcessWebhookAsync(
        Guid tenantId,
        Guid branchId,
        string providerCode,
        string signature,
        string payload,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var adapter = _providerResolver.Resolve(providerCode);
        var verified = await adapter.VerifyWebhookAsync(
            new EInvoiceWebhookVerificationRequest(providerCode, signature, payload, headers),
            ct);

        if (!string.IsNullOrWhiteSpace(verified.EventId))
        {
            var duplicate = await _db.EInvoiceWebhookLogs.AsNoTracking().AnyAsync(
                x => x.TenantId == tenantId && x.ProviderCode == providerCode && x.EventId == verified.EventId,
                ct);
            if (duplicate)
                return new WebhookProcessResult(true, null, "Duplicate webhook ignored.");
        }

        var log = new EInvoiceWebhookLog
        {
            TenantId = tenantId,
            BranchId = branchId,
            ProviderCode = providerCode,
            Signature = signature,
            EventId = verified.EventId,
            EventType = verified.EventType,
            IntegratorDocumentId = verified.DocumentId,
            PayloadJson = payload,
            IsVerified = verified.IsValid
        };
        _db.EInvoiceWebhookLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        if (!verified.IsValid)
        {
            log.ProcessError = verified.ErrorMessage ?? "Webhook verification failed.";
            log.IsProcessed = true;
            log.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new WebhookProcessResult(false, log.Id, log.ProcessError);
        }

        var doc = await _db.EInvoiceDocuments.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.IntegratorDocumentId == verified.DocumentId,
            ct);

        if (doc is null)
        {
            log.ProcessError = "Document not found for webhook.";
            log.IsProcessed = true;
            log.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return new WebhookProcessResult(false, log.Id, log.ProcessError);
        }

        doc.Status = NormalizeStatus(verified.ProviderStatus, doc.Status);
        doc.RawLastResponse = payload;
        doc.LastError = null;
        if (string.Equals(doc.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
            doc.DeliveredAt = DateTime.UtcNow;

        var invoice = await _db.Invoices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.InvoiceId, ct);
        if (invoice is not null && (doc.Status == "Sent" || doc.Status == "Delivered"))
            invoice.IsExported = true;

        log.IsProcessed = true;
        log.ProcessedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new WebhookProcessResult(true, log.Id, "Webhook processed.");
    }

    private async Task<string> BuildInvoiceNumberAsync(Guid tenantId, Guid branchId, string docType, DateTime date, Guid id, CancellationToken ct)
    {
        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.IsActive, ct);
        var prefix = docType == "EFatura" ? profile?.DefaultInvoicePrefix : profile?.DefaultArchivePrefix;
        return BuildInvoiceNumber(prefix, date, id);
    }

    private async Task<string> BuildPayloadJsonAsync(Invoice invoice, Customer? customer, string invoiceNo, string docType, CancellationToken ct)
    {
        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.BranchId == invoice.BranchId && x.IsActive, ct);
        var ubl = await _ublBuilder.BuildOutgoingAsync(invoice, customer, profile, invoiceNo, docType, ct);

        var payloadObj = new
        {
            invoiceId = invoice.Id,
            invoiceNo,
            invoiceDateUtc = invoice.InvoiceDate,
            tenantId = invoice.TenantId,
            branchId = invoice.BranchId,
            grandTotal = invoice.GrandTotal,
            currency = "TRY",
            senderVkn = ubl.SellerTaxNumber,
            senderAlias = ubl.SellerAlias,
            receiverVkn = ubl.BuyerTaxNumber,
            receiverAlias = ubl.BuyerAlias,
            buyerEmail = ubl.BuyerAlias,
            ublBase64 = ubl.UblBase64,
            ublXml = ubl.UblXml,
            customer = customer is null ? null : new
            {
                customer.Id,
                customer.FullName,
                customer.NationalId,
                customer.Address,
                customer.Phone,
                customer.Email
            }
        };

        return JsonSerializer.Serialize(payloadObj);
    }

    private async Task<string> BuildPayloadJsonFromDraftAsync(Invoice invoice, EInvoiceDocument doc, ManualEInvoiceDraft draft, CancellationToken ct)
    {
        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.BranchId == invoice.BranchId && x.IsActive, ct);
        var ubl = await _ublBuilder.BuildOutgoingFromDraftAsync(invoice, profile, doc.InvoiceNumber, draft, ct);

        var payloadObj = new
        {
            invoiceId = invoice.Id,
            invoiceNo = doc.InvoiceNumber,
            invoiceDateUtc = invoice.InvoiceDate,
            tenantId = invoice.TenantId,
            branchId = invoice.BranchId,
            grandTotal = invoice.GrandTotal,
            currency = string.IsNullOrWhiteSpace(draft.Currency) ? "TRY" : draft.Currency,
            senderVkn = ubl.SellerTaxNumber,
            senderAlias = ubl.SellerAlias,
            receiverVkn = ubl.BuyerTaxNumber,
            receiverAlias = ubl.BuyerAlias,
            buyerEmail = draft.BuyerEmail,
            ublBase64 = ubl.UblBase64,
            ublXml = ubl.UblXml,
            draft
        };

        return JsonSerializer.Serialize(payloadObj);
    }

    private static string BuildInvoiceNumber(string? prefix, DateTime date, Guid id)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? "INV" : prefix.Trim().ToUpperInvariant();
        return $"{p}-{date:yyyyMMdd}-{id.ToString("N")[..8]}";
    }

    private static string ResolveDocumentType(Customer? customer)
    {
        var taxNo = NormalizeTaxNo(customer?.NationalId);
        // EDM/GIB akışında varsayılan olarak yalnızca VKN(10) için e-Fatura,
        // diğer durumlarda e-Arşiv seçiyoruz. Kullanıcı önizlemede manuel değiştirebilir.
        return taxNo.Length == 10 ? "EFatura" : "EArsiv";
    }

    private static string NormalizeTaxNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsDigit).ToArray();
        return new string(chars);
    }

    private static string? ResolvePostalCodeFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b\d{5}\b");
        return match.Success ? match.Value : null;
    }

    private static (string BuyerName, string BuyerTaxNo) ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return ("", "");

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object)
            {
                var name = customer.TryGetProperty("FullName", out var n1) ? n1.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && customer.TryGetProperty("fullName", out var n2))
                    name = n2.GetString();
                var taxNo = customer.TryGetProperty("NationalId", out var t1) ? t1.GetString() : null;
                if (string.IsNullOrWhiteSpace(taxNo) && customer.TryGetProperty("nationalId", out var t2))
                    taxNo = t2.GetString();
                return (name ?? "", taxNo ?? "");
            }
        }
        catch
        {
            // ignored
        }

        return ("", "");
    }

    private static string ToDbSafeError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Bilinmeyen hata";
        var text = raw.Trim();
        const int maxLen = 950; // DB column is 1000, keep headroom.
        return text.Length <= maxLen ? text : text[..maxLen];
    }

    private decimal ResolveGoldLineBaseAmount(
        decimal quantity,
        string? karat,
        decimal fallbackLineBase,
        IReadOnlyDictionary<string, decimal> adjustedSellRates)
    {
        var source = _config["EInvoice:GoldPriceReferenceSource"];
        var normalizedKaratKey = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipKarat(karat) ?? karat;
        var gramPrice = _rates.GetKaratGramSellPrice(normalizedKaratKey, source, adjustedSellRates);
        if (gramPrice <= 0m || quantity <= 0m)
            return Math.Max(0m, fallbackLineBase);
        return Math.Round(quantity * gramPrice, 2, MidpointRounding.AwayFromZero);
    }

    private static bool IsZiynetSarrafiye(string? productName, string? category, Product? product)
    {
        if (product?.InventoryType == InventoryType.Ziynet)
            return true;

        var text = $"{productName} {category} {product?.ZiynetTipi}".ToUpperInvariant();
        return text.Contains("ZİYNET", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ZIYNET", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ÇEYREK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CEYREK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("YARIM", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TAM", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ATA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecialProductSale(string? productName, string? category, Product? product)
    {
        if (product?.IsSpecialProduct == true)
            return true;
        var text = $"{productName} {category} {product?.Category}".ToUpperInvariant();
        return text.Contains("ÖZEL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OZEL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SAAT", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveZiynetDisplayName(string? itemName, string? ziynetTip)
    {
        var value = !string.IsNullOrWhiteSpace(ziynetTip) ? ziynetTip : itemName;
        return string.IsNullOrWhiteSpace(value) ? "Ziynet Altın" : value.Trim();
    }

    private static string ResolveZiynetRuleSelector(string? itemName, string? ziynetTip, string? category)
    {
        var text = $"{ziynetTip} {itemName} {category}".ToUpperInvariant();
        if (text.Contains("GREMSE", StringComparison.OrdinalIgnoreCase)) return "GREMSE";
        if (text.Contains("ATA5", StringComparison.OrdinalIgnoreCase) || text.Contains("BEŞLİ", StringComparison.OrdinalIgnoreCase) || text.Contains("BESLI", StringComparison.OrdinalIgnoreCase)) return "ATA5";
        if (text.Contains("ATA", StringComparison.OrdinalIgnoreCase)) return "ATA";
        if (text.Contains("ÇEYREK", StringComparison.OrdinalIgnoreCase) || text.Contains("CEYREK", StringComparison.OrdinalIgnoreCase)) return "CEYREK";
        if (text.Contains("YARIM", StringComparison.OrdinalIgnoreCase)) return "YARIM";
        if (text.Contains("TAM", StringComparison.OrdinalIgnoreCase)) return "TAM";
        if (text.Contains("HAS", StringComparison.OrdinalIgnoreCase)) return "HASALTIN";
        if (text.Contains("22") && text.Contains("GR", StringComparison.OrdinalIgnoreCase)) return "22AYARGR";
        if (text.Contains("GRAM", StringComparison.OrdinalIgnoreCase)) return "GRAMALTIN";
        return "CEYREK";
    }

    private static decimal ResolveZiynetUnitGram(string? ziynetName, string? olcu, string? malTanim)
    {
        if (TryReadDecimal(olcu, out var fromOlcu) && fromOlcu > 0m)
            return fromOlcu;
        if (TryReadDecimal(malTanim, out var fromMal) && fromMal > 0m)
            return fromMal;

        var text = (ziynetName ?? string.Empty).ToUpperInvariant();
        if (text.Contains("ÇEYREK") || text.Contains("CEYREK")) return 1.75m;
        if (text.Contains("YARIM")) return 3.50m;
        if (text.Contains("TAM")) return 7.00m;
        if (text.Contains("ATA")) return 7.20m;
        if (text.Contains("GRAM")) return 1.00m;
        return 1.00m;
    }

    private static bool TryReadDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = new string(text.Where(ch => char.IsDigit(ch) || ch == ',' || ch == '.').ToArray());
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        normalized = normalized.Replace(",", ".");
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static decimal ResolveRuleWorkmanshipGross(decimal saleGross, decimal percentage)
    {
        if (saleGross <= 0m || percentage <= 0m)
            return 0m;

        var gross = Math.Round(saleGross * (percentage / 100m), 2, MidpointRounding.AwayFromZero);
        if (gross >= saleGross)
            gross = Math.Max(0m, saleGross - 0.01m);
        return gross;
    }

    internal static string NormalizeStatus(string? providerStatus, string fallback)
    {
        var value = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "queued" => "Queued",
            "sent" => "Sent",
            "delivered" => "Delivered",
            "rejected" => "Rejected",
            "cancelpending" => "CancelPending",
            "cancelled" => "Cancelled",
            "failed" => "Failed",
            _ => fallback
        };
    }
}
