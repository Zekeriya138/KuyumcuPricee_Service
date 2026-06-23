namespace kuyumcu_domain.Enums;

/// <summary>Tedarikçi fiyatlandırma tipi - kuyumculuk sektörüne özel.</summary>
public enum SupplierPricingType
{
    GramPlusLabor = 0,   // Gram + İşçilik
    FixedUnitPrice = 1,  // Sabit birim fiyat
    LaborOnly = 2,       // Sadece işçilik
    Consignment = 3      // Emanet (komisyon)
}
