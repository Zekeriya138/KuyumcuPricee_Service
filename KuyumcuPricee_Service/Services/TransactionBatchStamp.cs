using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

internal static class TransactionBatchStamp
{
    public static void Stamp(CustomerTransaction tx, Guid batchId)
    {
        tx.BatchId = batchId;
    }

    public static void Stamp(SupplierTransaction tx, Guid batchId)
    {
        tx.BatchId = batchId;
    }

    public static void Stamp(CashTransaction tx, Guid batchId)
    {
        tx.BatchId = batchId;
    }
}
