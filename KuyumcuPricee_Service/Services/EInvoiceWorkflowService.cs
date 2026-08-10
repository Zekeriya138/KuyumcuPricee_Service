using System.Text.Json;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using kuyumcu_application.Abstractions;
using kuyumcu_application;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KUYUMCU.Price_Service.Services;

public interface IEInvoiceWorkflowService
{
    Task<EInvoiceDocument> QueueInvoiceAsync(Invoice invoice, Customer? customer, CancellationToken ct);
    Task<EInvoiceDocument?> QueueManualSendAsync(Guid tenantId, Guid invoiceId, ManualEInvoiceDraft? manualDraft, CancellationToken ct);
    Task<EInvoiceDocument?> TryProcessPendingImmediatelyAsync(Guid tenantId, Guid invoiceId, CancellationToken ct);
    Task RefreshOutboxPayloadIfNeededAsync(EInvoiceDocument doc, EInvoiceOutbox outbox, CancellationToken ct);
    void ScheduleImmediateProcessing(Guid tenantId, Guid invoiceId);
    Task<ManualEInvoiceDraft?> BuildManualDraftAsync(Guid tenantId, Guid invoiceId, CancellationToken ct);
    Task<bool> CancelDocumentAsync(Guid tenantId, Guid documentId, string reason, CancellationToken ct);
    Task<WebhookProcessResult> ProcessWebhookAsync(Guid tenantId, Guid branchId, string providerCode, string signature, string payload, Dictionary<string, string> headers, CancellationToken ct);
    Task<(Guid InvoiceId, Guid DocumentId)> CreateCollectionDraftAsync(CollectionDraftInput input, CancellationToken ct);
}

public sealed record WebhookProcessResult(bool IsSuccess, Guid? LogId, string Message);

/// <summary>Tahsilat kaynaklı has altın taslak faturası girdisi (satışa bağlı değildir).</summary>
public sealed record CollectionDraftInput(
    Guid TenantId,
    Guid BranchId,
    Guid? CustomerId,
    string BuyerName,
    string? BuyerTaxNumber,
    string? BuyerAddress,
    string? BuyerCity,
    string? BuyerDistrict,
    string? BuyerEmail,
    decimal AmountTl,
    string? Description,
    DateTime TxDateUtc,
    string? DocumentTypeOverride);

public sealed class EInvoiceWorkflowService : IEInvoiceWorkflowService
{
    private readonly AppDbContext _db;
    private readonly IEInvoiceProviderResolver _providerResolver;
    private readonly IUblInvoiceBuilder _ublBuilder;
    private readonly ExchangeRateService _rates;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EInvoiceWorkflowService> _logger;
    private readonly EInvoiceImmediateProcessQueue _immediateQueue;
    private readonly ITaxpayerLookupService? _taxpayerLookup;

