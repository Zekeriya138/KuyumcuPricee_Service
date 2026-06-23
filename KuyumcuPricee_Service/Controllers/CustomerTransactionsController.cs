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

    public CustomerTransactionsController(AppDbContext db, ExchangeRateService rates)
    {
        _db = db;
        _rates = rates;
    }

    public sealed record ZiynetSettleReq(string Ad, string? Tip, decimal Adet, decimal? BirimFiyatTl, decimal? BirimAlisTl, string? CariDurum);
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
        DateTime? TxDate);

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

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            if (txType == "OPENING_BALANCE")
            {
                await ApplyOpeningBalanceAsync(req, tenantId, branchId, txDate, ct);
            }
            else if (txType == "BALANCE_CONVERSION")
            {
                await ApplyBalanceConversionAsync(req, tenantId, branchId, txDate, ct);
            }
            else
            {
                await ApplyCurrencyAsync(req, tenantId, branchId, txType, txDate, ct);
                await ApplyZiynetAsync(req, tenantId, branchId, txType, txDate, ct);
                await ApplyIscilikliAsync(req, tenantId, branchId, txType, txDate, ct);
                await ApplyCashMovementAsync(req, tenantId, branchId, txType, txDate, ct);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new { ok = true });
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ApplyCurrencyAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, CancellationToken ct)
    {
        if (req.SourceAmount <= 0) return;

        var src = NormalizeUnit(req.SourceUnit);
        var tgt = req.IsConvertEnabled ? NormalizeUnit(req.TargetUnit) : src;
        var buyRates = _rates.GetUnitToTlBuyRates();
        var sellRates = _rates.GetUnitToTlSellRates();
        var srcMap = txType == "COLLECTION" ? buyRates : sellRates;
        var tgtMap = txType == "COLLECTION" ? sellRates : buyRates;
        var srcRate = srcMap.TryGetValue(src, out var sr) ? sr : 0m;
        var tgtRate = tgtMap.TryGetValue(tgt, out var tr) ? tr : 0m;
        if (srcRate <= 0 || tgtRate <= 0)
            throw new InvalidOperationException("Kur bilgisi alınamadı.");

        var srcAmt = decimal.Round(req.SourceAmount, 6);
        var tgtAmt = req.IsConvertEnabled ? decimal.Round((srcAmt * srcRate) / tgtRate, 6) : srcAmt;
        if (req.IsConvertEnabled && TryConvertHasByKgRate(src, tgt, txType, srcAmt, out var kgConverted))
            tgtAmt = kgConverted;

        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, req.CustomerId, ct);
        // İş kuralı:
        // COLLECTION (Tahsilat): müşteri bize öder -> negatif borç bakiyesi sıfıra yaklaşır (delta +)
        // PAYMENT (Ödeme): biz müşteriye öderiz -> müşterinin bize borcu artar / bizim borcumuz azalır (delta -)
        var delta = txType == "COLLECTION" ? +tgtAmt : -tgtAmt;
        ApplyCustomerBalanceDelta(bal, tgt, delta);
        bal.UpdatedAt = DateTime.UtcNow;

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = req.CustomerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = tgt,
            ItemType = null,
            Quantity = tgtAmt,
            Direction = delta >= 0 ? 1 : -1,
            UnitPriceTl = tgtRate,
            TotalPriceTl = decimal.Round(tgtAmt * tgtRate, 4),
            TxDate = txDate,
            CariDurum = delta >= 0 ? "Alacakli" : "Borclu",
            RefType = "MANUAL",
            Note = req.Description
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

    private async Task ApplyOpeningBalanceAsync(ProcessReq req, Guid tenantId, Guid branchId, DateTime txDate, CancellationToken ct)
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
                Note = req.Description
            });
        }

        foreach (var z in (req.ZiynetItems ?? new List<ZiynetSettleReq>()).Where(x => x.Adet > 0m && !string.IsNullOrWhiteSpace(x.Ad)))
        {
            var durum = NormalizeCariDurum(z.CariDurum);
            var dir = durum == "BORCLU" ? -1 : 1;
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = req.CustomerId,
                BranchId = branchId,
                GroupCode = "ZIYNET",
                ItemName = (z.Ad ?? string.Empty).Trim().ToUpperInvariant(),
                ItemType = string.IsNullOrWhiteSpace(z.Tip) ? "Yeni" : z.Tip!.Trim(),
                Quantity = decimal.Round(Math.Abs(z.Adet), 3, MidpointRounding.AwayFromZero),
                Direction = dir,
                TxDate = txDate,
                CariDurum = durum == "EMANET" ? "Emanet" : (dir >= 0 ? "Alacakli" : "Borclu"),
                RefType = "OPENING_BALANCE",
                Note = req.Description
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
                Note = req.Description
            });
        }

        bal.UpdatedAt = DateTime.UtcNow;
    }

    private async Task ApplyBalanceConversionAsync(ProcessReq req, Guid tenantId, Guid branchId, DateTime txDate, CancellationToken ct)
    {
        if (req.SourceAmount <= 0m)
            throw new InvalidOperationException("Dönüştürülecek kaynak miktar 0'dan büyük olmalıdır.");

        var src = NormalizeUnit(req.SourceUnit);
        var tgt = NormalizeUnit(req.TargetUnit);
        if (src == tgt)
            throw new InvalidOperationException("Kaynak ve hedef birim aynı olamaz.");

        var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, req.CustomerId, ct);
        var sourceBalance = GetCustomerBalanceByUnit(bal, src);
        var conversionMap = sourceBalance >= 0m
            ? _rates.GetUnitToTlBuyRates()
            : _rates.GetUnitToTlSellRates();
        var srcMap = conversionMap;
        var tgtMap = conversionMap;
        var srcRate = srcMap.TryGetValue(src, out var sr) ? sr : 0m;
        var tgtRate = tgtMap.TryGetValue(tgt, out var tr) ? tr : 0m;
        if (srcRate <= 0m || tgtRate <= 0m)
            throw new InvalidOperationException("Kur bilgisi alınamadı.");

        var srcAmt = decimal.Round(req.SourceAmount, 6, MidpointRounding.AwayFromZero);
        var tgtAmt = decimal.Round((srcAmt * srcRate) / tgtRate, 6, MidpointRounding.AwayFromZero);
        if (TryConvertHasByKgRateForBalanceConversion(src, tgt, sourceBalance, srcAmt, out var kgConverted))
            tgtAmt = kgConverted;

        // Bakiye dönüşümünde işaret yönü her zaman birim-1 (kaynak) bakiyesine göre belirlenir:
        // - Kaynak bakiye eksi ise: borç birim değiştirir -> kaynak (+), hedef (-)
        // - Kaynak bakiye artı ise: alacak birim değiştirir -> kaynak (-), hedef (+)
        var sourceIsNegative = sourceBalance < 0m;
        var srcDelta = sourceIsNegative ? +srcAmt : -srcAmt;
        var tgtDelta = sourceIsNegative ? -tgtAmt : +tgtAmt;
        ApplyCustomerBalanceDelta(bal, src, srcDelta);
        ApplyCustomerBalanceDelta(bal, tgt, tgtDelta);
        bal.UpdatedAt = DateTime.UtcNow;

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = req.CustomerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = src,
            Quantity = srcAmt,
            Direction = srcDelta >= 0m ? 1 : -1,
            UnitPriceTl = srcRate,
            TotalPriceTl = decimal.Round(srcAmt * srcRate, 4, MidpointRounding.AwayFromZero),
            TxDate = txDate,
            CariDurum = "Dönüşüm",
            RefType = "BALANCE_CONVERSION",
            Note = req.Description
        });
        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = req.CustomerId,
            BranchId = branchId,
            GroupCode = "DOVIZ",
            ItemName = tgt,
            Quantity = tgtAmt,
            Direction = tgtDelta >= 0m ? 1 : -1,
            UnitPriceTl = tgtRate,
            TotalPriceTl = decimal.Round(tgtAmt * tgtRate, 4, MidpointRounding.AwayFromZero),
            TxDate = txDate,
            CariDurum = "Dönüşüm",
            RefType = "BALANCE_CONVERSION",
            Note = string.IsNullOrWhiteSpace(req.Description)
                ? $"{srcAmt:N6} {src} -> {tgtAmt:N6} {tgt}"
                : req.Description
        });
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

    private async Task ApplyCashMovementAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, CancellationToken ct)
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
                : $"Müşteriye ödeme ({paymentMethod.ToLowerInvariant()})"
        });
    }

    private async Task ApplyZiynetAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, CancellationToken ct)
    {
        var items = req.ZiynetItems ?? new();
        if (items.Count == 0) return;
        var emanetFlow = (req.Description ?? "").Contains("emanet", StringComparison.OrdinalIgnoreCase);

        var rows = await _db.CustomerTransactions
            .Where(x => x.TenantId == tenantId && x.CustomerId == req.CustomerId && x.BranchId == branchId && !x.IsDeleted && x.GroupCode == "ZIYNET")
            .ToListAsync(ct);
        static string NormalizeZiynetKeyPart(string? raw)
            => (raw ?? string.Empty).Trim().ToUpperInvariant();

        var open = rows
            .GroupBy(x => (
                Ad: NormalizeZiynetKeyPart(x.ItemName),
                Tip: NormalizeZiynetKeyPart(string.IsNullOrWhiteSpace(x.ItemType) ? "Yeni" : x.ItemType)))
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

        foreach (var it in items.Where(x => x.Adet > 0))
        {
            var key = (
                NormalizeZiynetKeyPart(it.Ad),
                NormalizeZiynetKeyPart(string.IsNullOrWhiteSpace(it.Tip) ? "Yeni" : it.Tip));
            if (emanetFlow)
            {
                var emanetDirection = txType == "COLLECTION" ? 1 : -1;
                var birimFiyatTl = it.BirimFiyatTl.HasValue && it.BirimFiyatTl.Value > 0m
                    ? decimal.Round(it.BirimFiyatTl.Value, 6, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var birimAlisTl = it.BirimAlisTl.HasValue && it.BirimAlisTl.Value > 0m
                    ? decimal.Round(it.BirimAlisTl.Value, 6, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                var toplamTutarTl = birimFiyatTl.HasValue
                    ? decimal.Round(it.Adet * birimFiyatTl.Value, 2, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                _db.CustomerTransactions.Add(new CustomerTransaction
                {
                    TenantId = tenantId,
                    CustomerId = req.CustomerId,
                    BranchId = branchId,
                    GroupCode = "ZIYNET",
                    ItemName = key.Item1,
                    ItemType = key.Item2,
                    Quantity = it.Adet,
                    Direction = emanetDirection,
                    UnitPriceTl = birimFiyatTl,
                    TotalPriceTl = toplamTutarTl,
                    HasEquivalent = birimAlisTl,
                    TxDate = txDate,
                    CariDurum = "Emanet",
                    RefType = "SALE",
                    Note = req.Description
                });
                continue;
            }

            if (!open.TryGetValue(key, out var openQty) || openQty == 0) continue;

            if (txType == "COLLECTION" && openQty >= 0) continue;
            if (txType == "PAYMENT" && openQty <= 0) continue;

            var settle = Math.Min(Math.Abs(openQty), it.Adet);
            if (settle <= 0) continue;
            var consumeDirection = openQty > 0 ? 1 : -1;
            var remaining = settle;
            var candidates = rows
                .Where(x =>
                    !x.IsDeleted &&
                    x.Direction == consumeDirection &&
                    NormalizeZiynetKeyPart(x.ItemName) == key.Item1 &&
                    NormalizeZiynetKeyPart(string.IsNullOrWhiteSpace(x.ItemType) ? "Yeni" : x.ItemType) == key.Item2)
                .OrderByDescending(x => x.TxDate)
                .ThenByDescending(x => x.CreatedAt)
                .ToList();

            foreach (var row in candidates)
            {
                if (remaining <= 0m) break;
                var rowQty = Math.Max(0m, row.Quantity);
                if (rowQty <= 0m) continue;

                var use = Math.Min(rowQty, remaining);
                row.Quantity = decimal.Round(rowQty - use, 3, MidpointRounding.AwayFromZero);
                if (row.Quantity <= 0.0005m)
                {
                    row.Quantity = 0m;
                    row.IsDeleted = true;
                }
                remaining -= use;
            }

            open[key] = openQty > 0 ? openQty - settle : openQty + settle;

            // Bakiyeyi bozmadan Son İşlemler'de görünmesi için audit izi bırak.
            _db.CustomerTransactions.Add(new CustomerTransaction
            {
                TenantId = tenantId,
                CustomerId = req.CustomerId,
                BranchId = branchId,
                GroupCode = "AUDIT",
                ItemName = "ZIYNET_DUSUM",
                ItemType = null,
                Quantity = settle,
                Direction = txType == "COLLECTION" ? 1 : -1,
                TxDate = txDate,
                CariDurum = "Düşüm",
                RefType = "AUDIT",
                Note = string.IsNullOrWhiteSpace(req.Description)
                    ? $"Ziynet düşüm: {key.Item1} ({key.Item2}) | Adet: {settle:0.###}"
                    : $"{req.Description} | Ziynet düşüm: {key.Item1} ({key.Item2}) | Adet: {settle:0.###}"
            });
        }
    }

    private async Task ApplyIscilikliAsync(ProcessReq req, Guid tenantId, Guid branchId, string txType, DateTime txDate, CancellationToken ct)
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
                Note = req.Description
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
