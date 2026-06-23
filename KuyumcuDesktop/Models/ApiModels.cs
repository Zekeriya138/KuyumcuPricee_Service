using System.Text.Json;

namespace KuyumcuDesktop.Models;

public class ProductItemDto
{
    public Guid Id { get; set; }
    public Guid BranchId { get; set; }
    public Guid ProductId { get; set; }
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string? Category { get; set; }
    public string? Serial { get; set; }
    public string? Barcode { get; set; }
    public string Karat { get; set; } = "";
    public decimal Weight { get; set; }
    public decimal Cost { get; set; }
    public bool IsInStock { get; set; }
}

public class QuoteDto
{
    public string Code { get; set; } = "";
    public string Display { get; set; } = "";
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
}

public class CustomerDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public class CreateSaleItemReq
{
    public int? LineNo { get; set; }
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Karat { get; set; } = "";
    public string? Category { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal TaxRate { get; set; }
    public Guid? ProductItemId { get; set; }
}

public class CreateSaleReqV2
{
    public Guid BranchId { get; set; }
    public Guid? CustomerId { get; set; }
    public List<CreateSaleItemReq> Items { get; set; } = new();
}
