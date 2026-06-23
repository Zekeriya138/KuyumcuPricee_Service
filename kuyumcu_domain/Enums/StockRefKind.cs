namespace kuyumcu_domain.Enums
{
    public enum StockRefKind
    {
        Unknown = 0,
        Purchase = 1,   // alış
        Sale = 2,   // satış
        Adjustment = 3,  // elle düzeltme vb.
        ScrapPurchase = 10,
        ScrapPayment = 11,
        ScrapRefine = 12,
        ScrapManufacturing = 13
    }
}
