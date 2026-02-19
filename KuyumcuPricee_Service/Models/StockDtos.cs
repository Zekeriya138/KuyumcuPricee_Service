namespace Kuyumcu.PriceService.Models;

// Liste satırı
public record StockDto(
    Guid ProductId,
    string ProductCode,
    string ProductName,
    decimal Quantity,
    DateTime UpdatedAt
);

// Hareket satırı
public record StockMovementDto(
    Guid Id,
    Guid ProductId,
    string ProductCode,
    string ProductName,
    DateTime Date,
    decimal InQty,
    decimal OutQty,
    string RefKind,   // Purchase, Sale, SaleUpdate, Delete v.b.
    Guid? RefId,
    string? Note
);
