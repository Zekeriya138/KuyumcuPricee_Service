namespace kuyumcu_domain.Enums;

/// <summary>Tedarikçi türü - kuyumculuk sektörüne özel.</summary>
public enum SupplierType
{
    Wholesaler = 0,      // Toptancı
    Atelier = 1,         // Atölye
    ScrapGold = 2,       // Hurda altın tedarikçisi
    MintProducts = 3,    // Darphane ürünleri (çeyrek, yarım, tam)
    DiamondSupplier = 4, // Elmas tedarikçisi
    Consignment = 5      // Emanet tedarikçi
}
