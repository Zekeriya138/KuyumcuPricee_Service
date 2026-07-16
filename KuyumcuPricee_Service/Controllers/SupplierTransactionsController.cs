using System.Globalization;
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
public sealed class SupplierTransactionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ExchangeRateService _rates;
    private readonly TransactionReversalService _reversal;

    public SupplierTransactionsController(AppDbContext db, ExchangeRateService rates, TransactionReversalService reversal)
    {
        _db = db;
        _rates = rates;
        _reversal = reversal;
    }

    public sealed record CreateSupplierTransactionReq(
        Guid SupplierId,
        Guid? BranchId,
        string TxType,
        string SourceUnit,
        decimal SourceAmount,
        string TargetUnit,
        bool IsConvertEnabled,
        List<ZiynetSettlementReq>? ZiynetItems,
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
    public sealed record OpeningBalanceReq(string Unit, decimal Amount);
    public sealed record ZiynetSettlementReq(string Ad, string Tip, decimal Adet, string? CariDurum);

    public sealed record SupplierTransactionDto(
        Guid Id,
        Guid SupplierId,
        string TxType,
        string SourceUnit,
        decimal SourceAmount,
        string TargetUnit,
        decimal TargetAmount,
        bool IsConverted,
        decimal SourceUnitTlRate,
        decimal TargetUnitTlRate,
        string? Description,
        DateTime TxDate);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSupplierTransactionReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        if (req.BranchId.HasValue && req.BranchId.Value != Guid.Empty && req.BranchId.Value != branchId)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });

        if (req.SupplierId == Guid.Empty)
            return BadRequest(new { error = "SupplierId zorunludur." });
        var supplier = await _db.Suppliers
            .FirstOrDefaultAsync(x => x.Id == req.SupplierId && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (supplier is null)
            return BadRequest(new { error = "Geçersiz SupplierId." });

        var txType = NormalizeTxType(req.TxType);
        if (txType is null)
            return BadRequest(new { error = "TxType PAYMENT/COLLECTION/OPENING_BALANCE/BALANCE_CONVERSION olmalıdır." });

        var effectiveDescription = ZiynetUrunStokMarker.AppendDescription(
            req.Description,
            ZiynetUrunStokMarker.FromReqItems(req.ZiynetUrunStokItems));
        req = req with { Description = effectiveDescription };

        var hasZiynetSettlement = txType == "PAYMENT" && req.ZiynetItems is { Count: > 0 };
        if (txType != "OPENING_BALANCE" && req.SourceAmount <= 0 && !hasZiynetSettlement)
            return BadRequest(new { error = "Miktar 0'dan büyük olmalıdır." });

        var sourceUnit = NormalizeUnit(req.SourceUnit);
        var targetUnit = req.IsConvertEnabled ? NormalizeUnit(req.TargetUnit) : sourceUnit;
        decimal sourceTlRate = 1m;
        decimal targetTlRate = 1m;
        decimal sourceAmount = 0m;
        decimal targetAmount = 0m;
        if (txType is "PAYMENT" or "COLLECTION")
        {
            var buyRates = _rates.GetUnitToTlBuyRates();
            var sellRates = _rates.GetUnitToTlSellRates();
            var sourceRates = txType == "COLLECTION" ? buyRates : sellRates;
            var targetRates = txType == "COLLECTION" ? sellRates : buyRates;
            if (!sourceRates.TryGetValue(sourceUnit, out sourceTlRate) || sourceTlRate <= 0)
                return BadRequest(new { error = $"Kaynak birim kuru bulunamadı: {sourceUnit}" });
            if (!targetRates.TryGetValue(targetUnit, out targetTlRate) || targetTlRate <= 0)
                return BadRequest(new { error = $"Hedef birim kuru bulunamadı: {targetUnit}" });

            sourceAmount = decimal.Round(req.SourceAmount, 6);
            targetAmount = req.IsConvertEnabled
                ? decimal.Round((sourceAmount * sourceTlRate) / targetTlRate, 6)
                : sourceAmount;
            if (req.IsConvertEnabled && TryConvertHasByKgRateForSettlement(sourceUnit, targetUnit, txType, sourceAmount, out var kgConverted))
                targetAmount = kgConverted;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
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

            var txDate = req.TxDate?.ToUniversalTime() ?? DateTime.UtcNow;
            var batchId = Guid.NewGuid();
            SupplierTransaction? lastEntity = null;
            var lastEntityAdded = false;
            if (txType == "OPENING_BALANCE")
            {
                var opening = req.OpeningBalances ?? new List<OpeningBalanceReq>();
                var ziynetItems = req.ZiynetItems ?? new List<ZiynetSettlementReq>();
                var hasOpening = opening.Any(x => x.Amount != 0m);
                var hasZiynet = ziynetItems.Any(z => z is not null && z.Adet > 0m && !string.IsNullOrWhiteSpace(z.Ad));
                if (!hasOpening && !hasZiynet)
                    return BadRequest(new { error = "Açılış bakiyesi için en az bir bakiye veya ziynet satırı girilmelidir." });

                foreach (var row in opening.Where(x => x.Amount != 0m))
                {
                    var unit = NormalizeUnit(row.Unit);
                    var amount = decimal.Round(row.Amount, 6, MidpointRounding.AwayFromZero);
                    ApplyBalanceDelta(bal, unit, amount);
                    lastEntity = new SupplierTransaction
                    {
                        TenantId = tenantId,
                        SupplierId = supplier.Id,
                        BranchId = branchId,
                        TxType = txType,
                        SourceUnit = unit,
                        SourceAmount = amount,
                        TargetUnit = unit,
                        TargetAmount = amount,
                        IsConverted = false,
                        SourceUnitTlRate = 1m,
                        TargetUnitTlRate = 1m,
                        Description = string.IsNullOrWhiteSpace(req.Description) ? "Açılış bakiye girişi" : req.Description.Trim(),
                        TxDate = txDate,
                        BatchId = batchId
                    };
                    _db.SupplierTransactions.Add(lastEntity);
                }

                foreach (var ziynet in ziynetItems
                             .Where(z => z is not null && z.Adet > 0m && !string.IsNullOrWhiteSpace(z.Ad)))
                {
                    var adet = decimal.Round(Math.Abs(ziynet.Adet), 3, MidpointRounding.AwayFromZero);
                    var ad = (ziynet.Ad ?? "").Trim();
                    var tip = NormalizeOpeningZiynetTip(ad, ziynet.Tip);
                    if (adet <= 0m || string.IsNullOrWhiteSpace(ad))
                        continue;

                    // Alacaklı → +adet (alacak sütunu); Borçlu → -adet (borç sütunu) — müşteri açılış bakiyesi ile aynı mantık
                    var signed = NormalizeCariDurum(ziynet.CariDurum) == "BORCLU" ? -adet : adet;
                    if (IsHasAltinZiynetAd(ad))
                    {
                        ApplyBalanceDelta(bal, "HAS", signed);
                        lastEntity = new SupplierTransaction
                        {
                            TenantId = tenantId,
                            SupplierId = supplier.Id,
                            BranchId = branchId,
                            TxType = txType,
                            SourceUnit = "HAS",
                            SourceAmount = adet,
                            TargetUnit = "HAS",
                            TargetAmount = signed,
                            IsConverted = false,
                            SourceUnitTlRate = 1m,
                            TargetUnitTlRate = 1m,
                            Description = string.IsNullOrWhiteSpace(req.Description) ? "Açılış bakiye girişi (HAS)" : req.Description.Trim(),
                            TxDate = txDate,
                            BatchId = batchId
                        };
                        _db.SupplierTransactions.Add(lastEntity);
                        continue;
                    }

                    lastEntity = new SupplierTransaction
                    {
                        TenantId = tenantId,
                        SupplierId = supplier.Id,
                        BranchId = branchId,
                        TxType = "ZIYNET",
                        SourceUnit = "ADET",
                        SourceAmount = adet,
                        TargetUnit = "ADET",
                        TargetAmount = signed,
                        IsConverted = false,
                        SourceUnitTlRate = 1m,
                        TargetUnitTlRate = 1m,
                        Description = BuildSupplierZiynetDescription(ad, tip, signed, "OPENING_BALANCE"),
                        TxDate = txDate,
                        BatchId = batchId
                    };
                    _db.SupplierTransactions.Add(lastEntity);
                }
            }
            else if (txType == "BALANCE_CONVERSION")
            {
                if (!BalanceConversionZiynetHelper.TryParseUnit(req.SourceUnit, out var srcU))
                    return BadRequest(new { error = "Kaynak birim geçersiz." });
                if (!BalanceConversionZiynetHelper.TryParseUnit(req.TargetUnit, out var tgtU))
                    return BadRequest(new { error = "Hedef birim geçersiz." });
                if (BalanceConversionZiynetHelper.UnitsEqual(srcU, tgtU))
                    return BadRequest(new { error = "Dönüşüm için kaynak ve hedef birim farklı olmalıdır." });

                // Kaynak→TL ve TL→hedef için ayrı alış/satış yönü (tedarikçi mantığı).
                var sourceBalance = srcU.IsZiynet
                    ? await GetSupplierZiynetNetAsync(tenantId, supplier.Id, branchId, srcU.ZiynetAd, srcU.ZiynetTip, ct)
                    : GetBalanceByUnit(bal, srcU.CurrencyUnit);
                var ledgerSide = CustomerFinanceHelper.NormalizeLedgerSide(req.SourceLedgerSide);
                if (string.IsNullOrEmpty(ledgerSide))
                    ledgerSide = sourceBalance < 0m ? CustomerFinanceHelper.LedgerBorc : CustomerFinanceHelper.LedgerAlacak;
                var (useBuySrc, useBuyTgt) = CustomerFinanceHelper.IsLedgerAlacak(ledgerSide)
                    ? (true, false)
                    : (false, true);

                var srcRate = req.SourceUnitTlRate is > 0m
                    ? req.SourceUnitTlRate.Value
                    : BalanceConversionZiynetHelper.ResolveUnitTlRate(_rates, srcU, useBuySrc);
                var tgtRate = req.TargetUnitTlRate is > 0m
                    ? req.TargetUnitTlRate.Value
                    : BalanceConversionZiynetHelper.ResolveUnitTlRate(_rates, tgtU, useBuyTgt);
                if (srcRate <= 0m)
                    return BadRequest(new { error = $"Kaynak birim kuru bulunamadı: {BalanceConversionZiynetHelper.FormatUnitLabel(srcU)}" });
                if (tgtRate <= 0m)
                    return BadRequest(new { error = $"Hedef birim kuru bulunamadı: {BalanceConversionZiynetHelper.FormatUnitLabel(tgtU)}" });

                var srcAmt = decimal.Round(req.SourceAmount, 6, MidpointRounding.AwayFromZero);
                if (srcAmt <= 0m)
                    return BadRequest(new { error = "Dönüştürülecek miktar 0'dan büyük olmalıdır." });
                var tgtAmt = req.TargetAmount is > 0m
                    ? decimal.Round(req.TargetAmount.Value, 6, MidpointRounding.AwayFromZero)
                    : decimal.Round((srcAmt * srcRate) / tgtRate, 6, MidpointRounding.AwayFromZero);

                var srcDelta = CustomerFinanceHelper.BuildReductionLeg(ledgerSide, srcAmt).BalanceDelta;
                var tgtDelta = CustomerFinanceHelper.BuildAdditionLeg(ledgerSide, tgtAmt).BalanceDelta;
                var note = BalanceConversionZiynetHelper.BuildConversionNote(req.Description, srcAmt, srcU, tgtAmt, tgtU, useBuySrc, useBuyTgt);

                ApplySupplierConversionSide(bal, tenantId, supplier.Id, branchId, srcU, srcAmt, srcDelta, srcRate, note, txDate, batchId);
                lastEntity = ApplySupplierConversionSide(bal, tenantId, supplier.Id, branchId, tgtU, tgtAmt, tgtDelta, tgtRate, note, txDate, batchId);
                lastEntityAdded = true;
            }
            else
            {
                var signed = txType == "PAYMENT" ? -targetAmount : targetAmount;
                ApplyBalanceDelta(bal, targetUnit, signed);

                lastEntity = new SupplierTransaction
                {
                    TenantId = tenantId,
                    SupplierId = supplier.Id,
                    BranchId = branchId,
                    TxType = txType,
                    SourceUnit = sourceUnit,
                    SourceAmount = sourceAmount,
                    TargetUnit = targetUnit,
                    TargetAmount = targetAmount,
                    IsConverted = req.IsConvertEnabled,
                    SourceUnitTlRate = sourceTlRate,
                    TargetUnitTlRate = targetTlRate,
                    Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                    TxDate = txDate,
                    BatchId = batchId
                };
                await ApplyCashMovementAsync(req, tenantId, branchId, txType, sourceUnit, lastEntity.TxDate, supplier.Id, batchId, ct);

                if (txType == "PAYMENT" && req.ZiynetItems is { Count: > 0 })
                {
                    foreach (var ziynet in req.ZiynetItems
                                 .Where(z => z is not null && z.Adet > 0m && !string.IsNullOrWhiteSpace(z.Ad)))
                    {
                        var adet = decimal.Round(ziynet.Adet, 3, MidpointRounding.AwayFromZero);
                        var ad = (ziynet.Ad ?? "").Trim();
                        var tip = NormalizeZiynetTip(ziynet.Tip);
                        if (adet <= 0m || string.IsNullOrWhiteSpace(ad))
                            continue;

                        // Has altın: adet defteri yerine HAS döviz bakiyesinden düş.
                        if (IsHasAltinZiynetAd(ad))
                        {
                            ApplyBalanceDelta(bal, "HAS", -adet);
                            _db.SupplierTransactions.Add(new SupplierTransaction
                            {
                                TenantId = tenantId,
                                SupplierId = supplier.Id,
                                BranchId = branchId,
                                TxType = "PAYMENT",
                                SourceUnit = "HAS",
                                SourceAmount = adet,
                                TargetUnit = "HAS",
                                TargetAmount = -adet,
                                IsConverted = false,
                                SourceUnitTlRate = 1m,
                                TargetUnitTlRate = 1m,
                                Description = $"Has altın ödemesi (SUPPLIER_PAYMENT:{supplier.Id}, Tutar: {adet.ToString("0.###", CultureInfo.InvariantCulture)} gr)",
                                TxDate = txDate,
                                BatchId = batchId
                            });
                            continue;
                        }

                        _db.SupplierTransactions.Add(new SupplierTransaction
                        {
                            TenantId = tenantId,
                            SupplierId = supplier.Id,
                            BranchId = branchId,
                            TxType = "ZIYNET",
                            SourceUnit = "ADET",
                            SourceAmount = adet,
                            TargetUnit = "ADET",
                            TargetAmount = -adet,
                            IsConverted = false,
                            SourceUnitTlRate = 1m,
                            TargetUnitTlRate = 1m,
                            Description = BuildSupplierZiynetDescription(ad, tip, -adet, $"SUPPLIER_PAYMENT:{supplier.Id}"),
                            TxDate = txDate,
                            BatchId = batchId
                        });
                    }
                }
            }

            bal.UpdatedAt = DateTime.UtcNow;
            if (txType != "OPENING_BALANCE" && !lastEntityAdded && lastEntity is not null)
                _db.SupplierTransactions.Add(lastEntity);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            if (lastEntity is null)
                return Ok(new { ok = true, batchId });

            return Ok(new
            {
                batchId,
                transaction = new SupplierTransactionDto(
                lastEntity.Id,
                lastEntity.SupplierId,
                lastEntity.TxType,
                lastEntity.SourceUnit,
                lastEntity.SourceAmount,
                lastEntity.TargetUnit,
                lastEntity.TargetAmount,
                lastEntity.IsConverted,
                lastEntity.SourceUnitTlRate,
                lastEntity.TargetUnitTlRate,
                lastEntity.Description,
                lastEntity.TxDate)
            });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ApplyCashMovementAsync(
        CreateSupplierTransactionReq req,
        Guid tenantId,
        Guid branchId,
        string txType,
        string sourceUnit,
        DateTime txDate,
        Guid supplierId,
        Guid batchId,
        CancellationToken ct)
    {
        var unit = NormalizeUnit(sourceUnit);
        var nakit = decimal.Round(Math.Max(0m, req.NakitAmount ?? 0m), 6, MidpointRounding.AwayFromZero);
        var havale = decimal.Round(Math.Max(0m, req.HavaleAmount ?? 0m), 6, MidpointRounding.AwayFromZero);
        if (nakit <= 0m && havale <= 0m) return;

        if (nakit > 0m)
        {
            await AddCashMovementAsync(
                tenantId, branchId, unit, nakit, txType,
                "Nakit", "TedarikciIslem", "SUPPLIER_SETTLEMENT", supplierId, txDate, batchId, ct);
        }
        if (havale > 0m)
        {
            await AddCashMovementAsync(
                tenantId, branchId, unit, havale, txType,
                "Havale", "TedarikciIslem", "SUPPLIER_SETTLEMENT", supplierId, txDate, batchId, ct);
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
                ? $"Tedarikçi tahsilat ({paymentMethod.ToLowerInvariant()})"
                : $"Tedarikçi ödeme ({paymentMethod.ToLowerInvariant()})",
            BatchId = batchId
        });
    }

    /// <summary>Has altın ziynet kalemi mi? (Külçe ve 22 ayar gram ziynet hariç) → DOVIZ/HAS bakiyesine yazılır.</summary>
    private static bool IsHasAltinZiynetAd(string? ad)
    {
        var s = (ad ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Contains("KÜLÇE") || s.Contains("KULCE") || s.Contains("22 AYAR") || s.Contains("22AYAR"))
            return false;
        return s.Contains("HAS ALTIN") || s.Contains("HASALTIN") || s.Contains("HAS ALTİN") || s == "HAS";
    }

    private static void ApplyBalanceDelta(SupplierBalance b, string unit, decimal delta)
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
                b.BalanceGUMUS += delta;
                break;
            default:
                b.BalanceTL += delta;
                break;
        }
    }

    private static decimal GetBalanceByUnit(SupplierBalance b, string unit)
    {
        return unit switch
        {
            "USD" => b.BalanceUSD,
            "EUR" => b.BalanceEUR,
            "GBP" => b.BalanceGBP,
            "HAS" => b.BalanceHAS,
            "GUMUS" => b.BalanceGUMUS,
            _ => b.BalanceTL
        };
    }

    private bool TryConvertHasByKgRateForSettlement(string sourceUnit, string targetUnit, string txType, decimal sourceAmount, out decimal converted)
    {
        converted = 0m;
        if (sourceAmount <= 0m) return false;
        if (sourceUnit != "HAS") return false;
        if (targetUnit is not ("USD" or "EUR")) return false;
        if (txType is not ("COLLECTION" or "PAYMENT")) return false;

        var code = targetUnit == "USD" ? "XAU_KG_USD" : "XAU_KG_EUR";
        var kgRate = txType == "COLLECTION"
            ? _rates.GetQuoteBidByCode(code)   // Tahsilat: alış
            : _rates.GetQuoteAskByCode(code);  // Ödeme: satış
        if (kgRate <= 0m) return false;

        converted = decimal.Round(sourceAmount * (kgRate / 1000m), 6, MidpointRounding.AwayFromZero);
        return converted > 0m;
    }

    private bool TryConvertHasByKgRateForBalanceConversion(string sourceUnit, string targetUnit, decimal sourceBalance, decimal sourceAmount, out decimal converted)
    {
        converted = 0m;
        if (sourceAmount <= 0m) return false;
        if (sourceUnit != "HAS") return false;
        if (targetUnit is not ("USD" or "EUR")) return false;

        var code = targetUnit == "USD" ? "XAU_KG_USD" : "XAU_KG_EUR";
        var useSell = sourceBalance < 0m; // kaynak eksi ise satış, artı/0 ise alış
        var kgRate = useSell
            ? _rates.GetQuoteAskByCode(code)
            : _rates.GetQuoteBidByCode(code);
        if (kgRate <= 0m) return false;

        converted = decimal.Round(sourceAmount * (kgRate / 1000m), 6, MidpointRounding.AwayFromZero);
        return converted > 0m;
    }

    private static string? NormalizeTxType(string? raw)
    {
        var t = (raw ?? "").Trim().ToUpperInvariant();
        if (t is "ODEME" or "PAYMENT") return "PAYMENT";
        if (t is "TAHSILAT" or "COLLECTION") return "COLLECTION";
        if (t is "OPENING_BALANCE" or "ACILIS_BAKIYE" or "ACILIS_BAKIYE_GIRISI") return "OPENING_BALANCE";
        if (t is "BALANCE_CONVERSION" or "BAKIYE_DONUSTURME") return "BALANCE_CONVERSION";
        return null;
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

    private static string NormalizeZiynetTip(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? "Yeni" : raw.Trim();

    private static string NormalizeOpeningZiynetTip(string ad, string? raw)
    {
        if (IsOpeningZiynetTipless(ad)) return "";
        return NormalizeZiynetTip(raw);
    }

    private static bool IsOpeningZiynetTipless(string? ad)
    {
        var s = (ad ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Contains("KÜLÇE") || s.Contains("KULCE") || s.Contains("22 AYAR") || s.Contains("22AYAR"))
            return true;
        return s.Contains("GRAM") && s.Contains("HAS");
    }

    private static string NormalizeCariDurum(string? raw)
    {
        var txt = (raw ?? "").Trim().ToUpperInvariant();
        if (txt.Contains("EMANET")) return "EMANET";
        if (txt.Contains("BOR")) return "BORCLU";
        return "ALACAKLI";
    }

    private static string BuildSupplierZiynetDescription(string ad, string tip, decimal adet, string reference)
    {
        static string Safe(string? raw) => (raw ?? "").Replace("|", "/").Replace(";", ",").Trim();
        return $"[ZIYNET]|AD={Safe(ad)}|TIP={Safe(tip)}|ADET={adet.ToString("0.###", CultureInfo.InvariantCulture)}|REF={Safe(reference)}";
    }

    private SupplierTransaction ApplySupplierConversionSide(
        SupplierBalance bal, Guid tenantId, Guid supplierId, Guid branchId,
        BalanceConversionZiynetHelper.ConversionUnit unit,
        decimal amount, decimal delta, decimal rate, string note, DateTime txDate, Guid batchId)
    {
        SupplierTransaction entity;
        if (unit.IsZiynet)
        {
            var tip = NormalizeOpeningZiynetTip(unit.ZiynetAd, unit.ZiynetTip);
            entity = new SupplierTransaction
            {
                TenantId = tenantId,
                SupplierId = supplierId,
                BranchId = branchId,
                TxType = "ZIYNET",
                SourceUnit = "ADET",
                SourceAmount = amount,
                TargetUnit = "ADET",
                TargetAmount = delta,
                IsConverted = false,
                SourceUnitTlRate = 1m,
                TargetUnitTlRate = 1m,
                Description = BuildSupplierZiynetDescription(unit.ZiynetAd, tip, delta, "BALANCE_CONVERSION"),
                TxDate = txDate,
                BatchId = batchId
            };
        }
        else
        {
            ApplyBalanceDelta(bal, unit.CurrencyUnit, delta);
            entity = new SupplierTransaction
            {
                TenantId = tenantId,
                SupplierId = supplierId,
                BranchId = branchId,
                TxType = "BALANCE_CONVERSION",
                SourceUnit = unit.CurrencyUnit,
                SourceAmount = amount,
                TargetUnit = unit.CurrencyUnit,
                TargetAmount = delta,
                IsConverted = true,
                SourceUnitTlRate = rate,
                TargetUnitTlRate = rate,
                Description = note,
                TxDate = txDate,
                BatchId = batchId
            };
        }
        _db.SupplierTransactions.Add(entity);
        return entity;
    }

    private async Task<decimal> GetSupplierZiynetNetAsync(
        Guid tenantId, Guid supplierId, Guid branchId, string ad, string? tip, CancellationToken ct)
    {
        var targetCode = BalanceConversionZiynetHelper.ZiynetRateCode(ad, tip);
        if (string.IsNullOrEmpty(targetCode)) return 0m;

        var rows = await _db.SupplierTransactions
            .Where(x => x.TenantId == tenantId && x.SupplierId == supplierId && x.BranchId == branchId
                        && !x.IsDeleted && x.TxType == "ZIYNET")
            .Select(x => new { x.Description, x.TargetAmount })
            .ToListAsync(ct);

        decimal net = 0m;
        foreach (var r in rows)
        {
            if (!TryGetZiynetAdTipFromDescription(r.Description, out var rad, out var rtip)) continue;
            var code = BalanceConversionZiynetHelper.ZiynetRateCode(rad, rtip);
            if (!string.Equals(code, targetCode, StringComparison.OrdinalIgnoreCase)) continue;
            net += r.TargetAmount;
        }
        return net;
    }

    private static bool TryGetZiynetAdTipFromDescription(string? desc, out string ad, out string tip)
    {
        ad = "";
        tip = "";
        var d = (desc ?? "").Trim();
        if (!d.Contains("[ZIYNET]|", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var rawPart in d.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = rawPart.Trim();
            if (p.StartsWith("AD=", StringComparison.OrdinalIgnoreCase)) ad = p.Substring(3).Trim();
            else if (p.StartsWith("TIP=", StringComparison.OrdinalIgnoreCase)) tip = p.Substring(4).Trim();
        }
        return !string.IsNullOrWhiteSpace(ad);
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

    public sealed record ReverseReq(string Reason);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDetail([FromRoute] Guid id, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var tx = await _db.SupplierTransactions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (tx is null) return NotFound();
        var detail = await _reversal.GetSupplierDetailAsync(tenantId, tx.SupplierId, id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("{id:guid}/reverse")]
    public async Task<IActionResult> Reverse([FromRoute] Guid id, [FromBody] ReverseReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var tx = await _db.SupplierTransactions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (tx is null) return NotFound();
        var (userId, userName) = ResolveUser();
        var result = await _reversal.ReverseSupplierAsync(
            tenantId, branchId, tx.SupplierId, id, req.Reason ?? "", userId, userName, ct);
        if (!result.Ok) return BadRequest(new { error = result.Error });
        return Ok(new { ok = true, reversalLogId = result.ReversalLogId, batchId = result.BatchId });
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
