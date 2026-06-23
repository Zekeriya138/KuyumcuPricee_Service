using kuyumcu_domain.Entities;

namespace KUYUMCU.Price_Service.Services;

public interface IUblInvoiceBuilder
{
    Task<UblBuildResult> BuildOutgoingAsync(
        Invoice invoice,
        Customer? customer,
        EInvoiceProfile? profile,
        string invoiceNumber,
        string documentType,
        CancellationToken ct);

    Task<UblBuildResult> BuildOutgoingFromDraftAsync(
        Invoice invoice,
        EInvoiceProfile? profile,
        string invoiceNumber,
        ManualEInvoiceDraft draft,
        CancellationToken ct);
}

public sealed record UblBuildResult(
    string UblXml,
    string UblBase64,
    string SellerTaxNumber,
    string? SellerAlias,
    string BuyerName,
    string BuyerTaxNumber,
    string? BuyerAlias
);

public sealed record ManualEInvoiceDraft(
    string DocumentType,
    string BuyerName,
    string BuyerTaxNumber,
    string? BuyerAddress,
    string? BuyerCity,
    string? BuyerDistrict,
    string? BuyerPostalCode,
    string? IssueDateText,
    string? IssueTimeText,
    string? BuyerEmail,
    string Currency,
    List<ManualEInvoiceLineDraft> Lines
);

public sealed record ManualEInvoiceLineDraft(
    int LineNo,
    string ProductName,
    string? Barcode,
    string? ProductCode,
    decimal Quantity,
    string? UnitCode,
    decimal UnitPrice,
    decimal KdvRate,
    decimal? KdvAmount,
    decimal? TotalAmount,
    decimal? Gram,
    string? Karat,
    decimal? Workmanship,
    string? ProductCategory,
    decimal? HasGoldEquivalent,
    string? StoneInfo,
    string? SerialNumber
);
