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

    public SupplierTransactionsController(AppDbContext db, ExchangeRateService rates)
    {
        _db = db;
        _rates = rates;
    }

    public sealed record CreateSupplierTransactionReq(
        Guid SupplierId,
        Guid? BranchId,
        string TxType,
        string SourceUnit,
        decimal SourceAmount,
        string TargetUnit,
        bool IsConvertEnabled,
        List<OpeningBalanceReq>? OpeningBalances,
        decimal? NakitAmount,
        decimal? HavaleAmount,
        string? Description,
        DateTime? TxDate);
    public sealed record OpeningBalanceReq(string Unit, decimal Amount);

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

        if (txType != "OPENING_BALANCE" && req.SourceAmount <= 0)
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
            SupplierTransaction entity;
            if (txType == "OPENING_BALANCE")
            {
                var opening = req.OpeningBalances ?? new List<OpeningBalanceReq>();
                if (opening.Count == 0 || !opening.Any(x => x.Amount != 0m))
                    return BadRequest(new { error = "Açılış bakiyesi için en az bir birim değeri girilmelidir." });
                entity = null!;
                foreach (var row in opening.Where(x => x.Amount != 0m))
                {
                    var unit = NormalizeUnit(row.Unit);
                    var amount = decimal.Round(row.Amount, 6, MidpointRounding.AwayFromZero);
                    ApplyBalanceDelta(bal, unit, amount);
                    entity = new SupplierTransaction
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
                        TxDate = txDate
                    };
                    _db.SupplierTransactions.Add(entity);
                }
            }
            else if (txType == "BALANCE_CONVERSION")
            {
                if (sourceUnit == targetUnit)
                    return BadRequest(new { error = "Dönüşüm için kaynak ve hedef birim farklı olmalıdır." });

                var sourceBalance = GetBalanceByUnit(bal, sourceUnit);
                var conversionRates = sourceBalance >= 0m
                    ? _rates.GetUnitToTlBuyRates()
                    : _rates.GetUnitToTlSellRates();
                var sourceRates = conversionRates;
                var targetRates = conversionRates;
                if (!sourceRates.TryGetValue(sourceUnit, out sourceTlRate) || sourceTlRate <= 0)
                    return BadRequest(new { error = $"Kaynak birim kuru bulunamadı: {sourceUnit}" });
                if (!targetRates.TryGetValue(targetUnit, out targetTlRate) || targetTlRate <= 0)
                    return BadRequest(new { error = $"Hedef birim kuru bulunamadı: {targetUnit}" });

                sourceAmount = decimal.Round(req.SourceAmount, 6, MidpointRounding.AwayFromZero);
                targetAmount = decimal.Round((sourceAmount * sourceTlRate) / targetTlRate, 6, MidpointRounding.AwayFromZero);
                if (TryConvertHasByKgRateForBalanceConversion(sourceUnit, targetUnit, sourceBalance, sourceAmount, out var kgConverted))
                    targetAmount = kgConverted;

                // Bakiye dönüşümünde yön birim-1 (kaynak) işaretine göre belirlenir.
                // kaynak eksi ise: kaynak (+), hedef (-)
                // kaynak artı/0 ise: kaynak (-), hedef (+)
                var sourceIsNegative = sourceBalance < 0m;
                var sourceDelta = sourceIsNegative ? +sourceAmount : -sourceAmount;
                var targetDelta = sourceIsNegative ? -targetAmount : +targetAmount;
                ApplyBalanceDelta(bal, sourceUnit, sourceDelta);
                ApplyBalanceDelta(bal, targetUnit, targetDelta);

                entity = new SupplierTransaction
                {
                    TenantId = tenantId,
                    SupplierId = supplier.Id,
                    BranchId = branchId,
                    TxType = txType,
                    SourceUnit = sourceUnit,
                    SourceAmount = sourceAmount,
                    TargetUnit = targetUnit,
                    TargetAmount = targetAmount,
                    IsConverted = true,
                    SourceUnitTlRate = sourceTlRate,
                    TargetUnitTlRate = targetTlRate,
                    Description = string.IsNullOrWhiteSpace(req.Description) ? "Bakiye dönüştürme" : req.Description.Trim(),
                    TxDate = txDate
                };
            }
            else
            {
                var signed = txType == "PAYMENT" ? -targetAmount : targetAmount;
                ApplyBalanceDelta(bal, targetUnit, signed);

                entity = new SupplierTransaction
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
                    TxDate = txDate
                };
                await ApplyCashMovementAsync(req, tenantId, branchId, txType, sourceUnit, entity.TxDate, supplier.Id, ct);
            }

            bal.UpdatedAt = DateTime.UtcNow;
            if (txType != "OPENING_BALANCE")
                _db.SupplierTransactions.Add(entity);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(new SupplierTransactionDto(
                entity.Id,
                entity.SupplierId,
                entity.TxType,
                entity.SourceUnit,
                entity.SourceAmount,
                entity.TargetUnit,
                entity.TargetAmount,
                entity.IsConverted,
                entity.SourceUnitTlRate,
                entity.TargetUnitTlRate,
                entity.Description,
                entity.TxDate));
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
                "Nakit", "TedarikciIslem", "SUPPLIER_SETTLEMENT", supplierId, txDate, ct);
        }
        if (havale > 0m)
        {
            await AddCashMovementAsync(
                tenantId, branchId, unit, havale, txType,
                "Havale", "TedarikciIslem", "SUPPLIER_SETTLEMENT", supplierId, txDate, ct);
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
                : $"Tedarikçi ödeme ({paymentMethod.ToLowerInvariant()})"
        });
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
}
