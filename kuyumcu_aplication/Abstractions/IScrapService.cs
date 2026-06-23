using kuyumcu_domain.Entities;

namespace kuyumcu_application.Abstractions;

public sealed record ScrapStockLineDto(string Karat, decimal WeightGram, decimal PureGoldGram);

public sealed record ScrapDashboardDto(
    decimal TotalWeightGram,
    decimal TotalPureGoldGram,
    IReadOnlyList<ScrapStockLineDto> ByKarat);

/// <summary>Hurda stok: müşteri alışı, tedarikçi ödemesinde hurda çıkışı, rafine, ürüne çevirme.</summary>
public interface IScrapService
{
    Task<ScrapDashboardDto> GetDashboardAsync(Guid tenantId, Guid branchId, CancellationToken ct = default);

    /// <summary>Alış faturasındaki ItemKind.Scrap satırlarını hurda stoğa işler.</summary>
    Task AddFromPurchaseItemsAsync(Guid tenantId, Purchase purchase, CancellationToken ct = default);

    /// <summary>Tedarikçi alışında hurda ile ödeme: stoktan düşer.</summary>
    Task<(bool ok, string? error)> TryConsumeForGoldPaymentAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        Guid? purchaseId,
        CancellationToken ct = default);

    /// <summary>Müşteriden tek satır hurda alışı (Purchase + hurda stoğu).</summary>
    Task<(bool ok, Guid? purchaseId, string? error)> RecordCustomerScrapPurchaseAsync(
        Guid tenantId,
        Guid branchId,
        Guid userId,
        Guid customerId,
        string karatRaw,
        decimal weightGram,
        decimal goldPricePerGram,
        int paymentMethod,
        string? note,
        CancellationToken ct = default);

    /// <summary>Hurdadan ürün: hurda düşer, tekil stok artar.</summary>
    Task<(bool ok, string? error)> RefineToProductItemsAsync(
        Guid tenantId,
        Guid branchId,
        Guid userId,
        string fromKaratRaw,
        decimal scrapWeightGram,
        Guid targetProductId,
        int itemCount,
        string outputKaratRaw,
        decimal? goldPricePerGram,
        string? note,
        CancellationToken ct = default);

    /// <summary>Genel hurda düşümü (manuel kullanım / fire vb.).</summary>
    Task<(bool ok, string? error)> TryManualUseAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        string? note,
        CancellationToken ct = default);

    /// <summary>Hurda alış satırından vitrine barkodlama: ScrapStock düşümü (ConvertToProduct).</summary>
    Task<(bool ok, string? error)> TrySubtractScrapForPurchaseLineBarcodeAsync(
        Guid tenantId,
        Guid branchId,
        string karatRaw,
        decimal weightGram,
        Guid purchaseId,
        string? note,
        CancellationToken ct = default);
}
