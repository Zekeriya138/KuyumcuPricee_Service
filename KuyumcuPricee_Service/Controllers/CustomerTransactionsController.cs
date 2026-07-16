using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class CustomerTransactionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ExchangeRateService _rates;
    private readonly TransactionReversalService _reversal;

    public CustomerTransactionsController(AppDbContext db, ExchangeRateService rates, TransactionReversalService reversal)
    {
        _db = db;
        _rates = rates;
        _reversal = reversal;
    }

    public sealed record ZiynetSettleReq(string Ad, string? Tip, decimal Adet, decimal? BirimFiyatTl, decimal? BirimAlisTl, string? CariDurum, string? LedgerSide = null);
    public sealed record IscilikliSettleReq(Guid? TransactionId, string UrunAdi, string Ayar, decimal? Gram, decimal? HasEquivalent, decimal? SatisFiyatiTl, string? CariDurum);
    public sealed record OpeningBalanceReq(string Unit, decimal Amount);
    public sealed record ProcessReq(
        Guid CustomerId,
        Guid? BranchId,
        string TxType,
        string SourceUnit,
        decimal SourceAmount,
        string TargetUnit,
        bool IsConvertEnabled,
        List<ZiynetSettleReq>? ZiynetItems,
        List<IscilikliSettleReq>? IscilikliItems,
        List<OpeningBalanceReq>? OpeningBalances,
        decimal? NakitAmount,
        decimal? HavaleAmount,
        string? Description,
        DateTime? TxDate,
        decimal? SourceUnitTlRate = null,
        decimal? TargetUnitTlRate = null,
        decimal? TargetAmount = null,
        List<ZiynetUrunStokReq>? ZiynetUrunStokItems = null,
        string? SourceLedgerSide = null);

    [HttpPost("process")]
    public async Task<IActionResult> Process([FromBody] ProcessReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        if (req.BranchId.HasValue && req.BranchId.Value != Guid.Empty && req.BranchId.Value != branchId)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });
        var customer = await _db.Customers.FirstOrDefaultAsync(x => x.Id == req.CustomerId && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (customer is null) return BadRequest(new { error = "Geçersiz müşteri." });

        var txType = NormalizeTxType(req.TxType);
        if (txType is null) return BadRequest(new { error = "TxType PAYMENT/COLLECTION/OPENING_BALANCE/BALANCE_CONVERSION olmalıdır." });
        var txDate = req.TxDate?.ToUniversalTime() ?? DateTime.UtcNow;
        var batchId = Guid.NewGuid();
        var effectiveDescription = ZiynetUrunStokMarker.AppendDescription(
            req.Description,
            ZiynetUrunStokMarker.FromReqItems(req.ZiynetUrunStokItems));
        req = req with { Description = effectiveDescription };

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            if (txType == "OPENING_BALANCE")
            {
                await ApplyOpeningBalanceAsync(req, tenantId, branchId, txDate, batchId, ct);
            }
            else if (txType == "BALANCE_CONVERSION")
            {
                await ApplyBalanceConversionAsync(req, tenantId, branchId, txDate, batchId, ct);
            }
            else
            {
                await ApplyCurrencyAsync(req, tenantId, branchId, txType, txDate, batchId, ct);
                await ApplyZiynetAsync(req, tenantId, branchId, txType, txDate, batchId, ct);
                await ApplyIscilikliAsync(req, tenantId, branchId, txType, txDate, batchId, ct);
                await ApplyCashMovementAsync(req, tenantId, branchId, txType, txDate, batchId, ct);
                ApplyZiynetUrunStokAudit(req, tenantId, branchId, txType, txDate, batchId);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new { ok = true, batchId });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ApplyCurrencyAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, Guid batchId, CancellationToken ct)
    {
        if (req.SourceAmount <= 0) return;

        var src = NormalizeUnit(req.SourceUnit);
        var tgt = req.IsConvertEnabled ? NormalizeUnit(req.TargetUnit) : src;

        decimal srcRate;
        decimal tgtRate;
        if (req.SourceUnitTlRate is > 0m && req.TargetUnitTlRate is > 0m)
        {
            srcRate = req.SourceUnitTlRate.Value;
            tgtRate = req.TargetUnitTlRate.Value;
        }
        else
        {
            var buyRates = _rates.GetUnitToTlBuyRates();
            var sellRates = _rates.GetUnitToTlSellRates();
            var srcMap = txType == "COLLECTION" ? buyRates : sellRates;
            var tgtMap = txType == "COLLECTION" ? sellRates : buyRates;
            srcRate = srcMap.TryGetValue(src, out var sr) ? sr : 0m;
            tgtRate = tgtMap.TryGetValue(tgt, out var tr) ? tr : 0m;
        }
        if (srcRate <= 0 || tgtRate <= 0)
            throw new InvalidOperationException("Kur bilgisi alınamadı.");

        var srcAmt = decimal.Round(req.SourceAmount, 6);
        var tgtAmt = req.TargetAmount is > 0m
            ? decimal.Round(req.TargetAmount.Value, 6, MidpointRounding.AwayFromZero)
            : req.IsConvertEnabled
                ? decimal.Round((srcAmt * srcRate) / tgtRate, 6, MidpointRounding.AwayFromZero)
                : srcAmt;
        if (req.TargetAmount is null or <= 0m && req.IsConvertEnabled && TryConvertHasByKgRate(src, tgt, txType, srcAmt, out var kgConverted))
            tgtAmt = kgConverted;

        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, req.CustomerId, ct);
        var existingRows = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CustomerId == req.CustomerId && x.BranchId == branchId
                        && !x.IsDeleted && !x.IsReversed
                        && x.GroupCode == "DOVIZ"
                        && (x.ItemName ?? "").ToUpper() == tgt)
            .ToListAsync(ct);
        var (grossBorc, grossAlacak) = CustomerFinanceHelper.ComputeGrossColumns(existingRows);

        var remaining = tgtAmt;
        if (txType == "PAYMENT")
        {
            // Ödeme: önce alacak sütunundan düş, kalan borca yaz.
            var offsetAlacak = Math.Min(grossAlacak, remaining);
            if (offsetAlacak > 0m)
            {
                AddDovizSettlementTransaction(tenantId, req.CustomerId, branchId, tgt, offsetAlacak, tgtRate,
                    CustomerFinanceHelper.RefSettleAlacak, -1, "Odeme", req.Description, txDate, batchId);
                remaining -= offsetAlacak;
            }
            if (remaining > 0m)
            {
                AddDovizSettlementTransaction(tenantId, req.CustomerId, branchId, tgt, remaining, tgtRate,
                    "MANUAL", -1, "Borclu", req.Description, txDate, batchId);
            }
            ApplyCustomerBalanceDelta(bal, tgt, -tgtAmt);
        }
        else
        {
            // Tahsilat: önce borç sütunundan düş, kalan alacağa yaz.
            var offsetBorc = Math.Min(grossBorc, remaining);
            if (offsetBorc > 0m)
            {
                AddDovizSettlementTransaction(tenantId, req.CustomerId, branchId, tgt, offsetBorc, tgtRate,
                    CustomerFinanceHelper.RefSettleBorc, 1, "Tahsilat", req.Description, txDate, batchId);
                remaining -= offsetBorc;
            }
            if (remaining > 0m)
            {
                AddDovizSettlementTransaction(tenantId, req.CustomerId, branchId, tgt, remaining, tgtRate,
                    "MANUAL", 1, "Alacakli", req.Description, txDate, batchId);
            }
            ApplyCustomerBalanceDelta(bal, tgt, +tgtAmt);
        }

        bal.UpdatedAt = DateTime.UtcNow;
    }

    private void AddDovizSettlementTransaction(
        Guid tenantId, Guid customerId, Guid branchId, string unit, decimal quantity, decimal unitPriceTl,
        string refType, int direction, string cariDurum, string? note, DateTime txDate, Guid batchId)
    {
        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = unit,
            ItemType = null,
            Quantity = decimal.Round(quantity, 6, MidpointRounding.AwayFromZero),
            Direction = direction,
            UnitPriceTl = unitPriceTl,
            TotalPriceTl = decimal.Round(quantity * unitPriceTl, 4, MidpointRounding.AwayFromZero),
            TxDate = txDate,
            CariDurum = cariDurum,
            RefType = refType,
            Note = note,
            BatchId = batchId
        });
    }

    private bool TryConvertHasByKgRate(string src, string tgt, string txType, decimal srcAmount, out decimal converted)
    {
        converted = 0m;
        if (srcAmount <= 0m) return false;
        if (src != "HAS") return false;
        if (tgt is not ("USD" or "EUR")) return false;
        if (txType is not ("COLLECTION" or "PAYMENT")) return false;

        var code = tgt == "USD" ? "XAU_KG_USD" : "XAU_KG_EUR";
        var kgRate = txType == "COLLECTION"
            ? _rates.GetQuoteBidByCode(code)   // Tahsilat: alış.
            : _rates.GetQuoteAskByCode(code);  // Ödeme: satış.
        if (kgRate <= 0m) return false;

        converted = decimal.Round(srcAmount * (kgRate / 1000m), 6, MidpointRounding.AwayFromZero);
        return converted > 0m;
    }

    private async Task ApplyOpeningBalanceAsync(ProcessReq req, Guid tenantId, Guid branchId, DateTime txDate, Guid batchId, CancellationToken ct)
    {
        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, req.CustomerId, ct);
        var opening = req.OpeningBalances ?? new List<OpeningBalanceReq>();
        foreach (var row in opening.Where(x => x.Amount != 0m))
        {
            var unit = NormalizeUnit(row.Unit);
            var amount = decimal.Round(row.Amount, 6, MidpointRounding.AwayFromZero);
            var delta = amount;
            ApplyCustomerBalanceDelta(bal, unit, delta);
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = req.CustomerId,
                BranchId = branchId,
                GroupCode = "DOVIZ",
                ItemName = unit,
                ItemType = null,
                Quantity = Math.Abs(amount),
                Direction = amount >= 0m ? 1 : -1,
                UnitPriceTl = unit == "TL" ? 1m : null,
                TotalPriceTl = null,
                TxDate = txDate,
                CariDurum = amount >= 0m ? "Alacakli" : "Borclu",
                RefType = "OPENING_BALANCE",
                Note = req.Description,
                BatchId = batchId
            });
        }

        foreach (var z in (req.ZiynetItems ?? new List<ZiynetSettleReq>()).Where(x => x.Adet > 0m && !string.IsNullOrWhiteSpace(x.Ad)))
        {
            var adNorm = (z.Ad ?? string.Empty).Trim().ToUpperInvariant();
            var durum = NormalizeCariDurum(z.CariDurum);
            var dir = durum == "BORCLU" ? -1 : 1;

            // Has altın açılış bakiyesi ziynet adet defterine değil DOVIZ/HAS'a yazılır.
            if (IsHasAltinZiynetAd(adNorm))
            {
                var hasQty = decimal.Round(Math.Abs(z.Adet), 6, MidpointRounding.AwayFromZero);
                ApplyCustomerBalanceDelta(bal, "HAS", dir * hasQty);
                _db.CustomerTransactions.Add(new CustomerTransaction
                {
                    TenantId = tenantId,
                    CustomerId = req.CustomerId,
                    BranchId = branchId,
                    GroupCode = "DOVIZ",
                    ItemName = "HAS",
                    ItemType = null,
                    Quantity = hasQty,
                    Direction = dir,
                    Gram = hasQty,
                    Ayar = "HAS",
                    HasEquivalent = hasQty,
                    TxDate = txDate,
                    CariDurum = dir >= 0 ? "Alacakli" : "Borclu",
                    RefType = "OPENING_BALANCE",
                    Note = req.Description,
                    BatchId = batchId
                });
                continue;
            }

            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = req.CustomerId,
                BranchId = branchId,
                GroupCode = "ZIYNET",
                ItemName = (z.Ad ?? string.Empty).Trim().ToUpperInvariant(),
                ItemType = string.IsNullOrWhiteSpace(z.Tip) ? null : z.Tip!.Trim(),
                Quantity = decimal.Round(Math.Abs(z.Adet), 3, MidpointRounding.AwayFromZero),
                Direction = dir,
                TxDate = txDate,
                CariDurum = durum == "EMANET" ? "Emanet" : (dir >= 0 ? "Alacakli" : "Borclu"),
                RefType = "OPENING_BALANCE",
                Note = req.Description,
                BatchId = batchId
            });
        }

        foreach (var i in (req.IscilikliItems ?? new List<IscilikliSettleReq>()).Where(x => !string.IsNullOrWhiteSpace(x.UrunAdi)))
        {
            var durum = NormalizeCariDurum(i.CariDurum);
            var dir = durum == "BORCLU" ? -1 : 1;
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = req.CustomerId,
                BranchId = branchId,
                GroupCode = "ISCILIKLI",
                ItemName = (i.UrunAdi ?? string.Empty).Trim().ToUpperInvariant(),
                ItemType = null,
                Quantity = 1m,
                Direction = dir,
                Gram = i.Gram.HasValue ? Math.Abs(i.Gram.Value) : null,
                Ayar = string.IsNullOrWhiteSpace(i.Ayar) ? null : i.Ayar.Trim(),
                HasEquivalent = i.HasEquivalent.HasValue ? Math.Abs(i.HasEquivalent.Value) : null,
                TotalPriceTl = i.SatisFiyatiTl.HasValue ? Math.Abs(i.SatisFiyatiTl.Value) : null,
                TxDate = txDate,
                CariDurum = durum == "EMANET" ? "Emanet" : (dir >= 0 ? "Alacakli" : "Borclu"),
                RefType = "OPENING_BALANCE",
                Note = req.Description,
                BatchId = batchId
            });
        }

        bal.UpdatedAt = DateTime.UtcNow;
    }

    private async Task ApplyBalanceConversionAsync(ProcessReq req, Guid tenantId, Guid branchId, DateTime txDate, Guid batchId, CancellationToken ct)
    {
        if (req.SourceAmount <= 0m)
            throw new InvalidOperationException("Dönüştürülecek kaynak miktar 0'dan büyük olmalıdır.");

        if (!BalanceConversionZiynetHelper.TryParseUnit(req.SourceUnit, out var srcU))
            throw new InvalidOperationException("Kaynak birim geçersiz.");
        if (!BalanceConversionZiynetHelper.TryParseUnit(req.TargetUnit, out var tgtU))
            throw new InvalidOperationException("Hedef birim geçersiz.");
        if (BalanceConversionZiynetHelper.UnitsEqual(srcU, tgtU))
            throw new InvalidOperationException("Kaynak ve hedef birim aynı olamaz.");

        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, req.CustomerId, ct);
        var (grossBorc, grossAlacak) = await GetCustomerConversionGrossAsync(tenantId, req.CustomerId, branchId, srcU, ct);
        var ledgerSide = CustomerFinanceHelper.NormalizeLedgerSide(req.SourceLedgerSide);
        if (string.IsNullOrEmpty(ledgerSide))
            ledgerSide = ResolveAutoLedgerSide(grossBorc, grossAlacak, req.SourceAmount);

        if (CustomerFinanceHelper.IsLedgerAlacak(ledgerSide))
        {
            if (grossAlacak + 0.0005m < req.SourceAmount)
                throw new InvalidOperationException("Kaynak alacak miktarı yetersiz.");
        }
        else if (grossBorc + 0.0005m < req.SourceAmount)
        {
            throw new InvalidOperationException("Kaynak borç miktarı yetersiz.");
        }

        var sourceBalance = grossAlacak - grossBorc;
        var (useBuySrc, useBuyTgt) = CustomerFinanceHelper.IsLedgerAlacak(ledgerSide)
            ? (true, false)
            : (false, true);

        var srcRate = req.SourceUnitTlRate is > 0m
            ? req.SourceUnitTlRate.Value
            : BalanceConversionZiynetHelper.ResolveUnitTlRate(_rates, srcU, useBuySrc);
        var tgtRate = req.TargetUnitTlRate is > 0m
            ? req.TargetUnitTlRate.Value
            : BalanceConversionZiynetHelper.ResolveUnitTlRate(_rates, tgtU, useBuyTgt);
        if (srcRate <= 0m || tgtRate <= 0m)
            throw new InvalidOperationException("Kur bilgisi alınamadı.");

        var srcAmt = decimal.Round(req.SourceAmount, 6, MidpointRounding.AwayFromZero);
        var tgtAmt = req.TargetAmount is > 0m
            ? decimal.Round(req.TargetAmount.Value, 6, MidpointRounding.AwayFromZero)
            : decimal.Round((srcAmt * srcRate) / tgtRate, 6, MidpointRounding.AwayFromZero);

        var note = BalanceConversionZiynetHelper.BuildConversionNote(req.Description, srcAmt, srcU, tgtAmt, tgtU, useBuySrc, useBuyTgt);
        ApplyCustomerConversionReduction(bal, tenantId, req.CustomerId, branchId, srcU, srcAmt, ledgerSide, srcRate, note, txDate, batchId);
        ApplyCustomerConversionAddition(bal, tenantId, req.CustomerId, branchId, tgtU, tgtAmt, ledgerSide, tgtRate, note, txDate, batchId);
        bal.UpdatedAt = DateTime.UtcNow;
        _ = sourceBalance;
    }

    private static string ResolveAutoLedgerSide(decimal grossBorc, decimal grossAlacak, decimal amount)
    {
        var canAlacak = grossAlacak >= amount - 0.0005m && grossAlacak > 0m;
        var canBorc = grossBorc >= amount - 0.0005m && grossBorc > 0m;
        if (canAlacak && !canBorc) return CustomerFinanceHelper.LedgerAlacak;
        if (canBorc && !canAlacak) return CustomerFinanceHelper.LedgerBorc;
        if (grossAlacak > grossBorc) return CustomerFinanceHelper.LedgerAlacak;
        if (grossBorc > grossAlacak) return CustomerFinanceHelper.LedgerBorc;
        return CustomerFinanceHelper.LedgerAlacak;
    }

    private async Task<(decimal Borc, decimal Alacak)> GetCustomerConversionGrossAsync(
        Guid tenantId, Guid customerId, Guid branchId,
        BalanceConversionZiynetHelper.ConversionUnit unit, CancellationToken ct)
    {
        if (unit.IsZiynet)
        {
            var rows = await _db.CustomerTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                            && !x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET")
                .ToListAsync(ct);
            var matched = rows.Where(x => CustomerFinanceHelper.ZiynetRowMatches(
                x.ItemName, x.ItemType, unit.ZiynetAd, unit.ZiynetTip)).ToList();
            return CustomerFinanceHelper.ComputeGrossColumns(matched);
        }

        var dovizRows = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                        && !x.IsDeleted && !x.IsReversed && x.GroupCode == "DOVIZ"
                        && x.ItemName == unit.CurrencyUnit)
            .ToListAsync(ct);
        if (unit.CurrencyUnit == "HAS")
        {
            var hasZiynetRows = await _db.CustomerTransactions.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                            && !x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET")
                .ToListAsync(ct);
            var misclassified = hasZiynetRows
                .Where(x => CustomerFinanceHelper.IsHasAltinZiynetAd(CustomerFinanceHelper.NormalizeZiynetItemName(x.ItemName)))
                .ToList();
            dovizRows = dovizRows.Concat(misclassified).ToList();
        }
        return CustomerFinanceHelper.ComputeGrossColumns(dovizRows);
    }

    private void ApplyCustomerConversionReduction(
        CustomerBalance bal, Guid tenantId, Guid customerId, Guid branchId,
        BalanceConversionZiynetHelper.ConversionUnit unit,
        decimal amount, string ledgerSide, decimal rate, string note, DateTime txDate, Guid batchId)
    {
        var (direction, refType, balanceDelta) = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, amount);
        var qty = decimal.Round(Math.Abs(amount), unit.IsZiynet ? 3 : 6, MidpointRounding.AwayFromZero);
        var totalTl = decimal.Round(qty * rate, 4, MidpointRounding.AwayFromZero);

        if (unit.IsZiynet && IsHasAltinZiynetAd(unit.ZiynetAd.Trim().ToUpperInvariant()))
        {
            ApplyCustomerBalanceDelta(bal, "HAS", balanceDelta);
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = customerId,
                BranchId = branchId,
                GroupCode = "DOVIZ",
                ItemName = "HAS",
                Quantity = qty,
                Direction = direction,
                UnitPriceTl = rate,
                TotalPriceTl = totalTl,
                HasEquivalent = qty,
                TxDate = txDate,
                CariDurum = "Dönüşüm",
                RefType = refType,
                Note = note,
                BatchId = batchId
            });
            return;
        }

        if (unit.IsZiynet)
        {
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = customerId,
                BranchId = branchId,
                GroupCode = "ZIYNET",
                ItemName = unit.ZiynetAd.Trim().ToUpperInvariant(),
                ItemType = string.IsNullOrWhiteSpace(unit.ZiynetTip) ? null : unit.ZiynetTip.Trim(),
                Quantity = qty,
                Direction = direction,
                UnitPriceTl = rate,
                TotalPriceTl = totalTl,
                TxDate = txDate,
                CariDurum = CustomerFinanceHelper.IsLedgerAlacak(ledgerSide) ? "Alacakli" : "Borclu",
                RefType = refType,
                Note = note,
                BatchId = batchId
            });
            return;
        }

        ApplyCustomerBalanceDelta(bal, unit.CurrencyUnit, balanceDelta);
        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = unit.CurrencyUnit,
            Quantity = qty,
            Direction = direction,
            UnitPriceTl = rate,
            TotalPriceTl = totalTl,
            TxDate = txDate,
            CariDurum = "Dönüşüm",
            RefType = refType,
            Note = note,
            BatchId = batchId
        });
    }

    private void ApplyCustomerConversionAddition(
        CustomerBalance bal, Guid tenantId, Guid customerId, Guid branchId,
        BalanceConversionZiynetHelper.ConversionUnit unit,
        decimal amount, string ledgerSide, decimal rate, string note, DateTime txDate, Guid batchId)
    {
        var (direction, cariDurum, balanceDelta) = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, amount);
        var qty = decimal.Round(Math.Abs(amount), unit.IsZiynet ? 3 : 6, MidpointRounding.AwayFromZero);
        var totalTl = decimal.Round(qty * rate, 4, MidpointRounding.AwayFromZero);

        if (unit.IsZiynet && IsHasAltinZiynetAd(unit.ZiynetAd.Trim().ToUpperInvariant()))
        {
            ApplyCustomerBalanceDelta(bal, "HAS", balanceDelta);
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = customerId,
                BranchId = branchId,
                GroupCode = "DOVIZ",
                ItemName = "HAS",
                Quantity = qty,
                Direction = direction,
                UnitPriceTl = rate,
                TotalPriceTl = totalTl,
                HasEquivalent = qty,
                TxDate = txDate,
                CariDurum = cariDurum,
                RefType = "BALANCE_CONVERSION",
                Note = note,
                BatchId = batchId
            });
            return;
        }

        if (unit.IsZiynet)
        {
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = customerId,
                BranchId = branchId,
                GroupCode = "ZIYNET",
                ItemName = unit.ZiynetAd.Trim().ToUpperInvariant(),
                ItemType = string.IsNullOrWhiteSpace(unit.ZiynetTip) ? null : unit.ZiynetTip.Trim(),
                Quantity = qty,
                Direction = direction,
                UnitPriceTl = rate,
                TotalPriceTl = totalTl,
                TxDate = txDate,
                CariDurum = cariDurum,
                RefType = "BALANCE_CONVERSION",
                Note = note,
                BatchId = batchId
            });
            return;
        }

        ApplyCustomerBalanceDelta(bal, unit.CurrencyUnit, balanceDelta);
        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = unit.CurrencyUnit,
            Quantity = qty,
            Direction = direction,
            UnitPriceTl = rate,
            TotalPriceTl = totalTl,
            TxDate = txDate,
            CariDurum = "Dönüşüm",
            RefType = "BALANCE_CONVERSION",
            Note = note,
            BatchId = batchId
        });
    }

    private async Task<decimal> GetCustomerZiynetNetAsync(
        Guid tenantId, Guid customerId, Guid branchId, string ad, string? tip, CancellationToken ct)
    {
        var targetCode = BalanceConversionZiynetHelper.ZiynetRateCode(ad, tip);
        if (string.IsNullOrEmpty(targetCode)) return 0m;

        var rows = await _db.CustomerTransactions
            .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BranchId == branchId
                        && !x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET")
            .Select(x => new { x.ItemName, x.ItemType, x.Quantity, x.Direction })
            .ToListAsync(ct);

        decimal net = 0m;
        foreach (var r in rows)
        {
            var code = BalanceConversionZiynetHelper.ZiynetRateCode(r.ItemName, r.ItemType);
            if (!string.Equals(code, targetCode, StringComparison.OrdinalIgnoreCase)) continue;
            net += r.Direction >= 0 ? r.Quantity : -r.Quantity;
        }
        return net;
    }

    private bool TryConvertHasByKgRateForBalanceConversion(string src, string tgt, decimal sourceBalance, decimal srcAmount, out decimal converted)
    {
        converted = 0m;
        if (srcAmount <= 0m) return false;
        if (src != "HAS") return false;
        if (tgt is not ("USD" or "EUR")) return false;

        var code = tgt == "USD" ? "XAU_KG_USD" : "XAU_KG_EUR";
        // Özel kural: kaynak bakiye eksi ise satış, artı/0 ise alış.
        var useSell = sourceBalance < 0m;
        var kgRate = useSell
            ? _rates.GetQuoteAskByCode(code)
            : _rates.GetQuoteBidByCode(code);
        if (kgRate <= 0m) return false;

        converted = decimal.Round(srcAmount * (kgRate / 1000m), 6, MidpointRounding.AwayFromZero);
        return converted > 0m;
    }

    private async Task ApplyCashMovementAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, Guid batchId, CancellationToken ct)
    {
        var unit = NormalizeUnit(req.SourceUnit);
        var nakit = decimal.Round(Math.Max(0m, req.NakitAmount ?? 0m), 6, MidpointRounding.AwayFromZero);
        var havale = decimal.Round(Math.Max(0m, req.HavaleAmount ?? 0m), 6, MidpointRounding.AwayFromZero);
        if (nakit <= 0m && havale <= 0m) return;

        if (nakit > 0m)
        {
            await AddCashMovementAsync(
                tenantId,
                branchId,
                unit,
                nakit,
                txType,
                "Nakit",
                "MusteriDovizIslem",
                "CUSTOMER_SETTLEMENT",
                req.CustomerId,
                txDate,
                batchId,
                ct);
        }
        if (havale > 0m)
        {
            await AddCashMovementAsync(
                tenantId,
                branchId,
                unit,
                havale,
                txType,
                "Havale",
                "MusteriDovizIslem",
                "CUSTOMER_SETTLEMENT",
                req.CustomerId,
                txDate,
                batchId,
                ct);
        }
    }

    private async Task AddCashMovementAsync(
        Guid tenantId,
        Guid branchId,
        string unit,
        decimal amount,
        string txType,
        string paymentMethod,
        string sourceModule,
        string refType,
        Guid refId,
        DateTime txDate,
        Guid batchId,
        CancellationToken ct)
    {
        var currency = unit is "USD" or "EUR" or "GBP" or "HAS" or "GUMUS" ? unit : "TL";
        var isHavale = paymentMethod.Equals("Havale", StringComparison.OrdinalIgnoreCase);
        var accountType = isHavale ? "PosBanka" : (currency == "TL" ? "Kasa" : "Vault");
        var accountName = isHavale
            ? (currency == "TL" ? "Banka" : $"Banka {currency}")
            : (currency == "TL" ? "Kasa TL" : $"Vault {currency}");

        var account = await _db.CashAccounts
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.AccountType == accountType &&
                x.Currency == currency &&
                x.Name == accountName &&
                !x.IsDeleted, ct);
        if (account is null)
        {
            account = new CashAccount
            {
                TenantId = tenantId,
                BranchId = branchId,
                AccountType = accountType,
                Currency = currency,
                Name = accountName,
                CurrentBalance = 0m
            };
            _db.CashAccounts.Add(account);
        }

        var isIncome = txType == "COLLECTION";
        account.CurrentBalance += isIncome ? amount : -amount;
        _db.CashTransactions.Add(new CashTransaction
        {
            TenantId = tenantId,
            BranchId = branchId,
            CashAccountId = account.Id,
            TxType = isIncome ? "Income" : "Expense",
            SourceModule = sourceModule,
            Currency = currency,
            Amount = amount,
            TxDate = txDate,
            RefType = refType,
            RefId = refId,
            Description = isIncome
                ? $"Müşteri tahsilatı ({paymentMethod.ToLowerInvariant()})"
                : $"Müşteriye ödeme ({paymentMethod.ToLowerInvariant()})",
            BatchId = batchId
        });
    }

    /// <summary>Has altın ziynet kalemi mi? (Külçe ve 22 ayar gram ziynet hariç) → DOVIZ/HAS bakiyesine yazılır.</summary>
    internal static bool IsHasAltinZiynetAd(string? ad)
        => CustomerFinanceHelper.IsHasAltinZiynetAd(ad);

    private async Task ApplyZiynetAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, Guid batchId, CancellationToken ct)
    {
        var items = req.ZiynetItems ?? new();
        if (items.Count == 0) return;
        // Yeni alacak/borç ziynet satırı oluşturma akışı (teslim edilmeyen kalemler).
        // Geriye dönük uyumluluk için "emanet" anahtarı da tetikleyici kabul edilir.
        var desc = req.Description ?? "";
        var emanetFlow = desc.Contains("ZIYNET_ALACAK", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("emanet", StringComparison.OrdinalIgnoreCase);

        var rows = await _db.CustomerTransactions
            .Where(x => x.TenantId == tenantId && x.CustomerId == req.CustomerId && x.BranchId == branchId && !x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET")
            .ToListAsync(ct);

        var open = rows
            .GroupBy(x => (
                Ad: CustomerFinanceHelper.NormalizeZiynetItemName(x.ItemName),
                Tip: CustomerFinanceHelper.NormalizeZiynetTipGroupingKey(
                    CustomerFinanceHelper.NormalizeZiynetItemName(x.ItemName), x.ItemType)))
            .Select(g => new
            {
                g.Key.Ad,
                g.Key.Tip,
                Qty = g.Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Ad))
            .ToDictionary(
                x => (x.Ad, x.Tip),
                x => x.Qty);

        CustomerBalance? balForHas = null;
        foreach (var it in items.Where(x => x.Adet > 0))
        {
            var normAd = CustomerFinanceHelper.NormalizeZiynetItemName(it.Ad);
            var normTip = CustomerFinanceHelper.NormalizeZiynetTipGroupingKey(normAd, it.Tip);
            var key = (normAd, normTip);
            if (emanetFlow)
            {
                var ledgerSide = CustomerFinanceHelper.NormalizeLedgerSide(it.LedgerSide);
                var birimFiyatTl = it.BirimFiyatTl.HasValue && it.BirimFiyatTl.Value > 0m
                    ? decimal.Round(it.BirimFiyatTl.Value, 6, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var birimAlisTl = it.BirimAlisTl.HasValue && it.BirimAlisTl.Value > 0m
                    ? decimal.Round(it.BirimAlisTl.Value, 6, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var toplamTutarTl = birimFiyatTl.HasValue
                    ? decimal.Round(it.Adet * birimFiyatTl.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var qty = decimal.Round(it.Adet, 6, MidpointRounding.AwayFromZero);
                if (qty <= 0m) continue;

                // Has altın teslim edilmeyen kalem: adet defterine değil, DOVIZ/HAS bakiyesine yaz.
                if (IsHasAltinZiynetAd(normAd)
                    || CustomerFinanceHelper.ShouldRouteHasAltinToDovizBalance(it.Ad, null, null))
                {
                    balForHas ??= await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, req.CustomerId, ct);
                    await CustomerFinanceHelper.ApplyEmanetDovizLegAsync(
                        _db, balForHas, tenantId, req.CustomerId, branchId,
                        unit: "HAS", amount: qty, ledgerSideOverride: ledgerSide,
                        unitPriceTl: birimFiyatTl, totalPriceTl: toplamTutarTl,
                        gram: qty, ayar: "HAS", hasEq: qty,
                        refType: "SALE", refId: null,
                        note: "Has altin teslim edilmeyen (emanet) - alacak kaydi",
                        txDate: txDate, batchId: batchId, ct: ct,
                        applyBalanceDelta: ApplyCustomerBalanceDelta);
                    continue;
                }

                if (string.IsNullOrEmpty(ledgerSide))
                {
                    var emItemRows = rows.Where(x => CustomerFinanceHelper.ZiynetRowMatches(x.ItemName, x.ItemType, it.Ad, it.Tip)).ToList();
                    var (emGrossBorc, emGrossAlacak) = CustomerFinanceHelper.ComputeGrossColumns(emItemRows);
                    ledgerSide = CustomerFinanceHelper.ResolveEmanetLedgerSideAuto(emGrossBorc, emGrossAlacak);
                }

                if (CustomerFinanceHelper.IsLedgerAlacak(ledgerSide))
                {
                    var (direction, cariDurum, _) = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, qty);
                    _db.CustomerTransactions.Add(new CustomerTransaction
                    {
                        TenantId = tenantId,
                        CustomerId = req.CustomerId,
                        BranchId = branchId,
                        GroupCode = "ZIYNET",
                        ItemName = normAd,
                        ItemType = normTip,
                        Quantity = qty,
                        Direction = direction,
                        UnitPriceTl = birimFiyatTl,
                        TotalPriceTl = toplamTutarTl,
                        HasEquivalent = birimAlisTl,
                        TxDate = txDate,
                        CariDurum = cariDurum,
                        RefType = "SALE",
                        Note = req.Description,
                        BatchId = batchId
                    });
                }
                else
                {
                    var (direction, refType, _) = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, qty);
                    _db.CustomerTransactions.Add(new CustomerTransaction
                    {
                        TenantId = tenantId,
                        CustomerId = req.CustomerId,
                        BranchId = branchId,
                        GroupCode = "ZIYNET",
                        ItemName = normAd,
                        ItemType = normTip,
                        Quantity = qty,
                        Direction = direction,
                        UnitPriceTl = birimFiyatTl,
                        TotalPriceTl = toplamTutarTl,
                        HasEquivalent = birimAlisTl,
                        TxDate = txDate,
                        CariDurum = "Borclu",
                        RefType = refType,
                        Note = req.Description,
                        BatchId = batchId
                    });
                }

                continue;
            }

            if (!open.TryGetValue(key, out var openQty))
                openQty = 0m;

            var itemRows = rows.Where(x => CustomerFinanceHelper.ZiynetRowMatches(x.ItemName, x.ItemType, it.Ad, it.Tip)).ToList();
            var (grossBorc, grossAlacak) = CustomerFinanceHelper.ComputeGrossColumns(itemRows);

            var settleRemaining = decimal.Round(it.Adet, 3, MidpointRounding.AwayFromZero);
            if (settleRemaining <= 0) continue;
            var tipDisplay = normTip;

            if (txType == "PAYMENT")
            {
                // Ödeme: önce alacak sütunundan düş, kalan borca yaz.
                var offsetAlacak = Math.Min(grossAlacak, settleRemaining);
                if (offsetAlacak > 0m)
                {
                    AddZiynetSettlementTransaction(tenantId, req.CustomerId, branchId, normAd, tipDisplay,
                        offsetAlacak, CustomerFinanceHelper.RefSettleAlacak, -1, "Odeme", req, txDate, batchId, txType);
                    settleRemaining -= offsetAlacak;
                }
                if (settleRemaining > 0m)
                {
                    AddZiynetSettlementTransaction(tenantId, req.CustomerId, branchId, normAd, tipDisplay,
                        settleRemaining, "MANUAL", -1, "Borclu", req, txDate, batchId, txType);
                }
            }
            else
            {
                // Tahsilat: önce borç sütunundan düş, kalan alacağa yaz.
                var offsetBorc = Math.Min(grossBorc, settleRemaining);
                if (offsetBorc > 0m)
                {
                    AddZiynetSettlementTransaction(tenantId, req.CustomerId, branchId, normAd, tipDisplay,
                        offsetBorc, CustomerFinanceHelper.RefSettleBorc, 1, "Tahsilat", req, txDate, batchId, txType);
                    settleRemaining -= offsetBorc;
                }
                if (settleRemaining > 0m)
                {
                    AddZiynetSettlementTransaction(tenantId, req.CustomerId, branchId, normAd, tipDisplay,
                        settleRemaining, "MANUAL", 1, "Alacakli", req, txDate, batchId, txType);
                }
            }

            if (open.ContainsKey(key))
                open[key] = open[key] > 0 ? open[key] - it.Adet : open[key] + it.Adet;
        }
    }

    private void AddZiynetSettlementTransaction(
        Guid tenantId, Guid customerId, Guid branchId, string itemName, string? tipDisplay,
        decimal quantity, string refType, int direction, string cariDurum,
        ProcessReq req, DateTime txDate, Guid batchId, string txType)
    {
        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "ZIYNET",
            ItemName = itemName,
            ItemType = tipDisplay,
            Quantity = decimal.Round(quantity, 3, MidpointRounding.AwayFromZero),
            Direction = direction,
            TxDate = txDate,
            CariDurum = cariDurum,
            RefType = refType,
            Note = string.IsNullOrWhiteSpace(req.Description)
                ? $"Ziynet {(txType == "COLLECTION" ? "tahsilatı" : "ödemesi")}: {itemName} ({tipDisplay ?? "Yeni"})"
                : req.Description,
            BatchId = batchId
        });
    }

    private async Task ApplyIscilikliAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, Guid batchId, CancellationToken ct)
    {
        var items = req.IscilikliItems ?? new();
        if (items.Count == 0) return;

        foreach (var it in items)
        {
            CustomerTransaction? baseTx = null;
            if (it.TransactionId.HasValue && it.TransactionId.Value != Guid.Empty)
            {
                baseTx = await _db.CustomerTransactions
                    .FirstOrDefaultAsync(x =>
                        x.Id == it.TransactionId.Value &&
                        x.TenantId == tenantId &&
                        x.BranchId == branchId &&
                        x.CustomerId == req.CustomerId &&
                        !x.IsDeleted &&
                        x.GroupCode == "ISCILIKLI", ct);
            }

            if (baseTx is null)
            {
                var name = (it.UrunAdi ?? "").Trim().ToUpperInvariant();
                var ayar = (it.Ayar ?? "").Trim().ToUpperInvariant();
                baseTx = await _db.CustomerTransactions
                    .Where(x =>
                        x.TenantId == tenantId &&
                        x.BranchId == branchId &&
                        x.CustomerId == req.CustomerId &&
                        !x.IsDeleted &&
                        x.GroupCode == "ISCILIKLI" &&
                        x.ItemName.ToUpper() == name &&
                        (x.Ayar ?? "").ToUpper() == ayar)
                    .OrderByDescending(x => x.TxDate)
                    .FirstOrDefaultAsync(ct);
            }

            if (baseTx is null) continue;

            // Tahsilatta sadece borçlu (direction -1), ödemede sadece alacaklı (direction +1) satır kapatılır.
            if (txType == "COLLECTION" && baseTx.Direction >= 0) continue;
            if (txType == "PAYMENT" && baseTx.Direction <= 0) continue;
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = req.CustomerId,
                BranchId = branchId,
                GroupCode = "AUDIT",
                ItemName = "ISCILIKLI_GUNCELLEME",
                ItemType = baseTx.ItemName,
                Quantity = Math.Abs(baseTx.Quantity),
                Gram = baseTx.Gram,
                Ayar = baseTx.Ayar,
                HasEquivalent = baseTx.HasEquivalent,
                TotalPriceTl = baseTx.TotalPriceTl,
                Direction = txType == "COLLECTION" ? 1 : -1,
                TxDate = txDate,
                CariDurum = txType == "COLLECTION" ? "Tahsilat" : "Odeme",
                RefType = "MANUAL_SETTLE",
                Note = req.Description,
                BatchId = batchId
            });

            // İstenen davranış: seçilen işçilikli kalem listeden düşsün, benzer satır eklenmesin.
            baseTx.IsDeleted = true;
        }
    }

    private static void ApplyCustomerBalanceDelta(CustomerBalance b, string unit, decimal delta)
    {
        switch (unit)
        {
            case "USD":
                b.BalanceUSD += delta;
                break;
            case "EUR":
                b.BalanceEUR += delta;
                break;
            case "GBP":
                b.BalanceGBP += delta;
                break;
            case "HAS":
                b.BalanceHAS += delta;
                break;
            case "GUMUS":
                // CustomerBalance tablosunda henüz ayrı GUMUS kolonu yok.
                // Gümüş bakiye DOVIZ işlem satırlarından hesaplanır.
                break;
            default:
                b.BalanceTL += delta;
                break;
        }
    }

    private static decimal GetCustomerBalanceByUnit(CustomerBalance b, string unit)
    {
        return unit switch
        {
            "USD" => b.BalanceUSD,
            "EUR" => b.BalanceEUR,
            "GBP" => b.BalanceGBP,
            "HAS" => b.BalanceHAS,
            // CustomerBalance tablosunda GUMUS alanı bulunmadığı için fallback 0.
            "GUMUS" => 0m,
            _ => b.BalanceTL
        };
    }


    private static string NormalizeUnit(string? raw)
    {
        var u = (raw ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "TL" => "TL",
            "USD" => "USD",
            "EUR" => "EUR",
            "GBP" => "GBP",
            "POUND" => "GBP",
            "HAS" => "HAS",
            "GOLD" => "HAS",
            "GUMUS" => "GUMUS",
            "GÜMÜŞ" => "GUMUS",
            "SILVER" => "GUMUS",
            _ => "TL"
        };
    }

    private void ApplyZiynetUrunStokAudit(
        ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, Guid batchId)
    {
        var items = ZiynetUrunStokMarker.FromReqItems(req.ZiynetUrunStokItems);
        if (items.Count == 0)
            items = ZiynetUrunStokMarker.Parse(req.Description);
        items = ZiynetUrunStokMarker.MergeDistinct(items);
        if (items.Count == 0) return;

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = req.CustomerId,
            BranchId = branchId,
            GroupCode = "AUDIT",
            ItemName = "ZIYNET_URUN_STOK",
            ItemType = txType,
            Quantity = items.Sum(x => x.Adet),
            Direction = 0,
            TxDate = txDate,
            Note = ZiynetUrunStokMarker.BuildMarker(items),
            RefType = txType,
            BatchId = batchId,
            CariDurum = txType == "COLLECTION" ? "Tahsilat" : "Odeme"
        });
    }

    private static string? NormalizeTxType(string? raw)
    {
        var t = (raw ?? "").Trim().ToUpperInvariant();
        if (t is "COLLECTION" or "TAHSILAT") return "COLLECTION";
        if (t is "PAYMENT" or "ODEME") return "PAYMENT";
        if (t is "OPENING_BALANCE" or "ACILIS_BAKIYE" or "ACILIS_BAKIYE_GIRISI") return "OPENING_BALANCE";
        if (t is "BALANCE_CONVERSION" or "BAKIYE_DONUSTURME") return "BALANCE_CONVERSION";
        return null;
    }

    private static string NormalizeCariDurum(string? raw)
    {
        var txt = (raw ?? "").Trim().ToUpperInvariant();
        if (txt.Contains("EMANET")) return "EMANET";
        if (txt.Contains("BOR")) return "BORCLU";
        return "ALACAKLI";
    }

    public sealed record ReverseReq(string Reason);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDetail([FromRoute] Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var tx = await _db.CustomerTransactions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (tx is null) return NotFound();
        var detail = await _reversal.GetCustomerDetailAsync(tenantId, tx.CustomerId, id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("{id:guid}/reverse")]
    public async Task<IActionResult> Reverse([FromRoute] Guid id, [FromBody] ReverseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var tx = await _db.CustomerTransactions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (tx is null) return NotFound();
        var (userId, userName) = ResolveUser();
        var result = await _reversal.ReverseCustomerAsync(
            tenantId, branchId, tx.CustomerId, id, req.Reason ?? "", userId, userName, ct);
        if (!result.Ok) return BadRequest(new { error = result.Error });
        return Ok(new { ok = true, reversalLogId = result.ReversalLogId, batchId = result.BatchId });
    }

    [HttpPost("batch/{batchId:guid}/reverse")]
    public async Task<IActionResult> ReverseBatch([FromRoute] Guid batchId, [FromBody] ReverseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var anchor = await _db.CustomerTransactions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BatchId == batchId && !x.IsDeleted, ct);
        if (anchor is null) return NotFound();
        var (userId, userName) = ResolveUser();
        var result = await _reversal.ReverseCustomerByBatchAsync(
            tenantId, branchId, anchor.CustomerId, batchId, req.Reason ?? "", userId, userName, ct);
        if (!result.Ok) return BadRequest(new { error = result.Error });
        return Ok(new { ok = true, reversalLogId = result.ReversalLogId, batchId = result.BatchId });
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
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Branch-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("BranchId missing (JWT veya X-Branch-Id).");
    }

    private (Guid? userId, string? userName) ResolveUser()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals(System.Security.Claims.ClaimTypes.NameIdentifier, StringComparison.OrdinalIgnoreCase) ||
            c.Type.Equals("sub", StringComparison.OrdinalIgnoreCase))?.Value;
        Guid? userId = Guid.TryParse(claim, out var g) ? g : null;
        var name = User?.Claims?.FirstOrDefault(c => c.Type.Equals("full_name", StringComparison.OrdinalIgnoreCase))?.Value
                   ?? User?.Identity?.Name;
        return (userId, name);
    }
}
