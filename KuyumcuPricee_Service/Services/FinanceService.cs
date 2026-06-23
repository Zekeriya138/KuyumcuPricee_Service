using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public interface IFinanceService
{
    Task ApplySalePaymentsAsync(Guid tenantId, Guid branchId, Guid saleId, IEnumerable<SalePayment> payments, CancellationToken ct);
    Task ApplyPurchasePaymentsAsync(Guid tenantId, Guid branchId, Guid purchaseId, IEnumerable<PurchasePayment> payments, CancellationToken ct);
    /// <summary>Döviz alışında satın alınan döviz miktarının tamamını vault'a yazar.</summary>
    Task ApplyForexPurchaseVaultAsync(Guid tenantId, Guid branchId, Guid purchaseId, IReadOnlyList<PurchaseItem> items, CancellationToken ct);
}

public sealed class FinanceService : IFinanceService
{
    private readonly AppDbContext _db;

    public FinanceService(AppDbContext db)
    {
        _db = db;
    }

    public async Task ApplySalePaymentsAsync(Guid tenantId, Guid branchId, Guid saleId, IEnumerable<SalePayment> payments, CancellationToken ct)
    {
        foreach (var pay in payments.Where(x => !x.IsDeleted && x.Amount > 0))
        {
            if (string.Equals(pay.Method, "TedarikciVeresiye", StringComparison.OrdinalIgnoreCase))
                continue;
            // Veresiye tahsilat değildir; kasa/banka/vault bakiyesini etkilememeli.
            if (string.Equals(pay.Method, "Veresiye", StringComparison.OrdinalIgnoreCase))
                continue;
            // Takas, hurda akışıdır; kasa/gelir-gider defterine likidite hareketi yazılmaz.
            if (string.Equals(pay.Method, "Takas", StringComparison.OrdinalIgnoreCase))
                continue;

            var kind = ResolveSaleAccount(pay);
            var account = await GetOrCreateAccountAsync(tenantId, branchId, kind.accountType, kind.currency, kind.name, ct);

            account.CurrentBalance += pay.Amount;
            _db.CashTransactions.Add(new CashTransaction
            {
                TenantId = tenantId,
                BranchId = branchId,
                CashAccountId = account.Id,
                TxType = "Income",
                SourceModule = "Sale",
                Currency = kind.currency,
                Amount = pay.Amount,
                TxDate = DateTime.UtcNow,
                RefType = "SALE",
                RefId = saleId,
                Description = $"Satis odeme: {pay.Method}"
            });
        }
    }

    public async Task ApplyPurchasePaymentsAsync(Guid tenantId, Guid branchId, Guid purchaseId, IEnumerable<PurchasePayment> payments, CancellationToken ct)
    {
        foreach (var pay in payments.Where(x => !x.IsDeleted && x.Amount > 0))
        {
            // Veresiye/Cari kasa çıkışı değildir; yalnızca tedarikçi finans bakiyesine yansır.
            if (pay.PaymentType == PurchasePaymentType.Credit)
                continue;

            if (pay.PaymentType == PurchasePaymentType.Takas)
                continue;

            var kind = ResolvePurchaseAccount(pay);
            var ledgerAmount = ResolvePurchaseLedgerAmount(pay, kind.currency);
            if (ledgerAmount <= 0m)
                continue;
            var account = await GetOrCreateAccountAsync(tenantId, branchId, kind.accountType, kind.currency, kind.name, ct);

            account.CurrentBalance -= ledgerAmount;
            _db.CashTransactions.Add(new CashTransaction
            {
                TenantId = tenantId,
                BranchId = branchId,
                CashAccountId = account.Id,
                TxType = "Expense",
                SourceModule = "Purchase",
                Currency = kind.currency,
                Amount = ledgerAmount,
                TxDate = DateTime.UtcNow,
                RefType = "PURCHASE",
                RefId = purchaseId,
                Description = $"Alis odeme: {pay.PaymentType}"
            });
        }
    }

    public async Task ApplyForexPurchaseVaultAsync(
        Guid tenantId,
        Guid branchId,
        Guid purchaseId,
        IReadOnlyList<PurchaseItem> items,
        CancellationToken ct)
    {
        if (items == null || items.Count == 0) return;
        var forex = items.Where(x => x.Kind == ItemKind.Forex && x.Quantity != 0).ToList();
        if (forex.Count == 0) return;

        foreach (var it in forex)
        {
            var cur = NormalizeForexPurchaseCurrency(it.Karat);
            if (cur is not ("USD" or "EUR" or "GBP" or "GUMUS")) continue;
            var qty = Math.Abs(it.Quantity);
            if (qty <= 0m) continue;

            var account = await GetOrCreateVaultAccountAsync(tenantId, branchId, cur, ct);
            account.CurrentBalance += qty;
            _db.CashTransactions.Add(new CashTransaction
            {
                TenantId = tenantId,
                BranchId = branchId,
                CashAccountId = account.Id,
                TxType = "Income",
                SourceModule = "Purchase",
                Currency = cur,
                Amount = qty,
                TxDate = DateTime.UtcNow,
                RefType = "PURCHASE",
                RefId = purchaseId,
                Description = $"Doviz alisi kasa girisi: {cur}"
            });
        }
    }

