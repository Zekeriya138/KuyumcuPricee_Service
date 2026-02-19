// Kuyumcu.PriceService/Models/SalesDtos.cs
public record CreateSaleDto(
    Guid BranchId,
    Guid? CustomerId,
    string ProductCode,
    string ProductName,
    string Karat,
    decimal Quantity,
    decimal UnitPrice,
    decimal? TotalPrice // İstersen null gelirse Quantity*UnitPrice hesaplarız
     
);

public record UpdateSaleDto(
    Guid BranchId,
    Guid? CustomerId,
    string ProductCode,
    string ProductName,
    string Karat,
    decimal Quantity,
    decimal UnitPrice,
    decimal? TotalPrice
);

public record SaleDto(
    Guid Id,
    Guid BranchId,
    Guid UserId,
    Guid? CustomerId,
    string ProductCode,
    string ProductName,
    string Karat,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    DateTime CreatedAt
);

