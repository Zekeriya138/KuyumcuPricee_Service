using KUYUMCU.Price_Service.Models;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public sealed class BalanceSheetService : IBalanceSheetService
{
    private readonly AppDbContext _db;
    private readonly IAccountingJournalService _accounting;
    private sealed record BalanceAggRow(Guid? BranchId, string Code, string Name, AccountType Type, decimal Balance);

    public BalanceSheetService(AppDbContext db, IAccountingJournalService accounting)
    {
        _db = db;
        _accounting = accounting;
    }

    public Task<BalanceSheetDto> GetCompanyBalanceSheetAsync(Guid tenantId, CancellationToken ct = default)
        => BuildAsync(tenantId, branchId: null, scope: "Company", includeBranchBreakdown: false, ct);

    public Task<BalanceSheetDto> GetBranchBalanceSheetAsync(Guid tenantId, Guid branchId, CancellationToken ct = default)
        => BuildAsync(tenantId, branchId, scope: "Branch", includeBranchBreakdown: false, ct);

    public Task<BalanceSheetDto> GetConsolidatedBalanceSheetAsync(Guid tenantId, CancellationToken ct = default)
        => BuildAsync(tenantId, branchId: null, scope: "Consolidated", includeBranchBreakdown: true, ct);

    private async Task<BalanceSheetDto> BuildAsync(
        Guid tenantId,
        Guid? branchId,
        string scope,
        bool includeBranchBreakdown,
        CancellationToken ct)
    {
        await EnsureHistoricalBackfillAsync(tenantId, ct);

        var linesQ = _db.JournalLines.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            linesQ = linesQ.Where(x => x.BranchId == branchId.Value);

        var grouped = await linesQ
            .Join(
                _db.Accounts.AsNoTracking().Where(x => x.TenantId == tenantId && !x.IsDeleted),
                l => l.AccountId,
                a => a.Id,
                (l, a) => new
                {
                    l.BranchId,
                    a.Code,
                    a.Name,
                    a.Type,
                    Balance = l.Debit - l.Credit
                })
            .GroupBy(x => new { x.BranchId, x.Code, x.Name, x.Type })
            .Select(g => new BalanceAggRow(
                g.Key.BranchId,
                g.Key.Code,
                g.Key.Name,
                g.Key.Type,
                g.Sum(x => x.Balance)))
            .ToListAsync(ct);

        var incomeNet = grouped
            .Where(x => x.Type == AccountType.Income || x.Type == AccountType.Expense)
            .Sum(x => x.Type == AccountType.Income ? -x.Balance : x.Balance);

        var accountRows = grouped
            .Where(x => x.Type == AccountType.Asset || x.Type == AccountType.Liability || x.Type == AccountType.Equity)
            .Select(x => new BalanceSheetAccountLineDto
            {
                Group = x.Type switch
                {
                    AccountType.Asset => "Assets",
                    AccountType.Liability => "Liabilities",
                    _ => "Equity"
                },
                AccountCode = x.Code,
                AccountName = x.Name,
                Balance = RoundForType(x.Balance, x.Type)
            })
            .OrderBy(x => x.Group)
            .ThenBy(x => x.AccountCode)
            .ToList();

        var totalAssets = accountRows.Where(x => x.Group == "Assets").Sum(x => x.Balance);
        var totalLiabilities = accountRows.Where(x => x.Group == "Liabilities").Sum(x => x.Balance);
        var totalEquityRaw = accountRows.Where(x => x.Group == "Equity").Sum(x => x.Balance);
        var totalEquity = totalEquityRaw + incomeNet;

        // Temel muhasebe denklemi tutarlılığı için küçük yuvarlama farkını özkaynağa yaz.
        var diff = Math.Round(totalAssets - (totalLiabilities + totalEquity), 2, MidpointRounding.AwayFromZero);
        if (diff != 0m)
            totalEquity += diff;

        var branchName = "";
        if (branchId.HasValue && branchId.Value != Guid.Empty)
        {
            branchName = await _db.Branches.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Id == branchId.Value)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(ct) ?? "";
        }

        var dto = new BalanceSheetDto
        {
            Scope = scope,
            BranchId = branchId,
            BranchName = branchName,
            GeneratedAt = DateTime.UtcNow,
            TotalAssets = Math.Round(totalAssets, 2, MidpointRounding.AwayFromZero),
            TotalLiabilities = Math.Round(totalLiabilities, 2, MidpointRounding.AwayFromZero),
            TotalEquity = Math.Round(totalEquity, 2, MidpointRounding.AwayFromZero),
            Difference = 0m,
            Accounts = accountRows
        };

        dto.Trend = BuildTrend(grouped);
        if (includeBranchBreakdown)
            dto.BranchSummaries = await BuildBranchBreakdownAsync(tenantId, grouped, ct);
        return dto;
    }

    private async Task EnsureHistoricalBackfillAsync(Guid tenantId, CancellationToken ct)
    {
        var hasAny = await _db.JournalEntries.AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && !x.IsDeleted, ct);
        if (hasAny) return;

        var salesBaseQ = _db.Sales.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.TenantId, x.BranchId, x.CreatedAt });
        var saleItems = await _db.SaleItems.AsNoTracking()
            .Join(
                salesBaseQ,
                i => i.SaleId,
                s => s.Id,
                (i, s) => new { s.Id, Item = i })
            .ToListAsync(ct);
        var salePayments = await _db.SalePayments.AsNoTracking()
            .Join(
                salesBaseQ,
                p => p.SaleId,
                s => s.Id,
                (p, s) => new { s.Id, Payment = p })
            .ToListAsync(ct);
        var sales = await salesBaseQ.ToListAsync(ct);
        foreach (var s in sales)
        {
            var sale = new Sale
            {
                Id = s.Id,
                TenantId = s.TenantId,
                BranchId = s.BranchId,
                CreatedAt = s.CreatedAt
            };
            await _accounting.RecordSaleAsync(
                sale,
                saleItems.Where(x => x.Id == s.Id).Select(x => x.Item).ToList(),
                salePayments.Where(x => x.Id == s.Id).Select(x => x.Payment).ToList(),
                ct);
        }

        var purchaseBaseQ = _db.Purchases.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .Select(x => new { x.Id, x.TenantId, x.BranchId, x.Date, x.GrandTotal });
        var purchasePayments = await _db.PurchasePayments.AsNoTracking()
            .Join(
                purchaseBaseQ,
                p => p.PurchaseId,
                b => b.Id,
                (p, b) => new { b.Id, Payment = p })
            .ToListAsync(ct);
        var purchases = await purchaseBaseQ.ToListAsync(ct);
        foreach (var p in purchases)
        {
            var purchase = new Purchase
            {
                Id = p.Id,
                TenantId = p.TenantId,
                BranchId = p.BranchId,
                Date = p.Date,
                GrandTotal = p.GrandTotal
            };
            await _accounting.RecordPurchaseAsync(
                purchase,
                purchasePayments.Where(x => x.Id == p.Id).Select(x => x.Payment).ToList(),
                ct);
        }

        var cashRows = await _db.CashTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Join(
                _db.CashAccounts.AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsDeleted),
                tx => tx.CashAccountId,
                acc => acc.Id,
                (tx, acc) => new { Tx = tx, Acc = acc })
            .ToListAsync(ct);
        foreach (var r in cashRows)
            await _accounting.RecordManualCashTransactionAsync(r.Tx, r.Acc, ct);

        await _accounting.EnsureCashOpeningFromAccountsAsync(tenantId, ct);
    }

    private static List<BalanceSheetTrendPointDto> BuildTrend(IReadOnlyList<BalanceAggRow> grouped)
    {
        // İlk sürümde entry bazlı trend yerine mevcut bilançonun snapshot trendini üretir.
        // Journal veri büyüdükçe aylık historical trend endpoint ile genişletilebilir.
        var assets = grouped
            .Where(x => x.Type == AccountType.Asset)
            .Sum(x => RoundForType(x.Balance, AccountType.Asset));
        var liabilities = grouped
            .Where(x => x.Type == AccountType.Liability)
            .Sum(x => RoundForType(x.Balance, AccountType.Liability));
        var equity = grouped
            .Where(x => x.Type == AccountType.Equity)
            .Sum(x => RoundForType(x.Balance, AccountType.Equity));

        return new List<BalanceSheetTrendPointDto>
        {
            new()
            {
                Period = DateTime.UtcNow.ToString("yyyy-MM"),
                Assets = Math.Round(assets, 2, MidpointRounding.AwayFromZero),
                Liabilities = Math.Round(liabilities, 2, MidpointRounding.AwayFromZero),
                Equity = Math.Round(equity, 2, MidpointRounding.AwayFromZero)
            }
        };
    }

    private async Task<List<BalanceSheetBranchSummaryDto>> BuildBranchBreakdownAsync(
        Guid tenantId,
        IReadOnlyList<BalanceAggRow> grouped,
        CancellationToken ct)
    {
        var names = await _db.Branches.AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted)
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        return grouped
            .GroupBy(x => (Guid?)x.BranchId)
            .Select(g =>
            {
                var assets = g.Where(x => x.Type == AccountType.Asset).Sum(x => RoundForType(x.Balance, AccountType.Asset));
                var liabilities = g.Where(x => x.Type == AccountType.Liability).Sum(x => RoundForType(x.Balance, AccountType.Liability));
                var equity = g.Where(x => x.Type == AccountType.Equity).Sum(x => RoundForType(x.Balance, AccountType.Equity));

                return new BalanceSheetBranchSummaryDto
                {
                    BranchId = g.Key,
                    BranchName = g.Key.HasValue && names.TryGetValue(g.Key.Value, out var n) ? n : "Merkez/Genel",
                    Assets = Math.Round(assets, 2, MidpointRounding.AwayFromZero),
                    Liabilities = Math.Round(liabilities, 2, MidpointRounding.AwayFromZero),
                    Equity = Math.Round(equity, 2, MidpointRounding.AwayFromZero)
                };
            })
            .OrderByDescending(x => x.Assets)
            .ToList();
    }

    private static decimal RoundForType(decimal rawBalance, AccountType type)
    {
        var v = type switch
        {
            AccountType.Asset => rawBalance,
            AccountType.Liability => -rawBalance,
            AccountType.Equity => -rawBalance,
            _ => rawBalance
        };
        return Math.Round(Math.Max(0m, v), 2, MidpointRounding.AwayFromZero);
    }
}
