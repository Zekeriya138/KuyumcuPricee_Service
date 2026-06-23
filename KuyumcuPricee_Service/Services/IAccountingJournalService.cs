using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

public interface IAccountingJournalService
{
    Task RecordPurchaseAsync(
        Purchase purchase,
        IReadOnlyList<PurchasePayment> payments,
        CancellationToken ct = default);

    Task RecordSaleAsync(
        Sale sale,
        IReadOnlyList<SaleItem> items,
        IReadOnlyList<SalePayment> payments,
        CancellationToken ct = default);

    Task RecordManualCashTransactionAsync(
        CashTransaction tx,
        CashAccount account,
        CancellationToken ct = default);

    Task EnsureCashOpeningFromAccountsAsync(
        Guid tenantId,
        CancellationToken ct = default);
}
