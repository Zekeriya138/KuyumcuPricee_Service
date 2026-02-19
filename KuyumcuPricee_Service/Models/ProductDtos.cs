namespace KUYUMCU.Price_Service.Models
{
    public record CreateProductDto(
        string ProductCode,
        string Name,
        string? Category,
        string? Karat,
        decimal? WeightGr,
        string? Barcode
    );

    public record UpdateProductDto(
        string Name,
        string? Category,
        string? Karat,
        decimal? WeightGr,
        string? Barcode
    );

    public record ProductDto(
        Guid Id,
        string ProductCode,
        string Name,
        string? Category,
        string? Karat,
        decimal? WeightGr,
        string? Barcode,
        DateTime CreatedAt
    );
}
