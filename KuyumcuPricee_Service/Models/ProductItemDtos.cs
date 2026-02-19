// Kuyumcu.PriceService/Models/ProductItemDtos.cs
using System;

public sealed record CreateProductItemDto(
    Guid BranchId,
    string ProductCode,   // ProductId yerine ProductCode istiyoruz (mevcut düzene uygun)
    string? Serial,
    string? Barcode,
    string Karat,
    decimal Weight
);

public sealed record UpdateProductItemDto(
    Guid BranchId,
    string Karat,
    decimal Weight,
    bool IsInStock,
    string? Serial,
    string? Barcode
);

public sealed record ProductItemDto(
    Guid Id,
    Guid BranchId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string? Serial,
    string? Barcode,
    string Karat,
    decimal Weight,
    bool IsInStock,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);
