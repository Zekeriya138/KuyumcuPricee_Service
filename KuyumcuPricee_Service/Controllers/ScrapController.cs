using System.Security.Claims;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_domain.Helpers;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using KUYUMCU.Price_Service.Models;
using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

/// <summary>Müşteri hurda stoku (ScrapStock), defter ve rafine/ürüne çevirme.</summary>
[ApiController]
[Route("api/scrap")]
[Authorize]
public sealed class ScrapController : ControllerBase
{
    private const string LedgerProductCode = "__SCRAP_LEDGER__";
    private readonly AppDbContext _db;
    private readonly IStockService _stock;
    private readonly IScrapService _scrap;
    private readonly ITenantContext _tenant;

    public ScrapController(AppDbContext db, IStockService stock, IScrapService scrap, ITenantContext tenant)
    {
        _db = db;
        _stock = stock;
        _scrap = scrap;
        _tenant = tenant;
    }

    private Guid TenantId => GetTenantId();

    private bool TryGetRequiredBranchId(out Guid branchId, out IActionResult? error)
    {
        branchId = _tenant.BranchId ?? Guid.Empty;
        if (branchId == Guid.Empty)
        {
            var claim = User?.Claims?.FirstOrDefault(c =>
                c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
            if (Guid.TryParse(claim, out var c) && c != Guid.Empty) branchId = c;
        }
        if (branchId == Guid.Empty)
        {
            error = BadRequest(new { error = "Şube bilgisi eksik." });
            return false;
        }
        error = null;
        return true;
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
        throw new InvalidOperationException("TenantId missing.");
    }

    /// <summary>PurchasesController.NormalizeAyar ile aynı kurallar.</summary>
    private static string NormalizeKarat(string? karat)
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

    private async Task<Guid> EnsureLedgerProductIdAsync(Guid branchId, CancellationToken ct)
    {
        var tid = TenantId;
        var p = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == branchId && x.ProductCode == LedgerProductCode, ct);
        if (p != null) return p.Id;

        var np = new Product
        {
            TenantId = tid,
            BranchId = branchId,
            ProductCode = LedgerProductCode,
            Name = "Sistem: Hurda hareket",
            Category = "Sistem",
            Karat = "—",
            InventoryType = InventoryType.Tekil
        };
        _db.Products.Add(np);
        await _db.SaveChangesAsync(ct);
        return np.Id;
    }

    private static string GenBarcode()
    {
        var ts = DateTime.UtcNow.ToString("yyMMddHHmm");
        var rnd = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("=", "").Replace("+", "").Replace("/", "")
            .ToUpperInvariant();
        return $"PI-{ts}-{rnd[..6]}";
    }

    // ---------- GET stock ----------
    public sealed record ScrapLineDto(string Karat, decimal WeightGram, decimal PureGoldGram, DateTime UpdatedAt);
    public sealed record ScrapStockResponseDto(
        List<ScrapLineDto> Lines,
        decimal TotalWeightGram,
        decimal TotalPureGoldGram);

    [HttpGet("stock")]
    public async Task<IActionResult> GetStock([FromQuery] Guid? branchId, CancellationToken ct)
    {
        var tid = TenantId;
        var bid = branchId ?? await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tid)
            .Select(b => b.Id)
            .FirstOrDefaultAsync(ct);
        if (bid == Guid.Empty)
            return BadRequest(new { error = "Şube bulunamadı." });

        var rows = await _db.ScrapStocks.AsNoTracking()
            .Where(x => x.TenantId == tid && x.BranchId == bid && !x.IsDeleted && x.WeightGram > 0)
            .OrderBy(x => x.Karat)
            .ToListAsync(ct);

