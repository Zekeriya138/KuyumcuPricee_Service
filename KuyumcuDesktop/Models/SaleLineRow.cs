namespace KuyumcuDesktop.Models;

/// <summary>
/// Satış listesi satırı (grid ve API gönderimi için).
/// Toplam Tutar = (Gram * Ayar Kuru) + İşçilik Bedeli
/// </summary>
public class SaleLineRow
{
    public Guid ProductItemId { get; set; }
    public string ProductCode { get; set; } = "";
    public string Barcode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal Gram { get; set; }
    public string Ayar { get; set; } = "";  // "24K", "22K", "14K"
    public decimal AyarKuru { get; set; }    // o anki kur (Ask)
    public decimal IsciLik { get; set; }
    public decimal ToplamTutar => Math.Round(Gram * AyarKuru + IsciLik, 2);

    public void RecalcToplam() => _ = ToplamTutar; // property already computed
}
