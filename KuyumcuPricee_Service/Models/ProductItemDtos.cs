// Kuyumcu.PriceService/Models/ProductItemDtos.cs
using System;

public sealed record CreateProductItemDto(
    Guid BranchId,
    string ProductCode,   // ProductId yerine ProductCode istiyoruz (mevcut düzene uygun)
    string? Serial,
    string? Barcode,
    string Karat,
    decimal Weight,
    decimal Cost
);

public sealed record UpdateProductItemDto(
    Guid BranchId,
    string Karat,
    decimal Weight,
    bool IsInStock,
    string? Serial,
    string? Barcode,
    decimal Cost
   
);

public sealed record ProductItemDto(
    Guid Id,
    Guid BranchId,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    string Category,
    string? Olcu,
    string? Serial,
    string? Barcode,
    string Karat,
    decimal Weight,
    decimal Cost,
    bool IsInStock,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    string? MalTanim = null,
    decimal? BelirlenenSatisFiyatiHas = null,
    decimal? BirimSatisIscilikHas = null
);
public sealed class PagedResult<T>
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<T> Items { get; set; } = new();
}
