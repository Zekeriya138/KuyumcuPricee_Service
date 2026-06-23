using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public sealed class AccountingJournalService : IAccountingJournalService
{
    private readonly AppDbContext _db;

    public AccountingJournalService(AppDbContext db)
    {
        _db = db;
    }

    public async Task RecordPurchaseAsync(
        Purchase purchase,
        IReadOnlyList<PurchasePayment> payments,
        CancellationToken ct = default)
    {
        if (purchase is null) return;
        if (purchase.GrandTotal <= 0m) return;
        if (await ExistsAsync(purchase.TenantId, "PURCHASE", purchase.Id, ct)) return;

        var inventory = await EnsureAccountAsync(purchase.TenantId, "1100", "Stoklar", AccountType.Asset, true, ct);
        var payable = await EnsureAccountAsync(purchase.TenantId, "2000", "Tedarikçi Borçları", AccountType.Liability, true, ct);
        var cash = await EnsureAccountAsync(purchase.TenantId, "1000", "Kasa", AccountType.Asset, true, ct);
        var bank = await EnsureAccountAsync(purchase.TenantId, "1010", "Banka", AccountType.Asset, true, ct);

        var lines = new List<(Guid accountId, decimal debit, decimal credit)>
        {
            (inventory.Id, purchase.GrandTotal, 0m)
        };

        var credited = 0m;
        foreach (var p in payments ?? [])
        {
            if (p.Amount <= 0m) continue;
            if (p.PaymentType == PurchasePaymentType.Takas) continue;
            var unit = NormalizeUnitCode(p.UnitCode);
            var journalAmount = ResolvePurchaseJournalAmount(p, unit);
            if (journalAmount <= 0m) continue;
            var liquidityAccount = await EnsureLiquidityAccountAsync(purchase.TenantId, unit, false, ct);
            switch (p.PaymentType)
            {
                case PurchasePaymentType.Cash:
                case PurchasePaymentType.Gold:
                case PurchasePaymentType.Takas:
                    lines.Add((liquidityAccount.Id, 0m, journalAmount));
                    break;
                case PurchasePaymentType.Bank:
                    lines.Add((bank.Id, 0m, journalAmount));
                    break;
                case PurchasePaymentType.Credit:
                    lines.Add((payable.Id, 0m, journalAmount));
                    break;
            }
            credited += journalAmount;
        }

        if (credited < purchase.GrandTotal)
        {
            var delta = purchase.GrandTotal - credited;
            lines.Add((payable.Id, 0m, delta));
            credited += delta;
        }
        else if (credited > purchase.GrandTotal)
        {
            var delta = credited - purchase.GrandTotal;
            lines.Add((payable.Id, delta, 0m));
        }

        BalanceLines(lines, payable.Id);

        await CreateEntryAsync(
            purchase.TenantId,
            purchase.BranchId,
            purchase.Date,
            $"REF:PURCHASE:{purchase.Id} Alış otomatik muhasebe kaydı",
            lines,
            ct);
    }

    public async Task RecordSaleAsync(
        Sale sale,
        IReadOnlyList<SaleItem> items,
        IReadOnlyList<SalePayment> payments,
        CancellationToken ct = default)
    {
        if (sale is null) return;
        if (await ExistsAsync(sale.TenantId, "SALE", sale.Id, ct)) return;

        var receivable = await EnsureAccountAsync(sale.TenantId, "1200", "Müşteri Alacakları", AccountType.Asset, true, ct);
        var inventory = await EnsureAccountAsync(sale.TenantId, "1100", "Stoklar", AccountType.Asset, true, ct);
        var cash = await EnsureAccountAsync(sale.TenantId, "1000", "Kasa", AccountType.Asset, true, ct);
        var bank = await EnsureAccountAsync(sale.TenantId, "1010", "Banka", AccountType.Asset, true, ct);

        var revenueTotal = Math.Round((items ?? []).Sum(x => x.LineTotal), 2, MidpointRounding.AwayFromZero);
        if (revenueTotal <= 0m) return;

        var lines = new List<(Guid accountId, decimal debit, decimal credit)>();

        var debited = 0m;
        foreach (var p in payments ?? [])
        {
            if (p.Amount <= 0m) continue;
            if (string.Equals(p.Method, "Takas", StringComparison.OrdinalIgnoreCase)) continue;
            var liquidity = await EnsureLiquidityAccountAsync(sale.TenantId, p.Currency, false, ct);
            if (string.Equals(p.Method, "Veresiye", StringComparison.OrdinalIgnoreCase))
                lines.Add((receivable.Id, p.Amount, 0m));
            else if (string.Equals(p.Method, "IBAN", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(p.Method, "Havale", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(p.Method, "Banka", StringComparison.OrdinalIgnoreCase))
                lines.Add((bank.Id, p.Amount, 0m));
            else
                lines.Add((liquidity.Id, p.Amount, 0m));
            debited += p.Amount;
        }

        if (debited < revenueTotal)
        {
            var delta = revenueTotal - debited;
            lines.Add((receivable.Id, delta, 0m));
            debited += delta;
        }
        else if (debited > revenueTotal)
        {
            var delta = debited - revenueTotal;
            lines.Add((receivable.Id, 0m, delta));
        }

        // İstenen akış: Satışta kredide envanter hesabı çalışsın.
        lines.Add((inventory.Id, 0m, revenueTotal));

        BalanceLines(lines, receivable.Id);

        await CreateEntryAsync(
            sale.TenantId,
            sale.BranchId,
            sale.CreatedAt,
            $"REF:SALE:{sale.Id} Satış otomatik muhasebe kaydı",
            lines,
            ct);
    }

    public async Task RecordManualCashTransactionAsync(
        CashTransaction tx,
        CashAccount account,
        CancellationToken ct = default)
    {
        if (tx is null || account is null) return;
        if (tx.Amount <= 0m) return;
        if (await ExistsAsync(tx.TenantId, "CASH", tx.Id, ct)) return;

        var cash = await EnsureAccountAsync(tx.TenantId, "1000", "Kasa", AccountType.Asset, true, ct);
        var bank = await EnsureAccountAsync(tx.TenantId, "1010", "Banka", AccountType.Asset, true, ct);
        var expense = await EnsureAccountAsync(tx.TenantId, "5000", "Giderler", AccountType.Expense, true, ct);
        var income = await EnsureAccountAsync(tx.TenantId, "4000", "Gelirler", AccountType.Income, true, ct);

        var isBank = string.Equals(account.AccountType, "PosBanka", StringComparison.OrdinalIgnoreCase);
        var liquidity = isBank
            ? bank
            : await EnsureLiquidityAccountAsync(tx.TenantId, tx.Currency, false, ct);
        var lines = new List<(Guid accountId, decimal debit, decimal credit)>();

        if (string.Equals(tx.TxType, "Expense", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add((expense.Id, tx.Amount, 0m));
            lines.Add((liquidity.Id, 0m, tx.Amount));
        }
        else
        {
            lines.Add((liquidity.Id, tx.Amount, 0m));
            lines.Add((income.Id, 0m, tx.Amount));
        }

        await CreateEntryAsync(
            tx.TenantId,
            tx.BranchId,
            tx.TxDate,
            $"REF:CASH:{tx.Id} Nakit işlem otomatik muhasebe kaydı",
            lines,
            ct);
    }

    public async Task EnsureCashOpeningFromAccountsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var equity = await EnsureAccountAsync(tenantId, "3000", "Açılış Özsermaye", AccountType.Equity, true, ct);
        var cashRows = await _db.CashAccounts.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Where(x =>
                ((x.AccountType ?? "").Trim().ToUpper() == "KASA") ||
                ((x.AccountType ?? "").Trim().ToUpper() == "VAULT") ||
                ((x.AccountType ?? "").Trim().ToUpper() == "POSBANKA"))
            .Select(x => new { x.BranchId, x.AccountType, x.Currency, x.CurrentBalance })
            .ToListAsync(ct);

        var expected = cashRows
            .GroupBy(x => new { x.BranchId, Code = LiquidityCode(x.Currency, string.Equals(x.AccountType, "PosBanka", StringComparison.OrdinalIgnoreCase)) })
            .Select(g => new { g.Key.BranchId, g.Key.Code, Amount = g.Sum(x => x.CurrentBalance) })
            .ToList();

        foreach (var row in expected)
        {
            var marker = $"REF:OPENING:CASH:{row.BranchId}:{row.Code}";
            var account = await EnsureLiquidityAccountByCodeAsync(tenantId, row.Code, ct);
            var currentJournalBalance = await _db.JournalLines.AsNoTracking()
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.BranchId == row.BranchId)
                .Join(
                    _db.Accounts.AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsDeleted && a.Code == row.Code),
                    l => l.AccountId,
                    a => a.Id,
                    (l, a) => l.Debit - l.Credit)
                .SumAsync(ct);

            var diff = Math.Round(row.Amount - currentJournalBalance, 2, MidpointRounding.AwayFromZero);
            if (diff == 0m) continue;

            var lines = new List<(Guid accountId, decimal debit, decimal credit)>();
            if (diff > 0m)
            {
                lines.Add((account.Id, diff, 0m));
                lines.Add((equity.Id, 0m, diff));
            }
            else
            {
                var abs = Math.Abs(diff);
                lines.Add((equity.Id, abs, 0m));
                lines.Add((account.Id, 0m, abs));
            }

            await CreateEntryAsync(
                tenantId,
                row.BranchId,
                DateTime.UtcNow,
                $"{marker} Mutabakat {DateTime.UtcNow:yyyyMMddHHmmss}",
                lines,
                ct);
        }
    }

    private async Task<Account> EnsureAccountAsync(
        Guid tenantId,
        string code,
        string name,
        AccountType type,
        bool isSystem,
        CancellationToken ct)
    {
        var row = await _db.Accounts.FirstOrDefaultAsync(x =>
            x.TenantId == tenantId &&
            !x.IsDeleted &&
            x.Code == code, ct);
        if (row is not null) return row;

        row = new Account
        {
            TenantId = tenantId,
            Code = code,
            Name = name,
            Type = type,
            IsSystemAccount = isSystem
        };
        _db.Accounts.Add(row);
        await _db.SaveChangesAsync(ct);
        return row;
    }

    private async Task<Account> EnsureLiquidityAccountAsync(Guid tenantId, string? currency, bool isBank, CancellationToken ct)
    {
        var code = LiquidityCode(currency, isBank);
        return await EnsureLiquidityAccountByCodeAsync(tenantId, code, ct);
    }

    private async Task<Account> EnsureLiquidityAccountByCodeAsync(Guid tenantId, string code, CancellationToken ct)
    {
        return code switch
        {
            "1010" => await EnsureAccountAsync(tenantId, "1010", "Banka TL", AccountType.Asset, true, ct),
            "1001" => await EnsureAccountAsync(tenantId, "1001", "Kasa USD", AccountType.Asset, true, ct),
            "1002" => await EnsureAccountAsync(tenantId, "1002", "Kasa EUR", AccountType.Asset, true, ct),
            "1003" => await EnsureAccountAsync(tenantId, "1003", "Kasa GBP", AccountType.Asset, true, ct),
            "1004" => await EnsureAccountAsync(tenantId, "1004", "Kasa HAS", AccountType.Asset, true, ct),
            "1005" => await EnsureAccountAsync(tenantId, "1005", "Kasa GUMUS", AccountType.Asset, true, ct),
            _ => await EnsureAccountAsync(tenantId, "1000", "Kasa TL", AccountType.Asset, true, ct)
        };
    }

    private static string LiquidityCode(string? currency, bool isBank)
    {
        if (isBank) return "1010";
        var c = NormalizeUnitCode(currency);
        return c switch
        {
            "USD" => "1001",
            "EUR" => "1002",
            "GBP" => "1003",
            "HAS" => "1004",
            "GUMUS" => "1005",
            _ => "1000"
        };
    }

    private static string NormalizeUnitCode(string? unit)
    {
        var c = (unit ?? "TL").Trim().ToUpperInvariant();
        return c switch
        {
            "TRY" => "TL",
            "EURO" => "EUR",
            "POUND" => "GBP",
            "GOLD" => "HAS",
            "GÜMÜŞ" => "GUMUS",
            _ => c
        };
    }

    private static decimal ResolvePurchaseJournalAmount(PurchasePayment p, string normalizedUnit)
    {
        // Dövizli nakitte (USD/EUR/GBP) alış defter satırı birim tutarıyla görünmelidir.
        if (p.PaymentType == PurchasePaymentType.Cash &&
            normalizedUnit is not "TL" &&
            p.UnitAmount.HasValue &&
            p.UnitAmount.Value > 0m)
            return p.UnitAmount.Value;

        return p.Amount;
    }

    private async Task<bool> ExistsAsync(Guid tenantId, string refType, Guid refId, CancellationToken ct)
    {
        var marker = $"REF:{refType}:{refId}";
        return await _db.JournalEntries.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted && x.Description.StartsWith(marker), ct);
    }

    private async Task CreateEntryAsync(
        Guid tenantId,
        Guid? branchId,
        DateTime date,
        string description,
        IReadOnlyList<(Guid accountId, decimal debit, decimal credit)> lines,
        CancellationToken ct)
    {
        var entry = new JournalEntry
        {
            TenantId = tenantId,
            BranchId = branchId,
            Date = date == default ? DateTime.UtcNow : date.ToUniversalTime(),
            Description = description
        };
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync(ct);

        foreach (var ln in lines)
        {
            if (ln.debit <= 0m && ln.credit <= 0m) continue;
            _db.JournalLines.Add(new JournalLine
            {
                TenantId = tenantId,
                BranchId = branchId,
                JournalEntryId = entry.Id,
                AccountId = ln.accountId,
                Debit = Math.Round(Math.Max(0m, ln.debit), 4, MidpointRounding.AwayFromZero),
                Credit = Math.Round(Math.Max(0m, ln.credit), 4, MidpointRounding.AwayFromZero)
            });
        }

        var debit = lines.Sum(x => x.debit);
        var credit = lines.Sum(x => x.credit);
        if (Math.Round(debit - credit, 2, MidpointRounding.AwayFromZero) != 0m)
            throw new InvalidOperationException("Yevmiye fişi dengelenemedi.");

        await _db.SaveChangesAsync(ct);
    }

    private static void BalanceLines(List<(Guid accountId, decimal debit, decimal credit)> lines, Guid balancingAccountId)
    {
        var debit = lines.Sum(x => x.debit);
        var credit = lines.Sum(x => x.credit);
        var diff = Math.Round(debit - credit, 2, MidpointRounding.AwayFromZero);
        if (diff == 0m) return;
        if (diff > 0m)
            lines.Add((balancingAccountId, 0m, diff));
        else
            lines.Add((balancingAccountId, Math.Abs(diff), 0m));
    }
}