        var lines = rows.Select(x => new ScrapLineDto(x.Karat, x.WeightGram, x.PureGoldGram, x.UpdatedAt)).ToList();
        return Ok(new ScrapStockResponseDto(
            lines,
            rows.Sum(x => x.WeightGram),
            rows.Sum(x => x.PureGoldGram)));
    }

    // ---------- POST purchase ----------
    public sealed record ScrapPurchaseReq(
        Guid BranchId,
        Guid? CustomerId,
        string Karat,
        decimal WeightGram,
        decimal GoldPricePerGram,
        int PaymentType, // 0 nakit 1 banka 2 veresiye (bilgi amaçlı)
        string? Note);

    [HttpPost("purchase")]
    public async Task<IActionResult> Purchase([FromBody] ScrapPurchaseReq req, CancellationToken ct)
    {
        if (req.WeightGram <= 0 || req.GoldPricePerGram < 0)
            return BadRequest(new { error = "Geçerli gram ve fiyat girin." });
        var k = NormalizeKarat(req.Karat);
        if (string.IsNullOrEmpty(k))
            return BadRequest(new { error = "Geçerli ayar seçin." });

        var tid = TenantId;
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tid, ct);
        if (branch is null)
            return BadRequest(new { error = "Geçersiz şube." });

        if (req.CustomerId.HasValue)
        {
            var cust = await _db.Customers.AsNoTracking()
                .AnyAsync(c => c.Id == req.CustomerId.Value && c.TenantId == tid && !c.IsDeleted, ct);
            if (!cust) return BadRequest(new { error = "Müşteri bulunamadı." });
        }

        var pureAdd = ScrapGoldMath.PureGoldGrams(req.WeightGram, k);
        var amountTl = Math.Round(req.WeightGram * req.GoldPricePerGram, 2);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await _db.ScrapStocks
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId && x.Karat == k && !x.IsDeleted, ct);
            if (row is null)
            {
                row = new ScrapStock
                {
                    TenantId = tid,
                    BranchId = req.BranchId,
                    Karat = k,
                    WeightGram = 0,
                    PureGoldGram = 0
                };
                _db.ScrapStocks.Add(row);
            }

            row.WeightGram += req.WeightGram;
            row.PureGoldGram = ScrapGoldMath.PureGoldGrams(row.WeightGram, k);
            row.UpdatedAt = DateTime.UtcNow;

            _db.ScrapLedgers.Add(new ScrapLedger
            {
                TenantId = tid,
                BranchId = req.BranchId,
                Kind = ScrapLedgerKind.PurchaseIn,
                Karat = k,
                DeltaWeightGram = req.WeightGram,
                DeltaPureGoldGram = pureAdd,
                GoldPricePerGram = req.GoldPricePerGram,
                AmountTl = amountTl,
                CustomerId = req.CustomerId,
                Note = req.Note
            });

            await _db.SaveChangesAsync(ct);

            var pid = await EnsureLedgerProductIdAsync(req.BranchId, ct);
            await _stock.AdjustAsync(
                req.BranchId,
                pid,
                null,
                +req.WeightGram,
                StockRefKind.ScrapPurchase,
                Guid.NewGuid(),
                $"Hurda alış {k} {req.WeightGram:N3} gr",
                ct);

            await tx.CommitAsync(ct);
            return Ok(new { pureGoldGram = pureAdd, amountTl, karat = k });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---------- POST use ----------
    public sealed record ScrapUseReq(
        Guid BranchId,
        string Karat,
        decimal WeightGram,
        decimal? GoldPricePerGram,
        Guid? PurchaseId,
        string? Note);

    [HttpPost("use")]
    public async Task<IActionResult> Use([FromBody] ScrapUseReq req, CancellationToken ct)
    {
        if (req.WeightGram <= 0)
            return BadRequest(new { error = "Gram pozitif olmalıdır." });
        var k = NormalizeKarat(req.Karat);
        if (string.IsNullOrEmpty(k))
            return BadRequest(new { error = "Geçerli ayar seçin." });

        var tid = TenantId;
        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == req.BranchId && b.TenantId == tid, ct);
        if (branch is null)
            return BadRequest(new { error = "Geçersiz şube." });

        var pureOut = ScrapGoldMath.PureGoldGrams(req.WeightGram, k);
        var amountTl = req.GoldPricePerGram.HasValue
            ? Math.Round(req.WeightGram * req.GoldPricePerGram.Value, 2)
            : (decimal?)null;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await _db.ScrapStocks
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId && x.Karat == k && !x.IsDeleted, ct);
            if (row is null || row.WeightGram < req.WeightGram)
            {
                await tx.RollbackAsync(ct);
                return BadRequest(new { error = "Yetersiz hurda stoku.", karat = k, requested = req.WeightGram, available = row?.WeightGram ?? 0 });
            }

            row.WeightGram -= req.WeightGram;
            row.PureGoldGram = ScrapGoldMath.PureGoldGrams(row.WeightGram, k);
            row.UpdatedAt = DateTime.UtcNow;

            _db.ScrapLedgers.Add(new ScrapLedger
            {
                TenantId = tid,
                BranchId = req.BranchId,
                Kind = ScrapLedgerKind.UseOut,
                Karat = k,
                DeltaWeightGram = -req.WeightGram,
                DeltaPureGoldGram = -pureOut,
                GoldPricePerGram = req.GoldPricePerGram,
                AmountTl = amountTl,
                PurchaseId = req.PurchaseId,
                Note = req.Note
            });

            await _db.SaveChangesAsync(ct);

            var pid = await EnsureLedgerProductIdAsync(req.BranchId, ct);
            await _stock.AdjustAsync(
                req.BranchId,
                pid,
                null,
                -req.WeightGram,
                StockRefKind.ScrapPayment,
                req.PurchaseId ?? Guid.NewGuid(),
                $"Hurda kullanım {k} {req.WeightGram:N3} gr",
                ct);

            await tx.CommitAsync(ct);
            return Ok(new { pureGoldGram = pureOut, amountTl });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---------- POST refine ----------
    public sealed record ScrapRefineReq(
        Guid BranchId,
        string FromKarat,
        decimal WeightGram,
        string TargetAyar,
        decimal? CostPerGramHas);

    [HttpPost("refine")]
    public async Task<IActionResult> Refine([FromBody] ScrapRefineReq req, CancellationToken ct)
    {
        if (req.WeightGram <= 0)
            return BadRequest(new { error = "Gram pozitif olmalıdır." });
        var fromK = NormalizeKarat(req.FromKarat);
        var target = NormalizeKarat(req.TargetAyar);
        if (string.IsNullOrEmpty(fromK) || string.IsNullOrEmpty(target))
            return BadRequest(new { error = "Geçerli ayar girin." });

        var tid = TenantId;
        var pure = ScrapGoldMath.PureGoldGrams(req.WeightGram, fromK);
        var costPer = req.CostPerGramHas ?? 0m;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await _db.ScrapStocks
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId && x.Karat == fromK && !x.IsDeleted, ct);
            if (row is null || row.WeightGram < req.WeightGram)
            {
                await tx.RollbackAsync(ct);
                return BadRequest(new { error = "Yetersiz hurda.", available = row?.WeightGram ?? 0 });
            }

            row.WeightGram -= req.WeightGram;
            row.PureGoldGram = ScrapGoldMath.PureGoldGrams(row.WeightGram, fromK);
            row.UpdatedAt = DateTime.UtcNow;

            _db.ScrapLedgers.Add(new ScrapLedger
            {
                TenantId = tid,
                BranchId = req.BranchId,
                Kind = ScrapLedgerKind.RefineOut,
                Karat = fromK,
                DeltaWeightGram = -req.WeightGram,
                DeltaPureGoldGram = -pure,
                Note = $"Rafine → {target}"
            });

            var depo = await _db.DepoStoklar
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId && x.Ayar == target && !x.IsDeleted, ct);
            if (depo is null)
            {
                depo = new DepoStok
                {
                    TenantId = tid,
                    BranchId = req.BranchId,
                    Ayar = target,
                    TotalGram = 0,
                    BarcodedGram = 0,
                    UnbarcodedGram = 0,
                    OrtalamaMaliyet = costPer
                };
                _db.DepoStoklar.Add(depo);
            }
            depo.Add(pure, costPer > 0 ? costPer : depo.OrtalamaMaliyet);

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new { pureGoldGram = pure, targetAyar = target, depoGramAdded = pure });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---------- POST convert-to-product ----------
    public sealed record ScrapConvertReq(
        Guid BranchId,
        string FromKarat,
        decimal WeightGram,
        Guid ProductId,
        int ItemCount,
        decimal? CostPerGramOverride);

    [HttpPost("convert")]
    public async Task<IActionResult> ConvertToProduct([FromBody] ScrapConvertReq req, CancellationToken ct)
    {
        if (req.WeightGram <= 0 || req.ItemCount <= 0)
            return BadRequest(new { error = "Gram ve adet pozitif olmalıdır." });
        var fromK = NormalizeKarat(req.FromKarat);
        if (string.IsNullOrEmpty(fromK))
            return BadRequest(new { error = "Geçerli ayar girin." });

        var tid = TenantId;
        var product = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == req.ProductId && p.TenantId == tid && p.BranchId == req.BranchId, ct);
        if (product is null)
            return BadRequest(new { error = "Ürün bulunamadı." });

        var perItemW = Math.Round(req.WeightGram / req.ItemCount, 4);
        var costPerGram = req.CostPerGramOverride ?? 0m;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var row = await _db.ScrapStocks
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId && x.Karat == fromK && !x.IsDeleted, ct);
            if (row is null || row.WeightGram < req.WeightGram)
            {
                await tx.RollbackAsync(ct);
                return BadRequest(new { error = "Yetersiz hurda.", available = row?.WeightGram ?? 0 });
            }

            var pureOut = ScrapGoldMath.PureGoldGrams(req.WeightGram, fromK);
            row.WeightGram -= req.WeightGram;
            row.PureGoldGram = ScrapGoldMath.PureGoldGrams(row.WeightGram, fromK);
            row.UpdatedAt = DateTime.UtcNow;

            _db.ScrapLedgers.Add(new ScrapLedger
            {
                TenantId = tid,
                BranchId = req.BranchId,
                Kind = ScrapLedgerKind.ConvertOut,
                Karat = fromK,
                DeltaWeightGram = -req.WeightGram,
                DeltaPureGoldGram = -pureOut,
                ProductId = req.ProductId,
                Note = $"Ürüne çevirim {req.ItemCount} ad."
            });

            var itemIds = new List<Guid>();
            var lineCost = Math.Round(costPerGram * perItemW, 2);
            for (var i = 0; i < req.ItemCount; i++)
            {
                var pi = new ProductItem
                {
                    TenantId = tid,
                    ProductId = req.ProductId,
                    BranchId = req.BranchId,
                    Barcode = GenBarcode(),
                    Serial = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                    Karat = fromK,
                    Weight = perItemW,
                    Cost = lineCost,
                    UpdatedAt = DateTime.UtcNow,
                    IsInStock = true
                };
                _db.ProductItems.Add(pi);
                itemIds.Add(pi.Id);
            }

            await _db.SaveChangesAsync(ct);

            await _stock.AdjustAsync(
                req.BranchId,
                req.ProductId,
                null,
                +req.WeightGram,
                StockRefKind.ScrapManufacturing,
                Guid.NewGuid(),
                $"Hurda→ürün {product.ProductCode} {req.WeightGram:N3} gr",
                ct);

            await tx.CommitAsync(ct);
            return Ok(new { productItemIds = itemIds, pureGoldGram = pureOut });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public sealed class HurdaMetricsRequest
    {
        public Guid BranchId { get; set; }
        public List<Guid>? PurchaseItemIds { get; set; }
    }

    /// <summary>Hurda alış satırları: vitrinde stokta barkodlu gram (IsInStock) + henüz vitrine çıkmamış gram; OriginalGram=alış satırı referansı.</summary>
    [HttpGet("hurda-metrics")]
    public async Task<IActionResult> GetHurdaMetrics([FromQuery] Guid branchId, [FromQuery] string? purchaseItemIds, CancellationToken ct)
    {
        var tid = TenantId;
        if (branchId == Guid.Empty) return BadRequest(new { error = "branchId gerekli." });
        if (string.IsNullOrWhiteSpace(purchaseItemIds))
            return Ok(new List<HurdaPurchaseLineMetricDto>());

        var idList = new List<Guid>();
        foreach (var s in purchaseItemIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(s, out var g) && g != Guid.Empty) idList.Add(g);
        if (idList.Count == 0) return Ok(new List<HurdaPurchaseLineMetricDto>());

        var result = await HurdaPurchaseMetricsHelper.ComputeHurdaPurchaseLineMetricsAsync(_db, tid, branchId, idList, ct);
        return Ok(result);
    }

    /// <summary>Uzun ID listeleri ve sorgu dizesi sorunlarından kaçınmak için POST (masaüstü istemci bunu kullanır).</summary>
    [HttpPost("hurda-metrics")]
    public async Task<IActionResult> PostHurdaMetrics([FromBody] HurdaMetricsRequest? body, CancellationToken ct)
    {
        if (body is null || body.BranchId == Guid.Empty)
            return BadRequest(new { error = "branchId ve purchaseItemIds gerekli." });
        var tid = TenantId;
        var idList = (body.PurchaseItemIds ?? new List<Guid>())
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
        if (idList.Count == 0)
            return Ok(new List<HurdaPurchaseLineMetricDto>());

        var result = await HurdaPurchaseMetricsHelper.ComputeHurdaPurchaseLineMetricsAsync(_db, tid, body.BranchId, idList, ct);
        return Ok(result);
    }

    public sealed record ScrapWithdrawLineReq(
        Guid BranchId,
        Guid PurchaseItemId,
        decimal Gram,
        bool FromBarcoded,
        string? Note);

    /// <summary>Stok/Depo hurda sekmesinden satır bazlı manuel çıkış (barkodlu veya barkodsuz).</summary>
    [HttpPost("withdraw-purchase-line")]
    public async Task<IActionResult> WithdrawPurchaseLine([FromBody] ScrapWithdrawLineReq req, CancellationToken ct)
    {
        if (req.BranchId == Guid.Empty || req.PurchaseItemId == Guid.Empty)
            return BadRequest(new { error = "branchId ve purchaseItemId zorunludur." });
        if (req.Gram <= 0)
            return BadRequest(new { error = "Gram pozitif olmalıdır." });

        var tid = TenantId;
        var gram = Math.Round(req.Gram, 4, MidpointRounding.AwayFromZero);

        var pi = await _db.PurchaseItems
            .Include(x => x.Purchase)
            .FirstOrDefaultAsync(x => x.Id == req.PurchaseItemId && x.TenantId == tid && !x.IsDeleted, ct);
        if (pi?.Purchase is null)
            return BadRequest(new { error = "Alış satırı bulunamadı." });
        if (pi.Purchase.BranchId != req.BranchId)
            return BadRequest(new { error = "Satır bu şubeye ait değil." });
        if (pi.Kind != ItemKind.Scrap)
            return BadRequest(new { error = "Bu satır hurda tipinde değil." });

        var metrics = await HurdaPurchaseMetricsHelper.ComputeHurdaPurchaseLineMetricsAsync(
            _db, tid, req.BranchId, new List<Guid> { pi.Id }, ct);
        var metric = metrics.FirstOrDefault();
        if (metric is null)
            return BadRequest(new { error = "Hurda metrikleri hesaplanamadı." });

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            if (req.FromBarcoded)
            {
                if (metric.BarkodluGram < gram - 0.0001m)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = $"Yetersiz barkodlu hurda. İstenen: {gram:0.###}, Barkodlu: {metric.BarkodluGram:0.###} g" });
                }

                var items = await _db.ProductItems
                    .Where(x => x.SourcePurchaseItemId == pi.Id && x.TenantId == tid && !x.IsDeleted && x.IsInStock)
                    .OrderBy(x => x.CreatedAt)
                    .ToListAsync(ct);

                var remaining = gram;
                foreach (var item in items)
                {
                    if (remaining <= 0.0001m) break;
                    var w = Math.Round(item.Weight, 4, MidpointRounding.AwayFromZero);
                    if (w <= remaining + 0.0001m)
                    {
                        item.IsInStock = false;
                        item.UpdatedAt = DateTime.UtcNow;
                        remaining -= w;
                    }
                    else
                    {
                        item.Weight = Math.Round(w - remaining, 4, MidpointRounding.AwayFromZero);
                        item.UpdatedAt = DateTime.UtcNow;
                        remaining = 0;
                    }
                }

                if (remaining > 0.0001m)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = "Barkodlu hurda çıkışı tamamlanamadı (yetersiz stokta ürün)." });
                }
            }
            else
            {
                if (metric.BarkodsuzGram < gram - 0.0001m)
                {
                    await tx.RollbackAsync(ct);
                    return BadRequest(new { error = $"Yetersiz barkodsuz hurda. İstenen: {gram:0.###}, Barkodsuz: {metric.BarkodsuzGram:0.###} g" });
                }

                pi.Quantity = Math.Round(pi.Quantity - gram, 4, MidpointRounding.AwayFromZero);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var updated = await HurdaPurchaseMetricsHelper.ComputeHurdaPurchaseLineMetricsAsync(
                _db, tid, req.BranchId, new List<Guid> { pi.Id }, ct);

            return Ok(new
            {
                ok = true,
                metrics = updated.FirstOrDefault()
            });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public sealed record ScrapBarcodeFromPurchaseLineReq(
        Guid BranchId,
        Guid PurchaseItemId,
        decimal Gram,
        CreateProductDto Product);

    /// <summary>Müşteri hurda alış satırından vitrine barkodlama: ScrapStock düşer, Product + ProductItem (kaynak satır bağlı).</summary>
    [HttpPost("barcode-from-purchase-line")]
    [Authorize(Roles = "Owner,Admin")]
    public async Task<IActionResult> BarcodeFromPurchaseLine([FromBody] ScrapBarcodeFromPurchaseLineReq req, CancellationToken ct)
    {
        if (req.Gram <= 0) return BadRequest(new { error = "Gram pozitif olmalıdır." });
        if (req.Product is null || string.IsNullOrWhiteSpace(req.Product.ProductCode) || string.IsNullOrWhiteSpace(req.Product.Name))
            return BadRequest(new { error = "Ürün bilgisi (kod, ad) zorunludur." });
        if (!TryGetRequiredBranchId(out var jwtBranchId, out var branchErr)) return branchErr!;
        if (req.BranchId != jwtBranchId)
            return BadRequest(new { error = "İstek şubesi ile oturum şubesi eşleşmiyor." });

        var tid = TenantId;
        var pi = await _db.PurchaseItems
            .Include(x => x.Purchase)
            .FirstOrDefaultAsync(x => x.Id == req.PurchaseItemId && x.TenantId == tid, ct);
        if (pi?.Purchase is null) return BadRequest(new { error = "Alış satırı bulunamadı." });
        if (pi.Purchase.BranchId != req.BranchId)
            return BadRequest(new { error = "Satır bu şubeye ait değil." });
        if (pi.Kind != ItemKind.Scrap)
            return BadRequest(new { error = "Bu satır hurda (Scrap) tipinde değil; stok hareketi yapılamaz." });

        var barcodedTotal = await _db.ProductItems.IgnoreQueryFilters()
            .Where(x =>
                x.SourcePurchaseItemId == pi.Id
                && x.TenantId == tid
                && !x.IsDeleted)
            .SumAsync(x => x.Weight, ct);
        var remaining = Math.Round(pi.Quantity - barcodedTotal, 4, MidpointRounding.AwayFromZero);
        if (req.Gram > remaining + 0.0001m)
            return BadRequest(new { error = $"Yetersiz hurda satırı. Kalan: {remaining:N3} g, istenen: {req.Gram:N3} g." });

        var dto = req.Product;
        if (dto.DepoBranchId.HasValue && dto.DepoBranchId != Guid.Empty)
            return BadRequest(new { error = "Hurda barkodlamada DepoBranchId kullanılamaz." });

        var productCode = dto.ProductCode.Trim();
        var existsInBranch = await _db.Products.IgnoreQueryFilters()
            .AnyAsync(x => x.TenantId == tid && x.BranchId == jwtBranchId && x.ProductCode == productCode && !x.IsDeleted, ct);
        if (existsInBranch)
            return Conflict(new { error = "Bu şubede aynı ürün kodu zaten kayıtlı." });
        var existsInTenant = await _db.Products.IgnoreQueryFilters()
            .AnyAsync(x => x.TenantId == tid && x.ProductCode == productCode && !x.IsDeleted, ct);
        if (existsInTenant)
            return Conflict(new { error = "Bu ürün kodu başka şubede kullanılıyor." });

        var gram = Math.Round(req.Gram, 4, MidpointRounding.AwayFromZero);
        var karatForScrap = (pi.Karat ?? "").Trim();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            await _scrap.EnsureScrapStockFromPurchaseItemAsync(tid, req.BranchId, pi, ct);

            var (subOk, subErr) = await _scrap.TrySubtractScrapForPurchaseLineBarcodeAsync(
                tid, req.BranchId, karatForScrap, gram, pi.PurchaseId,
                $"Hurda satır vitrin L{pi.LineNo}", ct);
            if (!subOk)
            {
                await tx.RollbackAsync(ct);
                return BadRequest(new { error = subErr ?? "Hurda stoğu yetersiz." });
            }

            var entity = new Product
            {
                TenantId = tid,
                BranchId = jwtBranchId,
                ProductCode = productCode,
                Name = dto.Name.Trim(),
                Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category.Trim(),
                Karat = string.IsNullOrWhiteSpace(dto.Karat) ? null : dto.Karat.Trim(),
                WeightGr = gram,
                Cost = dto.Cost ?? 0m,
                Barcode = string.IsNullOrWhiteSpace(dto.Barcode) ? null : dto.Barcode.Trim(),
                Olcu = string.IsNullOrWhiteSpace(dto.Olcu) ? null : dto.Olcu.Trim(),
                InventoryType = (InventoryType)Math.Max(0, Math.Min(1, dto.InventoryType)),
                StokMiktari = Math.Max(0, dto.StokMiktari),
                ZiynetTipi = string.IsNullOrWhiteSpace(dto.ZiynetTipi) ? null : dto.ZiynetTipi.Trim(),
                IsSpecialProduct = dto.IsSpecialProduct,
                MalTanim = string.IsNullOrWhiteSpace(dto.MalTanim) ? null : dto.MalTanim.Trim(),
                DepoTedarikciFirma = string.IsNullOrWhiteSpace(dto.DepoTedarikciFirma) ? null : dto.DepoTedarikciFirma.Trim(),
                BelirlenenSatisFiyatiHas = dto.BelirlenenSatisFiyatiHas,
                BirimSatisIscilikHas = dto.BirimSatisIscilikHas,
                DepoBirimMaliyet = dto.DepoBirimMaliyet,
            };

            _db.Products.Add(entity);
            await _db.SaveChangesAsync(ct);

            var bar = string.IsNullOrWhiteSpace(dto.Barcode) ? GenBarcode() : dto.Barcode.Trim();
            var dupPi = await _db.ProductItems.IgnoreQueryFilters()
                .AnyAsync(x => x.TenantId == tid && x.BranchId == jwtBranchId && x.Barcode == bar, ct);
            if (dupPi)
            {
                await tx.RollbackAsync(ct);
                return Conflict(new { error = "Bu barkod bu şubede zaten kullanılıyor." });
            }

            var item = new ProductItem
            {
                TenantId = tid,
                ProductId = entity.Id,
                BranchId = jwtBranchId,
                Barcode = bar,
                Serial = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                Karat = (dto.Karat ?? pi.Karat ?? "").Trim(),
                Weight = gram,
                Cost = dto.Cost ?? 0m,
                IsInStock = true,
                SourcePurchaseItemId = pi.Id,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.ProductItems.Add(item);
            await _db.SaveChangesAsync(ct);

            await _stock.AdjustAsync(
                jwtBranchId,
                entity.Id,
                item.Id,
                +gram,
                StockRefKind.ScrapManufacturing,
                item.Id,
                $"Hurda→vitrin {productCode} {gram:N3} g",
                ct);

            await tx.CommitAsync(ct);
            return Ok(new { productId = entity.Id, productItemId = item.Id, barcode = bar });
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync(ct);
            var msg = ex.InnerException?.Message ?? ex.Message;
            return BadRequest(new { error = "Kayıt hatası: " + msg });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
