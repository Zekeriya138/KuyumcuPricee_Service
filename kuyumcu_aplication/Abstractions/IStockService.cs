// kuyumcu_application/Abstractions/IStockService.cs
using kuyumcu_domain.Enums;

namespace kuyumcu_application.Abstractions
{
    public interface IStockService
    {
        // YALNIZCA KAPSAMLI İMZA KORUNDU:
        Task AdjustAsync(
            Guid branchId,
            Guid productId,
            Guid? productItemId,  // << Tekil parçaya bağlantı
            decimal deltaQuantity,  // + giriş, - çıkış
            StockRefKind refKind,
            Guid refId,
            string? note,
            CancellationToken ct = default);
    }
    //public interface IStockService
    //{
    //    Task AdjustAsync(
    //        Guid branchId,          // <— eklendi
    //        Guid productId,
    //        decimal deltaQuantity,  // + giriş, - çıkış
    //        StockRefKind refKind,
    //        Guid refId,
    //        string? note,
    //        CancellationToken ct = default);

    //    // YENİ: hareketi ProductItem ile bağlamak için
    //    Task AdjustAsync(Guid branchId, Guid productId, Guid? productItemId, decimal deltaQuantity,
    //                     StockRefKind refKind, Guid refId, string? note,
    //                     CancellationToken ct = default);

    //}
}