    public EInvoiceWorkflowService(
        AppDbContext db,
        IEInvoiceProviderResolver providerResolver,
        IUblInvoiceBuilder ublBuilder,
        ExchangeRateService rates,
        IConfiguration config,
        IServiceScopeFactory scopeFactory,
        ILogger<EInvoiceWorkflowService> logger,
        EInvoiceImmediateProcessQueue immediateQueue,
        ITaxpayerLookupService? taxpayerLookup = null)
    {
        _db = db;
        _providerResolver = providerResolver;
        _ublBuilder = ublBuilder;
        _rates = rates;
        _config = config;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _immediateQueue = immediateQueue;
        _taxpayerLookup = taxpayerLookup;
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

    public async Task<(Guid InvoiceId, Guid DocumentId)> CreateCollectionDraftAsync(CollectionDraftInput input, CancellationToken ct)
    {
        var amount = decimal.Round(Math.Max(0m, input.AmountTl), 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
            throw new InvalidOperationException("Tahsilat tutarı 0'dan büyük olmalıdır.");

        var buyerTax = NormalizeTaxNo(input.BuyerTaxNumber);
        var buyerName = BankMovementParser.SanitizeCounterpartyDisplayName(input.BuyerName, buyerTax)
            ?? input.BuyerName?.Trim()
            ?? "";
        string? receiverAlias = null;
        var buyerEmail = input.BuyerEmail?.Trim();
        var docType = !string.IsNullOrWhiteSpace(input.DocumentTypeOverride)
            ? (string.Equals(input.DocumentTypeOverride, "EFatura", StringComparison.OrdinalIgnoreCase) ? "EFatura" : "EArsiv")
            : (buyerTax.Length == 10 ? "EFatura" : "EArsiv");

        if (string.IsNullOrWhiteSpace(input.DocumentTypeOverride) &&
            IsValidCollectionTaxNo(buyerTax) &&
            _taxpayerLookup is not null)
        {
            try
            {
                var lookup = await _taxpayerLookup.VerifyTaxNoAsync(input.TenantId, input.BranchId, buyerTax, ct);
                if (lookup is not null)
                {
                    docType = lookup.IsEInvoiceTaxpayer ? "EFatura" : "EArsiv";
                    if (!string.IsNullOrWhiteSpace(lookup.Title))
                    {
                        var lookupTitle = BankMovementParser.SanitizeCounterpartyDisplayName(lookup.Title, buyerTax)
                            ?? lookup.Title.Trim();
                        if (!string.IsNullOrWhiteSpace(lookupTitle))
                            buyerName = lookupTitle;
                    }
                    else
                    {
                        buyerName = BankMovementParser.SanitizeCounterpartyDisplayName(buyerName, buyerTax) ?? buyerName;
                    }

                    receiverAlias = lookup.ReceiverAlias?.Trim();
                }
                else
                {
                    _logger.LogWarning(
                        "Tahsilat taslağı mükellef sorgusu sonuç döndürmedi ({TaxNo}). E-Fatura ayarları ve entegratör bilgilerini kontrol edin.",
                        buyerTax);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tahsilat taslağı mükellef sorgusu başarısız ({TaxNo})", buyerTax);
            }
        }
        else
        {
            buyerName = BankMovementParser.SanitizeCounterpartyDisplayName(buyerName, buyerTax) ?? buyerName;
        }

        if (string.IsNullOrWhiteSpace(buyerName))
            buyerName = BankMovementParser.SanitizeCounterpartyDisplayName(input.BuyerName, buyerTax)
                ?? "Karşı Taraf";

        if (docType == "EFatura" && string.IsNullOrWhiteSpace(receiverAlias))
            _logger.LogWarning("Tahsilat taslağı e-Fatura için alıcı etiketi boş ({TaxNo})", buyerTax);

        var meta = new CollectionDraftMeta(
            Version: 2,
            buyerName,
            buyerTax,
            input.BuyerAddress,
            input.BuyerCity,
            input.BuyerDistrict,
            buyerEmail,
            docType,
            input.Description,
            receiverAlias);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = input.TenantId,
            SaleId = null,
            BranchId = input.BranchId,
            CustomerId = input.CustomerId,
            InvoiceDate = input.TxDateUtc == default ? DateTime.UtcNow : input.TxDateUtc,
            GrandTotal = amount,
            PaymentType = "Havale",
            PaymentSplitRatio = 1m,
            IsExported = false,
            CollectionMetaJson = JsonSerializer.Serialize(meta)
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);

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
        return (invoice.Id, doc.Id);
    }

    private static CollectionDraftMeta? TryDeserializeCollectionMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if ((doc.RootElement.TryGetProperty("Version", out var versionProp)
                 || doc.RootElement.TryGetProperty("version", out versionProp))
                && versionProp.TryGetInt32(out var version)
                && version >= 2)
            {
                return JsonSerializer.Deserialize<CollectionDraftMeta>(json, CollectionMetaJsonOptions);
            }

            // Eski (v1) kayıtlar: alıcı bilgilerini taşır, kalemler işlem anında yeniden hesaplanır.
            var legacy = JsonSerializer.Deserialize<CollectionDraftMetaLegacy>(json, CollectionMetaJsonOptions);
            if (legacy is null) return null;
            return new CollectionDraftMeta(
                2,
                legacy.BuyerName,
                legacy.BuyerTaxNumber,
                legacy.BuyerAddress,
                legacy.BuyerCity,
                legacy.BuyerDistrict,
                legacy.BuyerEmail,
                legacy.DocumentType,
                legacy.Description);
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions CollectionMetaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record CollectionDraftMeta(
        int Version,
        string BuyerName,
        string? BuyerTaxNumber,
        string? BuyerAddress,
        string? BuyerCity,
        string? BuyerDistrict,
        string? BuyerEmail,
        string DocumentType,
        string? Description,
        string? ReceiverAlias = null);

    private static bool IsValidCollectionTaxNo(string taxNo)
        => !string.IsNullOrWhiteSpace(taxNo) &&
           taxNo.Length is 10 or 11 &&
           !string.Equals(taxNo, CounterpartyTaxResolverService.NihaiTuketiciTckn, StringComparison.Ordinal);

    private async Task<CollectionDraftMeta> RefreshCollectionDraftMetaAsync(
        Guid tenantId,
        Guid branchId,
        CollectionDraftMeta meta,
        CancellationToken ct)
    {
        if (_taxpayerLookup is null || !IsValidCollectionTaxNo(NormalizeTaxNo(meta.BuyerTaxNumber)))
            return meta;

        var buyerTax = NormalizeTaxNo(meta.BuyerTaxNumber)!;
        var needsRefresh = string.IsNullOrWhiteSpace(meta.ReceiverAlias) ||
                           string.Equals(meta.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
        if (!needsRefresh)
            return meta;

        try
        {
            var lookup = await _taxpayerLookup.VerifyTaxNoAsync(tenantId, branchId, buyerTax, ct);
            if (lookup is null)
                return meta;

            var docType = lookup.IsEInvoiceTaxpayer ? "EFatura" : meta.DocumentType;
            var buyerName = meta.BuyerName;
            if (!string.IsNullOrWhiteSpace(lookup.Title))
            {
                var lookupTitle = BankMovementParser.SanitizeCounterpartyDisplayName(lookup.Title, buyerTax)
                    ?? lookup.Title.Trim();
                if (!string.IsNullOrWhiteSpace(lookupTitle))
                    buyerName = lookupTitle;
            }

            var receiverAlias = lookup.ReceiverAlias?.Trim() ?? meta.ReceiverAlias;
            if (string.Equals(docType, meta.DocumentType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(buyerName, meta.BuyerName, StringComparison.Ordinal) &&
                string.Equals(receiverAlias, meta.ReceiverAlias, StringComparison.Ordinal))
                return meta;

            return meta with
            {
                BuyerName = buyerName,
                DocumentType = docType,
                ReceiverAlias = receiverAlias
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tahsilat taslağı mükellef bilgisi yenilenemedi ({TaxNo})", buyerTax);
            return meta;
        }
    }

    private async Task PersistCollectionDraftMetaAsync(
        Guid tenantId,
        Guid invoiceId,
        Guid documentId,
        CollectionDraftMeta meta,
        CancellationToken ct)
    {
        var invoice = await _db.Invoices
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == invoiceId, ct);
        if (invoice is null) return;

        var existing = TryDeserializeCollectionMeta(invoice.CollectionMetaJson);
        if (existing is null) return;

        if (string.Equals(existing.BuyerName, meta.BuyerName, StringComparison.Ordinal) &&
            string.Equals(existing.DocumentType, meta.DocumentType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.ReceiverAlias, meta.ReceiverAlias, StringComparison.Ordinal))
            return;

        invoice.CollectionMetaJson = JsonSerializer.Serialize(meta, CollectionMetaJsonOptions);

        var doc = await _db.EInvoiceDocuments
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == documentId, ct);
        if (doc is not null && !string.IsNullOrWhiteSpace(meta.DocumentType))
            doc.DocumentType = meta.DocumentType;

        await _db.SaveChangesAsync(ct);
    }

    private async Task<List<ManualEInvoiceLineDraft>> BuildCollectionSpecialMatrahLinesAsync(
        Guid tenantId,
        Guid branchId,
        decimal amountTl,
        CancellationToken ct)
    {
        var amount = decimal.Round(Math.Max(0m, amountTl), 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
            return new List<ManualEInvoiceLineDraft>();

        decimal hasSatis = 0m;
        try
        {
            var sell = await _rates.GetAdjustedSellRatesByCodeAsync(tenantId, branchId, ct);
            if (sell != null && sell.TryGetValue("G24_TRY", out var r) && r > 0m) hasSatis = r;
        }
        catch { /* fallback below */ }
        if (hasSatis <= 0m) hasSatis = _rates.GetQuoteAskByCode("G24_TRY");
        if (hasSatis <= 0m) hasSatis = _rates.GetQuoteBidByCode("G24_TRY");
        if (hasSatis <= 0m)
            throw new InvalidOperationException("Has altın kuru bulunamadı; taslak fatura oluşturulamadı.");

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId, ct);
        var settings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        var specialCraftedVatRatePercent = EInvoiceProfileSettingsCodec.NormalizeVatPercent(settings.SpecialMatrahCraftedVatRatePercent);
        var specialCraftedVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(settings.SpecialMatrahCraftedVatRatePercent);

        var matchedRule = EInvoiceProfileSettingsCodec.ResolveCollectionWorkmanshipRule(
            settings.WorkmanshipRules,
            amount);

        var saleGross = amount;
        var ruleGross = matchedRule is not null
            ? ResolveRuleWorkmanshipGross(saleGross, matchedRule.Percentage)
            : 0m;
        var goldTotal = Math.Max(0m, Math.Round(saleGross - ruleGross, 2, MidpointRounding.AwayFromZero));

        var goldUnitPrice = hasSatis;
        var goldQty = goldUnitPrice > 0m
            ? Math.Max(0m, Math.Round(goldTotal / goldUnitPrice, 6, MidpointRounding.AwayFromZero))
            : 0m;
        if (goldQty <= 0m && goldTotal > 0m)
        {
            goldQty = 0.001m;
            goldUnitPrice = Math.Round(goldTotal / goldQty, 2, MidpointRounding.AwayFromZero);
        }

        const string productLabel = "Has Altın (Tahsilat Karşılığı)";
        const string karat = "995";
        var lines = new List<ManualEInvoiceLineDraft>
        {
            new(
                1,
                productLabel,
                null,
                null,
                goldQty,
                "GR",
                goldUnitPrice,
                0m,
                0m,
                goldTotal,
                goldQty,
                karat,
                0m,
                "Özel Matrah",
                goldQty,
                null,
                null)
        };

        if (ruleGross > 0m)
        {
            var craftedNet = specialCraftedVatRateRatio > 0m
                ? Math.Round(ruleGross / (1m + specialCraftedVatRateRatio), 2, MidpointRounding.AwayFromZero)
                : Math.Round(ruleGross, 2, MidpointRounding.AwayFromZero);
            var craftedVat = Math.Round(ruleGross - craftedNet, 2, MidpointRounding.AwayFromZero);
            var craftedUnitNet = goldQty > 0m
                ? Math.Round(craftedNet / goldQty, 2, MidpointRounding.AwayFromZero)
                : craftedNet;

            lines.Add(new ManualEInvoiceLineDraft(
                2,
                $"{productLabel} İşçiliği",
                null,
                null,
                goldQty,
                "GR",
                craftedUnitNet,
                specialCraftedVatRatePercent,
                craftedVat,
                ruleGross,
                goldQty,
                karat,
                0m,
                "Özel Matrah İşçilik",
                0m,
                null,
                null));
        }

        return lines;
    }

    private sealed record CollectionDraftMetaLegacy(
        string BuyerName,
        string? BuyerTaxNumber,
        string? BuyerAddress,
        string? BuyerCity,
        string? BuyerDistrict,
        string? BuyerEmail,
        string DocumentType,
        string ProductLabel,
        decimal Gram,
        string Karat,
        decimal HasEquivalent,
        decimal UnitPrice,
        decimal KdvRate,
        decimal KdvAmount,
        decimal TotalAmount,
        string? Description);

    public async Task<EInvoiceDocument?> QueueManualSendAsync(Guid tenantId, Guid invoiceId, ManualEInvoiceDraft? manualDraft, CancellationToken ct)
    {
        var doc = await _db.EInvoiceDocuments.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, ct);
        if (doc is null) return null;

        var invoice = await _db.Invoices.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.InvoiceId, ct);
        if (invoice is null) return null;
        var customer = doc.CustomerId.HasValue
            ? await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == doc.CustomerId.Value, ct)
            : null;

        // Tahsilat kaynaklı faturalar el ile taslak verilmese de has altın meta üzerinden gönderilir.
        if (manualDraft is null && !string.IsNullOrWhiteSpace(invoice.CollectionMetaJson))
            manualDraft = await BuildManualDraftAsync(tenantId, invoiceId, ct);

        doc.Status = "Queued";
        doc.LastError = null;

        if (manualDraft is not null)
        {
            var normalizedType = string.Equals(manualDraft.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase)
                ? "EArsiv"
                : "EFatura";
            manualDraft = manualDraft with { DocumentType = normalizedType };
            doc.DocumentType = normalizedType;
        }

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == doc.BranchId, ct);
        var prefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
            doc.DocumentType,
            profile?.DefaultInvoicePrefix,
            profile?.DefaultArchivePrefix);
        var issueDate = invoice.InvoiceDate.ToLocalTime().Date;
        var isUyumsoft = string.Equals(profile?.ProviderCode, "uyumsoft", StringComparison.OrdinalIgnoreCase);
        if (isUyumsoft)
        {
            var adapter = _providerResolver.Resolve("uyumsoft");
            var lastKnown = await ResolveUyumsoftLastKnownSerialForSendAsync(
                adapter,
                _db,
                profile,
                tenantId,
                doc.BranchId,
                doc.DocumentType,
                issueDate,
                _config,
                ct);
            if (lastKnown <= 0)
            {
                doc.LastError = "Uyumsoft sıra numarası alınamadı. E-Fatura ayarlarından bağlantı testi yapın; sistem portal son numarayı otomatik okur.";
            }
            else
            {
                doc.InvoiceNumber = GibInvoiceNumber.BuildFromSerial(prefix, issueDate, lastKnown + 1);
            }
        }
        else
        {
            doc.InvoiceNumber = GibInvoiceNumber.Build(prefix, issueDate, invoice.Id);
        }

        var staleOutboxes = await _db.EInvoiceOutboxes
            .Where(x => x.TenantId == tenantId && x.DocumentId == doc.Id && x.Status == "Pending")
            .ToListAsync(ct);
        foreach (var stale in staleOutboxes)
        {
            stale.Status = "Done";
            stale.ProcessedAt = DateTime.UtcNow;
            stale.LastError = "Yeni gönderim isteği ile değiştirildi.";
            stale.LockedAt = null;
        }

        string payload;
        if (manualDraft is not null)
        {
            payload = await BuildPayloadJsonFromDraftAsync(invoice, doc, manualDraft, profile, ct);
        }
        else
        {
            payload = await BuildPayloadJsonAsync(invoice, customer, doc.InvoiceNumber, doc.DocumentType, profile, ct);
        }

        if (!isUyumsoft)
            payload = GibInvoiceNumber.PatchPayloadJson(payload, doc.InvoiceNumber, invoice.Id, issueDate, prefix) ?? payload;

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
        ScheduleImmediateProcessing(tenantId, invoiceId);
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
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == invoice.BranchId, ct);
        var (companyCity, companyDistrict) = ParseCityDistrictFromAddress(profile?.CompanyAddress);

        // Tahsilat kaynaklı (satışsız) faturalar: özel matrah + şube işçilik kuralları ile oluşturulur.
        var collectionMeta = TryDeserializeCollectionMeta(invoice.CollectionMetaJson);
        if (collectionMeta is not null)
        {
            collectionMeta = await RefreshCollectionDraftMetaAsync(
                tenantId,
                invoice.BranchId,
                collectionMeta,
                ct);
            await PersistCollectionDraftMetaAsync(tenantId, invoiceId, doc.Id, collectionMeta, ct);

            var collectionLines = await BuildCollectionSpecialMatrahLinesAsync(
                tenantId,
                invoice.BranchId,
                invoice.GrandTotal,
                ct);
            if (collectionLines.Count == 0)
                throw new InvalidOperationException("Tahsilat taslak faturası kalemleri oluşturulamadı.");

            return new ManualEInvoiceDraft(
                string.IsNullOrWhiteSpace(collectionMeta.DocumentType) ? doc.DocumentType : collectionMeta.DocumentType,
                string.IsNullOrWhiteSpace(collectionMeta.BuyerName) ? (customer?.FullName?.Trim() ?? string.Empty) : collectionMeta.BuyerName,
                string.IsNullOrWhiteSpace(collectionMeta.BuyerTaxNumber) ? NormalizeTaxNo(customer?.NationalId) : collectionMeta.BuyerTaxNumber!,
                collectionMeta.BuyerAddress ?? customer?.Address,
                CoalesceText(collectionMeta.BuyerCity, customer?.City, companyCity),
                CoalesceText(collectionMeta.BuyerDistrict, customer?.District, companyDistrict),
                ResolvePostalCodeFromText(collectionMeta.BuyerAddress ?? customer?.Address ?? profile?.CompanyAddress),
                invoice.InvoiceDate.ToLocalTime().ToString("dd.MM.yyyy"),
                invoice.InvoiceDate.ToLocalTime().ToString("HH:mm:ss"),
                !string.IsNullOrWhiteSpace(collectionMeta.ReceiverAlias)
                    ? collectionMeta.ReceiverAlias
                    : collectionMeta.BuyerEmail ?? customer?.Email,
                "TRY",
                collectionLines);
        }
        // İşçilik kuralları/KDV oranları yerel hesaplama ayarlarıdır; profil varsa IsActive durumundan
        // bağımsız uygulanır (şube başına tek profil garantili olduğu için ayrım gerekmez).
        var profileSettings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);

        var saleItems = invoice.SaleId.HasValue
            ? await _db.SaleItems
                .AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.SaleId == invoice.SaleId.Value)
                .OrderBy(x => x.LineNo)
                .ToListAsync(ct)
            : new List<SaleItem>();

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

        // Çoklu ödeme bölünmesi: bu faturanın toplam satışa oranı (1.0 = tam fatura).
        var splitRatio = invoice.PaymentSplitRatio > 0m && invoice.PaymentSplitRatio <= 1m
            ? invoice.PaymentSplitRatio
            : 1m;

        var lineNo = 1;
        foreach (var item in saleItems)
        {
            if (item.Kind == ItemKind.Forex)
                continue;

            itemMap.TryGetValue(item.ProductItemId ?? Guid.Empty, out var pItem);
            Product? product = null;
            if (pItem is not null)
                productMap.TryGetValue(pItem.ProductId, out product);

            // Miktar (gram/adet) ve tutar alanları split oranıyla ölçeklenir; birim fiyat değişmez.
            // Böylece çoklu ödeme bölünmesinde her fatura kendi ödeme tutarı kadar olur.
            var qty = (item.Quantity <= 0 ? 1m : item.Quantity) * splitRatio;
            var price = item.UnitPrice < 0 ? 0 : item.UnitPrice;
            var kdvRate = item.TaxRate > 1 ? item.TaxRate : item.TaxRate * 100m;
            var scaledDiscount = Math.Max(0, item.Discount) * splitRatio;
            var scaledLineTotal = item.LineTotal * splitRatio;
            var lineBase = Math.Max(0, qty * price - scaledDiscount);
            var kdv = Math.Round(lineBase * (kdvRate / 100m), 2, MidpointRounding.AwayFromZero);
            var total = scaledLineTotal > 0 ? scaledLineTotal : lineBase + kdv;
            var normalizedKarat = !string.IsNullOrWhiteSpace(item.Karat) ? item.Karat : pItem?.Karat ?? product?.Karat;
            var productCode = string.IsNullOrWhiteSpace(item.ProductCode) ? null : item.ProductCode.Trim();
            var workmanshipHas = product?.BirimSatisIscilikHas ?? 0m;
            var baseCostHas = product?.Cost ?? 0m;
            var normalizedCategory = string.IsNullOrWhiteSpace(item.Category) ? (product?.Category ?? string.Empty) : item.Category.Trim();
            var isZiynetSale = IsZiynetSarrafiye(item.ProductName, normalizedCategory, product);
            var isSpecialProductSale = IsSpecialProductSale(item.ProductName, normalizedCategory, product);
            var grossTotal = scaledLineTotal > 0 ? scaledLineTotal : total;
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

        var storedDraft = await TryLoadStoredDraftFromOutboxAsync(tenantId, doc.Id, ct);
        return new ManualEInvoiceDraft(
            CoalesceText(doc.DocumentType, storedDraft?.DocumentType),
            CoalesceText(storedDraft?.BuyerName, customer?.FullName),
            CoalesceText(storedDraft?.BuyerTaxNumber, NormalizeTaxNo(customer?.NationalId)),
            CoalesceText(storedDraft?.BuyerAddress, customer?.Address),
            CoalesceText(storedDraft?.BuyerCity, customer?.City, companyCity),
            CoalesceText(storedDraft?.BuyerDistrict, customer?.District, companyDistrict),
            CoalesceText(storedDraft?.BuyerPostalCode, ResolvePostalCodeFromText(storedDraft?.BuyerAddress ?? customer?.Address ?? profile?.CompanyAddress)),
            CoalesceText(storedDraft?.IssueDateText, invoice.InvoiceDate.ToLocalTime().ToString("dd.MM.yyyy")),
            CoalesceText(storedDraft?.IssueTimeText, invoice.InvoiceDate.ToLocalTime().ToString("HH:mm:ss")),
            CoalesceText(storedDraft?.BuyerEmail, customer?.Email),
            CoalesceText(storedDraft?.Currency, "TRY"),
            lines);
    }

    public void ScheduleImmediateProcessing(Guid tenantId, Guid invoiceId)
    {
        _immediateQueue.Enqueue(tenantId, invoiceId);
    }

    public async Task RefreshOutboxPayloadIfNeededAsync(EInvoiceDocument doc, EInvoiceOutbox outbox, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbox.PayloadJson) || string.IsNullOrWhiteSpace(doc.DocumentType))
            return;
        if (!IsPayloadUblProfileMismatch(doc.DocumentType, outbox.PayloadJson))
            return;

        var invoice = await _db.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.Id == doc.InvoiceId, ct);
        if (invoice is null)
            return;

        var draft = await BuildManualDraftAsync(doc.TenantId, doc.InvoiceId, ct);
        if (draft is null)
            return;

        var normalizedType = string.Equals(doc.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase)
            ? "EArsiv"
            : "EFatura";
        draft = draft with { DocumentType = normalizedType };

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId, ct);

        var freshPayload = await BuildPayloadJsonFromDraftAsync(invoice, doc, draft, profile, ct);
        if (!string.Equals(profile?.ProviderCode, "uyumsoft", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
                doc.DocumentType,
                profile?.DefaultInvoicePrefix,
                profile?.DefaultArchivePrefix);
            freshPayload = GibInvoiceNumber.PatchPayloadJson(
                freshPayload,
                doc.InvoiceNumber,
                invoice.Id,
                invoice.InvoiceDate.ToLocalTime().Date,
                prefix) ?? freshPayload;
        }

        outbox.PayloadJson = freshPayload;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<EInvoiceDocument?> TryProcessPendingImmediatelyAsync(Guid tenantId, Guid invoiceId, CancellationToken ct)
    {
        var doc = await _db.EInvoiceDocuments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.InvoiceId == invoiceId, ct);
        if (doc is null) return null;

        var now = DateTime.UtcNow;
        var staleLockBefore = now.AddSeconds(-30);
        var outbox = await _db.EInvoiceOutboxes
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId &&
                        x.DocumentId == doc.Id &&
                        x.Status == "Pending" &&
                        (x.NextAttemptAt <= now || x.RetryCount == 0) &&
                        (x.LockedAt == null || x.LockedAt < staleLockBefore))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (outbox is null) return doc;

        outbox.LockedAt = now;
        await _db.SaveChangesAsync(ct);

        try
        {
            var profile = await _db.EInvoiceProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId, ct);
            var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
            var adapter = _providerResolver.Resolve(providerCode);

            await RefreshOutboxPayloadIfNeededAsync(doc, outbox, ct);

            var payloadJson = PatchPayloadWithSoleProprietorName(outbox.PayloadJson, profile?.SoleProprietorName);
            var (buyerName, buyerTaxNo) = ParsePayload(payloadJson);
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
                payloadJson,
                profile?.IntegratorUsername,
                profile?.IntegratorSecretRef);

            var sendResult = await adapter.SendOutgoingAsync(sendReq, CancellationToken.None);
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
                if (!string.IsNullOrWhiteSpace(sendResult.Ettn))
                    doc.InvoiceNumber = sendResult.Ettn;
                doc.RawLastResponse = sendResult.RawResponse;
                doc.LastError = null;
                if (doc.Status is "Sent" or "Delivered")
                    doc.SubmittedAt ??= DateTime.UtcNow;
                if (doc.Status == "Delivered")
                    doc.DeliveredAt ??= DateTime.UtcNow;

                if (string.Equals(providerCode, "uyumsoft", StringComparison.OrdinalIgnoreCase))
                {
                    await UpdateProfileSeriesSerialAsync(
                        _db,
                        doc.TenantId,
                        doc.BranchId,
                        sendResult.Ettn ?? doc.InvoiceNumber,
                        ct);
                }

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
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId, ct);
        var prefix = GibInvoiceNumber.ResolvePrefixForDocumentType(docType, profile?.DefaultInvoicePrefix, profile?.DefaultArchivePrefix);
        return BuildInvoiceNumber(prefix, date, id);
    }

    private async Task<string> BuildPayloadJsonAsync(
        Invoice invoice,
        Customer? customer,
        string invoiceNo,
        string docType,
        EInvoiceProfile? profile,
        CancellationToken ct)
    {
        profile ??= await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.BranchId == invoice.BranchId, ct);
        var ubl = await _ublBuilder.BuildOutgoingAsync(invoice, customer, profile, invoiceNo, docType, ct);
        var seriesPrefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
            docType,
            profile?.DefaultInvoicePrefix,
            profile?.DefaultArchivePrefix);

        var payloadObj = new
        {
            invoiceId = invoice.Id,
            invoiceNo,
            invoiceDateUtc = invoice.InvoiceDate,
            documentType = docType,
            seriesPrefix,
            invoiceSeriesPrefix = profile?.DefaultInvoicePrefix,
            archiveSeriesPrefix = profile?.DefaultArchivePrefix,
            tenantId = invoice.TenantId,
            branchId = invoice.BranchId,
            grandTotal = invoice.GrandTotal,
            currency = "TRY",
            senderVkn = ubl.SellerTaxNumber,
            senderAlias = ubl.SellerAlias,
            receiverVkn = ubl.BuyerTaxNumber,
            receiverAlias = ubl.BuyerAlias,
            buyerEmail = ubl.BuyerAlias,
            soleProprietorName = profile?.SoleProprietorName,
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

    private async Task<string> BuildPayloadJsonFromDraftAsync(
        Invoice invoice,
        EInvoiceDocument doc,
        ManualEInvoiceDraft draft,
        EInvoiceProfile? profile,
        CancellationToken ct)
    {
        profile ??= await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == invoice.TenantId && x.BranchId == invoice.BranchId, ct);
        var ubl = await _ublBuilder.BuildOutgoingFromDraftAsync(invoice, profile, doc.InvoiceNumber, draft, ct);
        var seriesPrefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
            doc.DocumentType,
            profile?.DefaultInvoicePrefix,
            profile?.DefaultArchivePrefix);

        var payloadObj = new
        {
            invoiceId = invoice.Id,
            invoiceNo = doc.InvoiceNumber,
            invoiceDateUtc = invoice.InvoiceDate,
            documentType = doc.DocumentType,
            seriesPrefix,
            invoiceSeriesPrefix = profile?.DefaultInvoicePrefix,
            archiveSeriesPrefix = profile?.DefaultArchivePrefix,
            tenantId = invoice.TenantId,
            branchId = invoice.BranchId,
            grandTotal = invoice.GrandTotal,
            currency = string.IsNullOrWhiteSpace(draft.Currency) ? "TRY" : draft.Currency,
            senderVkn = ubl.SellerTaxNumber,
            senderAlias = ubl.SellerAlias,
            receiverVkn = ubl.BuyerTaxNumber,
            receiverAlias = ubl.BuyerAlias,
            buyerEmail = draft.BuyerEmail,
            soleProprietorName = profile?.SoleProprietorName,
            ublBase64 = ubl.UblBase64,
            ublXml = ubl.UblXml,
            draft
        };

        return JsonSerializer.Serialize(payloadObj);
    }

    private static string BuildInvoiceNumber(string? prefix, DateTime date, Guid id)
        => GibInvoiceNumber.Build(prefix, date.ToLocalTime().Date, id);

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

    private static (string? City, string? District) ParseCityDistrictFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return (null, null);

        var text = address.Trim();
        var slashMatch = Regex.Match(text, @"([^\s/]+)\s*/\s*([^\s/]+)\s*$", RegexOptions.IgnoreCase);
        if (slashMatch.Success)
        {
            var district = slashMatch.Groups[1].Value.Trim().ToUpperInvariant();
            var city = slashMatch.Groups[2].Value.Trim().ToUpperInvariant();
            return (city, district);
        }

        return (null, null);
    }

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task<ManualEInvoiceDraft?> TryLoadStoredDraftFromOutboxAsync(Guid tenantId, Guid documentId, CancellationToken ct)
    {
        var payloadJson = await _db.EInvoiceOutboxes.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.DocumentId == documentId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);
        return TryDeserializeManualDraftFromPayload(payloadJson);
    }

    private static ManualEInvoiceDraft? TryDeserializeManualDraftFromPayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("draft", out var draftEl) && !root.TryGetProperty("Draft", out draftEl))
                return null;
            if (draftEl.ValueKind != JsonValueKind.Object)
                return null;
            return JsonSerializer.Deserialize<ManualEInvoiceDraft>(draftEl.GetRawText(), PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string CoalesceText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return string.Empty;
    }

    private static bool IsPayloadUblProfileMismatch(string? documentType, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(documentType))
            return false;

        var isEArchive = string.Equals(documentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
        var expectedProfileId = isEArchive ? "EARSIVFATURA" : "TEMELFATURA";
        var ublXml = ExtractUblXmlFromPayload(payloadJson);
        if (string.IsNullOrWhiteSpace(ublXml))
            return false;

        var match = Regex.Match(
            ublXml,
            @"<(?:cbc:)?ProfileID\b[^>]*>(?<id>[^<]+)</(?:cbc:)?ProfileID>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));
        if (!match.Success)
            return true;

        return !string.Equals(match.Groups["id"].Value.Trim(), expectedProfileId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractUblXmlFromPayload(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (TryFindJsonString(doc.RootElement, "ublXml", out var xml) && !string.IsNullOrWhiteSpace(xml))
                return xml;
            if (TryFindJsonString(doc.RootElement, "ublBase64", out var base64) && !string.IsNullOrWhiteSpace(base64))
            {
                var bytes = Convert.FromBase64String(base64);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryFindJsonString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    value = prop.Value.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                if ((prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array) &&
                    TryFindJsonString(prop.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindJsonString(item, name, out value))
                    return true;
            }
        }

        return false;
    }

    internal static string PatchPayloadWithSoleProprietorName(string payloadJson, string? soleProprietorName)
    {
        if (string.IsNullOrWhiteSpace(soleProprietorName) || string.IsNullOrWhiteSpace(payloadJson))
            return payloadJson;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("soleProprietorName", out var existing) &&
                !string.IsNullOrWhiteSpace(existing.GetString()))
                return payloadJson;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
                writer.WriteString("soleProprietorName", soleProprietorName.Trim());
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return payloadJson;
        }
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

    internal static async Task<int> ResolveUyumsoftLastKnownSerialForSendAsync(
        IEInvoiceProviderAdapter adapter,
        AppDbContext db,
        EInvoiceProfile? profile,
        Guid tenantId,
        Guid branchId,
        string documentType,
        DateTime issueDate,
        IConfiguration? config,
        CancellationToken ct)
    {
        var prefix = GibInvoiceNumber.ResolvePrefixForDocumentType(
            documentType,
            profile?.DefaultInvoicePrefix,
            profile?.DefaultArchivePrefix);
        var localNumbers = await db.EInvoiceDocuments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.InvoiceNumber.StartsWith(prefix)
                        && (x.Status == "IntegratorDraft"
                            || x.Status == "Sent"
                            || x.Status == "Delivered"))
            .Select(x => x.InvoiceNumber)
            .ToListAsync(ct);
        var bootstrap = ReadUyumsoftBootstrapSerial(config, prefix, issueDate.Year);
        var localMax = GibInvoiceNumber.GetMaxSerial(prefix, issueDate.Year, localNumbers, bootstrap);
        var lastKnown = Math.Max(bootstrap, localMax);

        if (!string.Equals(profile?.ProviderCode, "uyumsoft", StringComparison.OrdinalIgnoreCase))
            return lastKnown;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            var syncResult = await adapter.QuerySeriesCounterAsync(
                profile?.IntegratorUsername,
                profile?.IntegratorSecretRef,
                prefix,
                issueDate.Year,
                GibInvoiceNumber.IsEArchiveDocumentType(documentType),
                timeoutCts.Token);
            if (syncResult.IsSuccess && syncResult.LastSerial.HasValue)
            {
                lastKnown = syncResult.LastSerial.Value;
                await UpdateProfileSeriesSerialAsync(
                    db,
                    tenantId,
                    branchId,
                    GibInvoiceNumber.BuildFromSerial(prefix, issueDate, syncResult.LastSerial.Value),
                    ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Portal sorgusu zaman aşımına uğradı; bootstrap + yerel başarılı kayıtlar kullanılır.
        }

        return lastKnown;
    }

    internal static int ReadUyumsoftBootstrapSerial(IConfiguration? config, string prefix, int year)
    {
        if (config is null || year <= 0)
            return 0;

        var underscoreKey = $"{prefix}_{year}";
        var serial = config.GetValue<int?>($"EInvoice:Uyumsoft:SeriesBootstrap:{underscoreKey}");
        if (serial > 0)
            return serial.Value;

        return config.GetValue<int?>($"EInvoice:Uyumsoft:SeriesBootstrap:{prefix}:{year}") ?? 0;
    }

    internal static string NormalizeStatus(string? providerStatus, string fallback)
    {
        var value = (providerStatus ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "queued" => "Queued",
            "sent" => "Sent",
            "integratordraft" => "IntegratorDraft",
            "draft" => "IntegratorDraft",
            "delivered" => "Delivered",
            "rejected" => "Rejected",
            "cancelpending" => "CancelPending",
            "cancelled" => "Cancelled",
            "failed" => "Failed",
            _ => fallback
        };
    }

    internal static int ResolveUyumsoftLastKnownSeriesSerial(
        EInvoiceProfile? profile,
        string prefix,
        int year,
        IEnumerable<string?> localInvoiceNumbers,
        int anchorSerial = 0)
    {
        var settings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        var profileSerial = EInvoiceProfileSettingsCodec.GetSeriesLastSerial(settings, prefix, year);
        var lastKnown = Math.Max(profileSerial, anchorSerial);

        foreach (var number in localInvoiceNumbers)
        {
            if (!GibInvoiceNumber.TryExtractSerialParts(number, out var numberPrefix, out var numberYear, out var serial))
                continue;
            if (!string.Equals(numberPrefix, prefix, StringComparison.OrdinalIgnoreCase) || numberYear != year)
                continue;
            if (GibInvoiceNumber.IsLikelyGeneratedSerial(serial, lastKnown > 0 ? lastKnown : anchorSerial))
                continue;
            if (serial > lastKnown)
                lastKnown = serial;
        }

        return lastKnown;
    }

    internal static async Task UpdateProfileSeriesSerialAsync(
        AppDbContext db,
        Guid tenantId,
        Guid branchId,
        string? invoiceNumber,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            return;

        var profile = await db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId, ct);
        if (profile is null)
            return;

        var settings = EInvoiceProfileSettingsCodec.Decode(profile.IntegratorCompanyCode);
        EInvoiceProfileSettingsCodec.SetSeriesLastSerial(settings, invoiceNumber);
        profile.IntegratorCompanyCode = EInvoiceProfileSettingsCodec.Encode(settings);
    }
}
