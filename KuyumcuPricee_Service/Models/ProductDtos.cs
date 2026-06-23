namespace KUYUMCU.Price_Service.Models
{
    /// <summary>0 = Tekil, 1 = Ziynet (adetli). UI'da 1=Tekil, 2=Ziynet olarak gösterilebilir.</summary>
    public record CreateProductDto(
        string ProductCode,
        string Name,
        string? Category,
        string? Karat,
        decimal? WeightGr,
        decimal? Cost,
        string? Barcode,
        string? Olcu,
        int InventoryType = 0,
        int StokMiktari = 0,
        string? ZiynetTipi = null,
        bool IsSpecialProduct = false,
        string? MalTanim = null,
        string? DepoTedarikciFirma = null,
        decimal? BelirlenenSatisFiyatiHas = null,
        decimal? BirimSatisIscilikHas = null,
        decimal? DepoBirimMaliyet = null,
        /// <summary>Tekil ürün: depo hareketi ürün kaydı ile aynı transaction’da; null ise depo ayrı çağrıdan.</summary>
        Guid? DepoBranchId = null
    );

    public record UpdateProductDto(
        string Name,
        string? Category,
        string? Karat,
        decimal? WeightGr,
        decimal? Cost,
        string? Barcode,
        string? Olcu,
        int? InventoryType = null,
        int? StokMiktari = null,
        string? ZiynetTipi = null,
        bool? IsSpecialProduct = null,
        string? MalTanim = null,
        string? DepoTedarikciFirma = null,
        decimal? BelirlenenSatisFiyatiHas = null,
        decimal? BirimSatisIscilikHas = null,
        decimal? DepoBirimMaliyet = null
    );

    public record ProductDto(
        Guid Id,
        string ProductCode,
        string Name,
        string? Category,
        string? Karat,
        decimal? WeightGr,
        decimal Cost,
        string? Barcode,
        string? Olcu,
        DateTime CreatedAt,
        int InventoryType = 0,
        int StokMiktari = 0,
        string? ZiynetTipi = null,
        bool IsSpecialProduct = false,
        string? MalTanim = null,
        string? DepoTedarikciFirma = null,
        decimal? BelirlenenSatisFiyatiHas = null,
        decimal? BirimSatisIscilikHas = null,
        decimal? DepoBirimMaliyet = null
    );
}
