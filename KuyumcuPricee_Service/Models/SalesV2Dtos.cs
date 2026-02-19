// Kuyumcu.PriceService/Models/SalesV2Dtos.cs
using kuyumcu_domain.Entities;

public sealed record CreateSaleItemReq(
    int? LineNo,
    string ProductCode,         // zorunlu (stok/ürün eşleme)
    string ProductName,         // opsiyonel label
    string Karat,               // "24K"/"22K"/...
    string? Category,           // opsiyonel
    decimal Quantity,           // gram/adet
    decimal UnitPrice,
    decimal Discount,           // TL
    decimal TaxRate,            // 0.00..1.00
    Guid? ProductItemId         // barkodlu tekil parça satılıyorsa (ProductItems.Id)
);

public sealed record CreateSaleReqV2(
    Guid BranchId,
    Guid? CustomerId,
    List<CreateSaleItemReq> Items
);

public sealed record UpdateSaleItemReq(
    int? LineNo,
    string ProductCode,
    string ProductName,
    string Karat,
    string? Category,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal TaxRate,
    Guid? ProductItemId
);

public sealed record UpdateSaleReqV2(
    Guid BranchId,
    Guid? CustomerId,
    List<UpdateSaleItemReq> Items
);

public sealed record SaleItemDtoV2(
    Guid Id,
    Guid SaleId,
    int LineNo,
    string ProductCode,
    string ProductName,
    string Karat,
    string? Category,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal TaxRate,
    decimal LineTotal,
    Guid? ProductItemId
);

public sealed record SaleDtoV2(
    Guid Id,
    Guid BranchId,
    Guid UserId,
    Guid? CustomerId,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    DateTime CreatedAt,
    List<SaleItemDtoV2> Items
);
