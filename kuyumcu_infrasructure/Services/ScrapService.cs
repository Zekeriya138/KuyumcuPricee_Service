using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_domain.Scrap;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace kuyumcu_infrastructure.Services;

public sealed class ScrapService : IScrapService
{
    private readonly AppDbContext _db;
    private readonly IStockService _stock;

    public ScrapService(AppDbContext db, IStockService stock)
    {
        _db = db;
        _stock = stock;
    }

    public async Task<ScrapDashboardDto> GetDashboardAsync(Guid tenantId, Guid branchId, CancellationToken ct = default)
    {
        var rows = await _db.ScrapStocks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && (x.WeightGram > 0 || x.PureGoldGram > 0))
            .OrderBy(x => x.Karat)
            .ToListAsync(ct);

        var lines = rows.Select(r => new ScrapStockLineDto(r.Karat, r.WeightGram, r.PureGoldGram)).ToList();
        return new ScrapDashboardDto(
            lines.Sum(x => x.WeightGram),
            lines.Sum(x => x.PureGoldGram),
            lines);
    }

    public async Task AddFromPurchaseItemsAsync(Guid tenantId, Purchase purchase, CancellationToken ct = default)
    {
        var groups = new Dictionary<string, (decimal Weight, decimal Pure, decimal AmountTl)>(StringComparer.OrdinalIgnoreCase);

        foreach (var it in purchase.Items.Where(i => i.Kind == ItemKind.Scrap))
        {
            var karatKey = ScrapPureGoldCalculator.NormalizeKaratKey(it.Karat);
            if (string.IsNullOrEmpty(karatKey) || it.Quantity <= 0) continue;

            var pure = ScrapPureGoldCalculator.ComputePureGoldGrams(it.Quantity, it.Karat);
            var amount = it.LineTotal > 0 ? it.LineTotal : 0m;

            if (groups.TryGetValue(karatKey, out var g))
                groups[karatKey] = (g.Weight + it.Quantity, g.Pure + pure, g.AmountTl + amount);
            else
                groups[karatKey] = (it.Quantity, pure, amount);
        }

        foreach (var kv in groups)
        {
            var amountTl = kv.Value.AmountTl > 0 ? Math.Round(kv.Value.AmountTl, 2, MidpointRounding.AwayFromZero) : (decimal?)null;
            await UpsertAddAsync(tenantId, purchase.BranchId, kv.Key, kv.Value.Weight, kv.Value.Pure, ScrapLedgerType.CustomerPurchase,
                purchase.CustomerId, purchase.Id, amountTl, null, null, ct);
        }
    }

    public Task<(bool ok, string? error)> TryConsumeForGoldPaymentAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        Guid? purchaseId,
        CancellationToken ct = default)
        => TrySubtractScrapAsync(tenantId, branchId, karatRaw, weightGram, ScrapLedgerType.SupplierPaymentOut, purchaseId,
            "Tedarikçi alışı hurda ödeme", ct);

    public Task<(bool ok, string? error)> TryManualUseAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        string? note,
        CancellationToken ct = default)
        => TrySubtractScrapAsync(tenantId, branchId, karatRaw, weightGram, ScrapLedgerType.ManualUseOut, null, note, ct);

    /// <inheritdoc />
    public Task<(bool ok, string? error)> TrySubtractScrapForPurchaseLineBarcodeAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        Guid purchaseId,
        string? note,
        CancellationToken ct = default)
        => TrySubtractScrapAsync(tenantId, branchId, karatRaw, weightGram, ScrapLedgerType.ConvertToProductOut, purchaseId, note, ct);

    public async Task EnsureScrapStockFromPurchaseItemAsync(
        Guid tenantId,
        Guid branchId,
        PurchaseItem purchaseItem,
        CancellationToken ct = default)
    {
        if (purchaseItem.Kind != ItemKind.Scrap || purchaseItem.Quantity <= 0m) return;

        var karatKey = ScrapPureGoldCalculator.NormalizeKaratKey(purchaseItem.Karat);
        if (string.IsNullOrEmpty(karatKey)) return;

        var purchaseId = purchaseItem.PurchaseId;
        if (purchaseId == Guid.Empty) return;

        var hasLedger = await _db.ScrapLedgers.AsNoTracking()
            .AnyAsync(x =>
                x.TenantId == tenantId
                && x.BranchId == branchId
                && x.PurchaseId == purchaseId
                && x.Karat == karatKey
                && x.Kind == ScrapLedgerKind.PurchaseIn, ct);
        if (hasLedger) return;

        var pure = ScrapPureGoldCalculator.ComputePureGoldGrams(purchaseItem.Quantity, purchaseItem.Karat);
        var amountTl = purchaseItem.LineTotal > 0m
            ? Math.Round(purchaseItem.LineTotal, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        Guid? customerId = null;
        if (purchaseItem.Purchase != null)
            customerId = purchaseItem.Purchase.CustomerId;

        await UpsertAddAsync(
            tenantId,
            branchId,
            karatKey,
            purchaseItem.Quantity,
            pure,
            ScrapLedgerType.CustomerPurchase,
            customerId,
            purchaseId,
            amountTl,
            null,
            $"Hurda alis satir L{purchaseItem.LineNo} (gecikmeli stok)",
            ct);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<(bool ok, Guid? purchaseId, string? error)> RecordCustomerScrapPurchaseAsync(
        Guid tenantId,
        Guid branchId,
        Guid userId,
        Guid customerId,
        string karatRaw,
        decimal weightGram,
        decimal goldPricePerGram,
        int paymentMethod,
        string? note,
        CancellationToken ct = default)
    {
        if (weightGram <= 0) return (false, null, "Gram pozitif olmalıdır.");
        if (goldPricePerGram < 0) return (false, null, "Birim fiyat geçersiz.");
        var karatKey = ScrapPureGoldCalculator.NormalizeKaratKey(karatRaw);
        if (string.IsNullOrEmpty(karatKey)) return (false, null, "Ayar bilgisi geçersiz.");

        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == branchId && b.TenantId == tenantId, ct);
        if (branch is null) return (false, null, "Şube bulunamadı.");

        var cust = await _db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && c.TenantId == tenantId && !c.IsDeleted, ct);
        if (cust is null) return (false, null, "Müşteri bulunamadı.");

        var lineTotal = Math.Round(weightGram * goldPricePerGram, 2);
        var pure = ScrapPureGoldCalculator.ComputePureGoldGrams(weightGram, karatRaw);

        var purchase = new Purchase
        {
            TenantId = tenantId,
            BranchId = branchId,
            UserId = userId,
            CustomerId = customerId,
            PurchaseType = PurchaseType.Musteri,
            PaymentMethod = (PurchasePaymentMethod)Math.Clamp(paymentMethod, 0, 4),
            Date = DateTime.UtcNow,
            Note = note,
            Subtotal = lineTotal,
            DiscountTotal = 0,
            TaxTotal = 0,
            GrandTotal = lineTotal,
            TotalAmount = lineTotal,
            TotalHas = pure
        };

        purchase.Items.Add(new PurchaseItem
        {
            TenantId = tenantId,
            LineNo = 1,
            Kind = ItemKind.Scrap,
            ProductCode = "HURDA",
            ProductName = "Müşteri hurda alışı",
            Karat = karatKey,
            Category = "Hurda",
            Quantity = weightGram,
            UnitCost = goldPricePerGram,
            Discount = 0,
            TaxRate = 0,
            LineTotal = lineTotal
        });

        _db.Purchases.Add(purchase);
        await _db.SaveChangesAsync(ct);

        await UpsertAddAsync(tenantId, branchId, karatKey, weightGram, pure, ScrapLedgerType.CustomerPurchase,
            customerId, purchase.Id, lineTotal, goldPricePerGram, note, ct);
        await _db.SaveChangesAsync(ct);

        return (true, purchase.Id, null);
    }

    public async Task<(bool ok, string? error)> RefineToProductItemsAsync(
        Guid tenantId,
        Guid branchId,
        Guid userId,
        string fromKaratRaw,
        decimal scrapWeightGram,
        Guid targetProductId,
        int itemCount,
        string outputKaratRaw,
        decimal? goldPricePerGram,
        string? note,
        CancellationToken ct = default)
    {
        _ = userId;
        if (scrapWeightGram <= 0 || itemCount <= 0) return (false, "Gram ve adet pozitif olmalıdır.");

        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == targetProductId && p.TenantId == tenantId && p.BranchId == branchId, ct);
        if (product is null) return (false, "Ürün bulunamadı.");

        var outKey = ScrapPureGoldCalculator.NormalizeKaratKey(outputKaratRaw);
        if (string.IsNullOrEmpty(outKey)) return (false, "Hedef ayar geçersiz.");

        var pureIn = ScrapPureGoldCalculator.ComputePureGoldGrams(scrapWeightGram, fromKaratRaw);
        var fOut = ScrapPureGoldCalculator.FinenessFromKarat(outKey);
        if (fOut <= 0) return (false, "Hedef ayar fineness hesaplanamadı.");

        var totalOutWeight = Math.Round(pureIn / fOut, 4);
        var perItem = Math.Round(totalOutWeight / itemCount, 4);
        if (perItem <= 0) return (false, "Parça başına ağırlık sıfır.");

        var sub = await TrySubtractScrapAsync(tenantId, branchId, fromKaratRaw, scrapWeightGram, ScrapLedgerType.RefineOut,
            null, note ?? $"Rafine → {itemCount} adet {product.ProductCode}", ct);
        if (!sub.ok) return sub;

        var unitCost = goldPricePerGram.HasValue
            ? Math.Round((scrapWeightGram * goldPricePerGram.Value) / itemCount, 2)
            : Math.Round(product.Cost ?? 0m, 2);

        for (var i = 0; i < itemCount; i++)
        {
            var pi = new ProductItem
            {
                TenantId = tenantId,
                ProductId = targetProductId,
                BranchId = branchId,
                Barcode = GenerateBarcode("RF"),
                Serial = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                Karat = outKey,
                Weight = perItem,
                Cost = unitCost,
                IsInStock = true
            };
            _db.ProductItems.Add(pi);
            await _db.SaveChangesAsync(ct);

            await _stock.AdjustAsync(
                branchId,
                targetProductId,
                pi.Id,
                +1m,
                StockRefKind.ScrapRefine,
                pi.Id,
                note ?? $"Hurda rafine → {product.ProductCode}",
                ct);
        }

        return (true, null);
    }

    private async Task<(bool ok, string? error)> TrySubtractScrapAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        ScrapLedgerType ledgerType,
        Guid? purchaseId,
        string? note,
        CancellationToken ct)
    {
        if (weightGram <= 0) return (true, null);
        var karatKey = ScrapPureGoldCalculator.NormalizeKaratKey(karatRaw);
        if (string.IsNullOrEmpty(karatKey)) return (false, "Geçerli ayar seçin.");

        var pure = ScrapPureGoldCalculator.ComputePureGoldGrams(weightGram, karatRaw);
        var row = await _db.ScrapStocks.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.BranchId == branchId && x.Karat == karatKey && !x.IsDeleted, ct);
        if (row is null || row.WeightGram < weightGram - 0.0001m)
            return (false, $"Yetersiz hurda stoğu ({karatKey}). Mevcut: {row?.WeightGram ?? 0:N3} g, istenen: {weightGram:N3} g.");

        row.WeightGram = Math.Round(row.WeightGram - weightGram, 4);
        row.PureGoldGram = Math.Max(0, Math.Round(row.PureGoldGram - pure, 4));
        row.UpdatedAt = DateTime.UtcNow;

        _db.ScrapLedgers.Add(new ScrapLedger
        {
            TenantId = tenantId,
            BranchId = branchId,
            Kind = MapLedgerKind(ledgerType),
            Karat = karatKey,
            DeltaWeightGram = -weightGram,
            DeltaPureGoldGram = -pure,
            PurchaseId = purchaseId,
            Note = note
        });

        return (true, null);
    }

    private static string GenerateBarcode(string prefix)
    {
        var ts = DateTime.UtcNow.ToString("yyMMddHHmm");
        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("=", "").Replace("+", "").Replace("/", "")
            .ToUpperInvariant();
        return $"{prefix}-{ts}-{rnd[..6]}";
    }

    private async Task UpsertAddAsync(
        Guid tenantId,
        Guid branchId,
        string karatKey,
        decimal dWeight,
        decimal dPure,
        ScrapLedgerType type,
        Guid? customerId,
        Guid? purchaseId,
        decimal? amountTl,
        decimal? goldPrice,
        string? note,
        CancellationToken ct)
    {
        var row = await _db.ScrapStocks.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.BranchId == branchId && x.Karat == karatKey && !x.IsDeleted, ct);
        if (row is null)
        {
            row = new ScrapStock
            {
                TenantId = tenantId,
                BranchId = branchId,
                Karat = karatKey,
                WeightGram = 0,
                PureGoldGram = 0
            };
            _db.ScrapStocks.Add(row);
        }

        row.WeightGram = Math.Round(row.WeightGram + dWeight, 4);
        row.PureGoldGram = Math.Round(row.PureGoldGram + dPure, 4);
        row.UpdatedAt = DateTime.UtcNow;

        _db.ScrapLedgers.Add(new ScrapLedger
        {
            TenantId = tenantId,
            BranchId = branchId,
            Kind = MapLedgerKind(type),
            Karat = karatKey,
            DeltaWeightGram = dWeight,
            DeltaPureGoldGram = dPure,
            AmountTl = amountTl,
            GoldPricePerGram = goldPrice,
            CustomerId = customerId,
            PurchaseId = purchaseId,
            Note = note
        });
    }

    private static ScrapLedgerKind MapLedgerKind(ScrapLedgerType t) => t switch
    {
        ScrapLedgerType.CustomerPurchase => ScrapLedgerKind.PurchaseIn,
        ScrapLedgerType.SupplierPaymentOut => ScrapLedgerKind.UseOut,
        ScrapLedgerType.RefineOut => ScrapLedgerKind.RefineOut,
        ScrapLedgerType.ConvertToProductOut => ScrapLedgerKind.ConvertOut,
        ScrapLedgerType.AdjustmentIn => ScrapLedgerKind.PurchaseIn,
        ScrapLedgerType.AdjustmentOut => ScrapLedgerKind.UseOut,
        ScrapLedgerType.ManualUseOut => ScrapLedgerKind.UseOut,
        _ => ScrapLedgerKind.UseOut
    };
}
