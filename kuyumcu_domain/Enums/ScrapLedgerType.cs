namespace kuyumcu_domain.Enums;

/// <summary>Hurda stok hareket türü (audit).</summary>
public enum ScrapLedgerType
{
    CustomerPurchase = 1,
    SupplierPaymentOut = 2,
    RefineOut = 3,
    ConvertToProductOut = 4,
    AdjustmentIn = 5,
    AdjustmentOut = 6,
    /// <summary>Manuel / diğer çıkışlar (api/scrap/use).</summary>
    ManualUseOut = 7
}