    private async Task<CashAccount> GetOrCreateVaultAccountAsync(Guid tenantId, Guid branchId, string currency, CancellationToken ct)
    {
        var cur = NormalizeForexPurchaseCurrency(currency);
        var name = $"Vault {cur}";
        var acc = await _db.CashAccounts
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.AccountType == "Vault" &&
                x.Currency == cur &&
                x.Name == name &&
                !x.IsDeleted, ct);
        if (acc is not null) return acc;

        acc = new CashAccount
        {
            TenantId = tenantId,
            BranchId = branchId,
            AccountType = "Vault",
            Currency = cur,
            Name = name,
            CurrentBalance = 0m
        };
        _db.CashAccounts.Add(acc);
        return acc;
    }

    private static string NormalizeForexPurchaseCurrency(string? raw)
    {
        var c = (raw ?? "").Trim().ToUpperInvariant();
        return c switch
        {
            "EURO" => "EUR",
            "£" => "GBP",
            _ => c
        };
    }

    private async Task<CashAccount> GetOrCreateAccountAsync(Guid tenantId, Guid branchId, string accountType, string currency, string name, CancellationToken ct)
    {
        var acc = await _db.CashAccounts
            .FirstOrDefaultAsync(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.AccountType == accountType &&
                x.Currency == currency &&
                x.Name == name &&
                !x.IsDeleted, ct);
        if (acc != null) return acc;

        acc = new CashAccount
        {
            TenantId = tenantId,
            BranchId = branchId,
            AccountType = accountType,
            Currency = currency,
            Name = name,
            CurrentBalance = 0m
        };
        _db.CashAccounts.Add(acc);
        return acc;
    }

    private static (string accountType, string currency, string name) ResolveSaleAccount(SalePayment pay)
    {
        var m = (pay.Method ?? "").Trim().ToUpperInvariant();
        var currency = NormalizeCurrency(pay.Currency);
        return m switch
        {
            "NAKIT" => ("Kasa", "TL", "Kasa TL"),
            "KART" => ("PosBanka", "TL", "POS"),
            "IBAN" => ("PosBanka", "TL", "Banka"),
            "USD" => ("Vault", "USD", "Vault USD"),
            "EURO" or "EUR" => ("Vault", "EUR", "Vault EUR"),
            "GBP" or "POUND" => ("Vault", "GBP", "Vault GBP"),
            "VERESIYE" => ("Alacak", currency, $"Veresiye {currency}"),
            "TAKAS" => ("Takas", "TL", "Takas"),
            _ => ("Kasa", currency, $"Kasa {currency}")
        };
    }

    private static (string accountType, string currency, string name) ResolvePurchaseAccount(PurchasePayment pay)
    {
        var unit = NormalizeCurrency(pay.UnitCode);
        return pay.PaymentType switch
        {
            PurchasePaymentType.Cash => unit switch
            {
                "USD" => ("Kasa", "USD", "Kasa USD"),
                "EUR" => ("Kasa", "EUR", "Kasa EUR"),
                "GBP" => ("Kasa", "GBP", "Kasa GBP"),
                _ => ("Kasa", "TL", "Kasa TL")
            },
            PurchasePaymentType.Bank => ("PosBanka", "TL", "Banka"),
            PurchasePaymentType.Credit => ("Alacak", unit, $"Veresiye {unit}"),
            PurchasePaymentType.Gold => ("Vault", "HAS", "Vault HAS"),
            PurchasePaymentType.Takas => ("Takas", "TL", "Takas"),
            _ => ("Kasa", "TL", "Kasa TL")
        };
    }

    private static decimal ResolvePurchaseLedgerAmount(PurchasePayment pay, string accountCurrency)
    {
        var currency = NormalizeCurrency(accountCurrency);
        if (pay.PaymentType == PurchasePaymentType.Cash && currency != "TL")
        {
            if (pay.UnitAmount.HasValue && pay.UnitAmount.Value > 0m)
                return pay.UnitAmount.Value;
            return 0m;
        }

        return pay.Amount > 0m ? pay.Amount : 0m;
    }

    private static string NormalizeCurrency(string? unit)
    {
        var u = (unit ?? "TL").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "EURO" => "EUR",
            "GOLD" => "HAS",
            "POUND" => "GBP",
            _ => u
        };
    }
}

