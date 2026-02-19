// Kuyumcu.PriceService/Models/PurchaseDtos.cs
namespace KUYUMCU.Price_Service.Models;

public record CreatePurchaseItemDto(
    string ProductCode,
    string ProductName,
    string Karat,
    decimal Quantity,
    decimal UnitPrice,
    decimal? TotalPrice // null gelirse Quantity*UnitPrice
);

public record CreatePurchaseDto(
    Guid BranchId,
    Guid? CustomerId,                // tedarikçi/müşteri (alım yapan/alan)
    List<CreatePurchaseItemDto> Items
);

public record UpdatePurchaseItemDto(
    Guid? Id,                        // varsa (mevcut satır) — yoksa yeni satır
    string ProductCode,
    string ProductName,
    string Karat,
    decimal Quantity,
    decimal UnitPrice,
    decimal? TotalPrice
);

public record UpdatePurchaseDto(
    Guid BranchId,
    Guid? CustomerId,
    List<UpdatePurchaseItemDto> Items
);

public record PurchaseItemDto(
    Guid Id,
    Guid PurchaseId,
    string ProductCode,
    string ProductName,
    string Karat,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice
);

public record PurchaseDto(
    Guid Id,
    Guid BranchId,
    Guid UserId,
    Guid? CustomerId,
    DateTime CreatedAt,
    List<PurchaseItemDto> Items
);
