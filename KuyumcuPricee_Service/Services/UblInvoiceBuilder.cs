using System.Globalization;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Services;

public sealed class UblInvoiceBuilder : IUblInvoiceBuilder
{
    private const string SpecialBaseExemptionCode = "805";
    private const string SpecialBaseExemptionReason = "Altından mamül veya altın ihtiva eden ziynet eşyaları ile sikke altınların teslim ve ithali";

    private readonly AppDbContext _db;
    private readonly IJewelryTaxCalculator _taxCalculator;
    private readonly IJewelryProductTypeMapper _productTypeMapper;
    private readonly ExchangeRateService _rates;
    private readonly string _defaultInvoiceTypeCode;
    private readonly string _goldReferenceSource;

    public UblInvoiceBuilder(
        AppDbContext db,
        IJewelryTaxCalculator taxCalculator,
        IJewelryProductTypeMapper productTypeMapper,
        ExchangeRateService rates,
        IConfiguration configuration)
    {
        _db = db;
        _taxCalculator = taxCalculator;
        _productTypeMapper = productTypeMapper;
        _rates = rates;
        _defaultInvoiceTypeCode = NormalizeInvoiceTypeCode(configuration["EInvoice:DefaultInvoiceTypeCode"]);
        _goldReferenceSource = string.IsNullOrWhiteSpace(configuration["EInvoice:GoldPriceReferenceSource"])
            ? "HAS"
            : configuration["EInvoice:GoldPriceReferenceSource"]!.Trim().ToUpperInvariant();
    }

    public async Task<UblBuildResult> BuildOutgoingAsync(
        Invoice invoice,
        Customer? customer,
        EInvoiceProfile? profile,
        string invoiceNumber,
        string documentType,
        CancellationToken ct)
    {
        var saleItems = invoice.SaleId.HasValue
            ? await _db.SaleItems
                .AsNoTracking()
                .Where(x => x.TenantId == invoice.TenantId && x.SaleId == invoice.SaleId.Value)
                .OrderBy(x => x.LineNo)
                .ToListAsync(ct)
            : new List<SaleItem>();

        // SQL Server eski compatibility level ortamlarında list.Contains(...)
        // ifadesi OPENJSON ... WITH üretebilir ve sözdizimi hatasına düşebilir.
        // Bu yüzden önce şube bazındaki ProductItem'ları çekip filtreyi bellekte yapıyoruz.
        var productItemIds = saleItems
            .Where(x => x.ProductItemId.HasValue)
            .Select(x => x.ProductItemId!.Value)
            .ToHashSet();
        var productItems = productItemIds.Count == 0
            ? new List<ProductItem>()
            : (await _db.ProductItems
                .AsNoTracking()
                .Where(x => x.TenantId == invoice.TenantId && x.BranchId == invoice.BranchId)
                .ToListAsync(ct))
                .Where(x => productItemIds.Contains(x.Id))
                .ToList();
        var productItemMap = productItems.ToDictionary(x => x.Id, x => x);

        var productIdsFromItems = productItems.Select(x => x.ProductId).ToHashSet();
        var productCodes = saleItems
            .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
            .Select(x => x.ProductCode.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var products = (await _db.Products
            .AsNoTracking()
            .Where(x => x.TenantId == invoice.TenantId && x.BranchId == invoice.BranchId)
            .ToListAsync(ct))
            .Where(x => productIdsFromItems.Contains(x.Id) ||
                        (!string.IsNullOrWhiteSpace(x.ProductCode) && productCodes.Contains(x.ProductCode)))
            .ToList();
        var productById = products.ToDictionary(x => x.Id, x => x);
        var productByCode = products
            .GroupBy(x => x.ProductCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var branch = await _db.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == invoice.BranchId && x.TenantId == invoice.TenantId, ct);

        var sellerName = profile?.CompanyName?.Trim();
        if (string.IsNullOrWhiteSpace(sellerName))
            sellerName = branch?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(sellerName))
            sellerName = "Firma";

        var sellerTax = DigitsOnly(profile?.TaxNumber);
        if (string.IsNullOrWhiteSpace(sellerTax))
            sellerTax = "1111111111";

        var buyerName = string.IsNullOrWhiteSpace(customer?.FullName) ? "Nihai Tuketici" : customer!.FullName.Trim();
        var buyerTax = DigitsOnly(customer?.NationalId);
        if (string.IsNullOrWhiteSpace(buyerTax))
            buyerTax = "11111111111";

        var adjustedSellRates = await _rates.GetAdjustedSellRatesByCodeAsync(invoice.TenantId, invoice.BranchId, ct);
        var profileSettings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        var lineElements = BuildInvoiceLines(
            saleItems,
            productItemMap,
            productById,
            productByCode,
            invoice.CurrencyOrDefault(),
            adjustedSellRates,
            profileSettings);
        return BuildUbl(
            invoice,
            profile,
            invoiceNumber,
            documentType,
            sellerName,
            sellerTax,
            customer?.Address,
            customer?.City,
            customer?.District,
            ResolvePostalCode(customer?.Address, customer?.City),
            buyerName,
            buyerTax,
            customer?.Email,
            lineElements);
    }

    public async Task<UblBuildResult> BuildOutgoingFromDraftAsync(
        Invoice invoice,
        EInvoiceProfile? profile,
        string invoiceNumber,
        ManualEInvoiceDraft draft,
        CancellationToken ct)
    {
        var branch = await _db.Branches
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == invoice.BranchId && x.TenantId == invoice.TenantId, ct);

        var sellerName = profile?.CompanyName?.Trim();
        if (string.IsNullOrWhiteSpace(sellerName))
            sellerName = branch?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(sellerName))
            sellerName = "Firma";

        var sellerTax = DigitsOnly(profile?.TaxNumber);
        if (string.IsNullOrWhiteSpace(sellerTax))
            sellerTax = "1111111111";

        var buyerName = string.IsNullOrWhiteSpace(draft.BuyerName) ? "Nihai Tuketici" : draft.BuyerName.Trim();
        var buyerTax = DigitsOnly(draft.BuyerTaxNumber);
        if (string.IsNullOrWhiteSpace(buyerTax))
            buyerTax = "11111111111";

        var currency = string.IsNullOrWhiteSpace(draft.Currency) ? invoice.CurrencyOrDefault() : draft.Currency.Trim().ToUpperInvariant();
        var manualLines = BuildManualInvoiceLines(draft.Lines ?? [], currency);
        return BuildUbl(
            invoice,
            profile,
            invoiceNumber,
            draft.DocumentType,
            sellerName,
            sellerTax,
            draft.BuyerAddress,
            draft.BuyerCity,
            draft.BuyerDistrict,
            draft.BuyerPostalCode,
            buyerName,
            buyerTax,
            draft.BuyerEmail,
            manualLines);
    }

    private UblBuildResult BuildUbl(
        Invoice invoice,
        EInvoiceProfile? profile,
        string invoiceNumber,
        string documentType,
        string sellerName,
        string sellerTax,
        string? buyerAddress,
        string? buyerCity,
        string? buyerDistrict,
        string? buyerPostalCode,
        string buyerName,
        string buyerTax,
        string? buyerAlias,
        IReadOnlyList<UblLine> lineElements)
    {
        var invoiceUuid = Guid.NewGuid().ToString();
        var issueDate = invoice.InvoiceDate.Date;
        var issueTime = invoice.InvoiceDate.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var ublInvoiceId = BuildUblInvoiceId(invoiceNumber, issueDate, invoice.Id);
        var currency = invoice.CurrencyOrDefault();
        if (lineElements.Count > 0)
            currency = lineElements[0].Currency;
        var profileId = string.Equals(documentType, "EArsiv", StringComparison.OrdinalIgnoreCase) ? "EARSIVFATURA" : "TEMELFATURA";
        var invoiceTypeCode = ResolveInvoiceTypeCode(lineElements);
        var normalizedLines = NormalizeSpecialBaseExemptions(lineElements, invoiceTypeCode);
        var totalTax = normalizedLines.Sum(x => x.TaxAmount);
        var lineExtension = normalizedLines.Sum(x => x.LineExtensionAmount);
        var taxExclusive = normalizedLines.Sum(x => x.TaxExclusiveAmount);
        var totalWithholding = normalizedLines.Sum(x => x.WithholdingTaxAmount);
        var taxInclusive = normalizedLines.Sum(x => x.PayableAmount);
        var payable = (invoice.GrandTotal > 0 ? invoice.GrandTotal : taxInclusive) - totalWithholding;
        if (payable < 0) payable = 0m;
        var invoiceLevelExemptionXml = string.Empty;
        if (string.Equals(invoiceTypeCode, "OZELMATRAH", StringComparison.OrdinalIgnoreCase))
        {
            invoiceLevelExemptionXml = $@"<cbc:TaxExemptionReasonCode>{SpecialBaseExemptionCode}</cbc:TaxExemptionReasonCode>
        <cbc:TaxExemptionReason>{Xml(SpecialBaseExemptionReason)}</cbc:TaxExemptionReason>";
        }
        else if (totalTax <= 0)
        {
            invoiceLevelExemptionXml = @"<cbc:TaxExemptionReasonCode>351</cbc:TaxExemptionReasonCode>
        <cbc:TaxExemptionReason>KDV istisna</cbc:TaxExemptionReason>";
        }

        var xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Invoice xmlns=""urn:oasis:names:specification:ubl:schema:xsd:Invoice-2""
         xmlns:ext=""urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2""
         xmlns:cac=""urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2""
         xmlns:cbc=""urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2"">
  <ext:UBLExtensions>
    <ext:UBLExtension>
      <ext:ExtensionContent />
    </ext:UBLExtension>
  </ext:UBLExtensions>
  <cbc:UBLVersionID>2.1</cbc:UBLVersionID>
  <cbc:CustomizationID>TR1.2</cbc:CustomizationID>
  <cbc:ProfileID>{Xml(profileId)}</cbc:ProfileID>
  <cbc:ID>{Xml(ublInvoiceId)}</cbc:ID>
  <cbc:CopyIndicator>false</cbc:CopyIndicator>
  <cbc:UUID>{Xml(invoiceUuid)}</cbc:UUID>
  <cbc:IssueDate>{issueDate:yyyy-MM-dd}</cbc:IssueDate>
  <cbc:IssueTime>{Xml(issueTime)}</cbc:IssueTime>
  <cbc:InvoiceTypeCode>{invoiceTypeCode}</cbc:InvoiceTypeCode>
  <cbc:DocumentCurrencyCode>{Xml(currency)}</cbc:DocumentCurrencyCode>
  <cac:AccountingSupplierParty>
    <cac:Party>
      <cac:PartyIdentification><cbc:ID schemeID=""{Xml(ResolveTaxSchemeId(sellerTax))}"">{Xml(sellerTax)}</cbc:ID></cac:PartyIdentification>
      <cac:PartyName><cbc:Name>{Xml(sellerName)}</cbc:Name></cac:PartyName>
      {BuildSellerPostalAddressXml(profile)}
      <cac:PartyTaxScheme>
        <cbc:CompanyID schemeID=""{Xml(ResolveTaxSchemeId(sellerTax))}"">{Xml(sellerTax)}</cbc:CompanyID>
        <cac:TaxScheme><cbc:Name>{Xml(profile?.TaxOffice ?? "VERGI DAIRESI")}</cbc:Name></cac:TaxScheme>
      </cac:PartyTaxScheme>
      {BuildPersonXmlIfTckn(sellerTax, sellerName)}
    </cac:Party>
  </cac:AccountingSupplierParty>
  <cac:AccountingCustomerParty>
    <cac:Party>
      <cac:PartyIdentification><cbc:ID schemeID=""{Xml(ResolveTaxSchemeId(buyerTax))}"">{Xml(buyerTax)}</cbc:ID></cac:PartyIdentification>
      <cac:PartyName><cbc:Name>{Xml(buyerName)}</cbc:Name></cac:PartyName>
      {BuildPostalAddressXml(buyerAddress, buyerCity, buyerDistrict, buyerPostalCode)}
      <cac:PartyTaxScheme>
        <cbc:CompanyID schemeID=""{Xml(ResolveTaxSchemeId(buyerTax))}"">{Xml(buyerTax)}</cbc:CompanyID>
        <cac:TaxScheme><cbc:Name>VERGI DAIRESI</cbc:Name></cac:TaxScheme>
      </cac:PartyTaxScheme>
      {BuildPersonXmlIfTckn(buyerTax, buyerName)}
    </cac:Party>
  </cac:AccountingCustomerParty>
  <cac:TaxTotal>
    <cbc:TaxAmount currencyID=""{Xml(currency)}"">{Fmt(totalTax)}</cbc:TaxAmount>
    <cac:TaxSubtotal>
      <cbc:TaxableAmount currencyID=""{Xml(currency)}"">{Fmt(taxExclusive)}</cbc:TaxableAmount>
      <cbc:TaxAmount currencyID=""{Xml(currency)}"">{Fmt(totalTax)}</cbc:TaxAmount>
      <cac:TaxCategory>
        {invoiceLevelExemptionXml}
        <cac:TaxScheme>
          <cbc:Name>KDV</cbc:Name>
          <cbc:TaxTypeCode>0015</cbc:TaxTypeCode>
        </cac:TaxScheme>
      </cac:TaxCategory>
    </cac:TaxSubtotal>
  </cac:TaxTotal>
{BuildInvoiceWithholdingXml(currency, totalWithholding)}
  <cac:LegalMonetaryTotal>
    <cbc:LineExtensionAmount currencyID=""{Xml(currency)}"">{Fmt(lineExtension)}</cbc:LineExtensionAmount>
    <cbc:TaxExclusiveAmount currencyID=""{Xml(currency)}"">{Fmt(taxExclusive)}</cbc:TaxExclusiveAmount>
    <cbc:TaxInclusiveAmount currencyID=""{Xml(currency)}"">{Fmt(taxInclusive)}</cbc:TaxInclusiveAmount>
    <cbc:PayableAmount currencyID=""{Xml(currency)}"">{Fmt(payable)}</cbc:PayableAmount>
  </cac:LegalMonetaryTotal>
{string.Join(Environment.NewLine, normalizedLines.Select(BuildLineXml))}
</Invoice>";

        xml = NormalizeForEdmSchema(xml, currency, normalizedLines.Count);

        return new UblBuildResult(
            xml,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)),
            sellerTax,
            profile?.SenderLabel,
            buyerName,
            buyerTax,
            buyerAlias);
    }

    private static string BuildInvoiceWithholdingXml(string currency, decimal totalWithholding)
    {
        if (totalWithholding <= 0) return string.Empty;
        return $@"  <cac:WithholdingTaxTotal>
    <cbc:TaxAmount currencyID=""{Xml(currency)}"">{Fmt(totalWithholding)}</cbc:TaxAmount>
  </cac:WithholdingTaxTotal>";
    }

    private static string BuildLineXml(UblLine line)
    {
        var taxPercent = line.TaxRate * 100m;
        var withholdingPercent = line.WithholdingRate * 100m;
        var isZeroVat = line.TaxRate <= 0 || line.TaxAmount <= 0;
        var exemptionCode = string.IsNullOrWhiteSpace(line.ExemptionCode) && isZeroVat ? "351" : line.ExemptionCode;
        var exemptionReason = string.IsNullOrWhiteSpace(line.ExemptionReason) && isZeroVat
            ? "KDV istisna"
            : line.ExemptionReason;
        var exemptionXml = string.IsNullOrWhiteSpace(exemptionCode)
            ? string.Empty
            : $@"<cbc:TaxExemptionReasonCode>{Xml(exemptionCode)}</cbc:TaxExemptionReasonCode>
          <cbc:TaxExemptionReason>{Xml(exemptionReason)}</cbc:TaxExemptionReason>";
        var withholdingXml = line.WithholdingTaxAmount <= 0
            ? string.Empty
            : $@"    <cac:WithholdingTaxTotal>
      <cbc:TaxAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.WithholdingTaxAmount)}</cbc:TaxAmount>
      <cac:TaxSubtotal>
        <cbc:TaxableAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.TaxAmount)}</cbc:TaxableAmount>
        <cbc:TaxAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.WithholdingTaxAmount)}</cbc:TaxAmount>
        <cbc:Percent>{Fmt(withholdingPercent)}</cbc:Percent>
        <cac:TaxCategory>
          <cac:TaxScheme>
            <cbc:Name>KDV TEVKIFAT</cbc:Name>
            <cbc:TaxTypeCode>601</cbc:TaxTypeCode>
          </cac:TaxScheme>
        </cac:TaxCategory>
      </cac:TaxSubtotal>
    </cac:WithholdingTaxTotal>";
        var itemPropsXml = string.Join(Environment.NewLine, line.AdditionalProperties.Select(p =>
            $@"      <cac:AdditionalItemProperty>
        <cbc:Name>{Xml(p.Name)}</cbc:Name>
        <cbc:Value>{Xml(p.Value)}</cbc:Value>
      </cac:AdditionalItemProperty>"));

        return $@"  <cac:InvoiceLine>
    <cbc:ID>{line.LineNo}</cbc:ID>
    <cbc:InvoicedQuantity unitCode=""{Xml(line.UnitCode)}"">{Fmt(line.Quantity)}</cbc:InvoicedQuantity>
    <cbc:LineExtensionAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.LineExtensionAmount)}</cbc:LineExtensionAmount>
    <cac:TaxTotal>
      <cbc:TaxAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.TaxAmount)}</cbc:TaxAmount>
      <cac:TaxSubtotal>
        <cbc:TaxableAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.TaxExclusiveAmount)}</cbc:TaxableAmount>
        <cbc:TaxAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.TaxAmount)}</cbc:TaxAmount>
        <cbc:Percent>{Fmt(taxPercent)}</cbc:Percent>
        <cac:TaxCategory>
          {exemptionXml}
          <cac:TaxScheme>
            <cbc:Name>KDV</cbc:Name>
            <cbc:TaxTypeCode>0015</cbc:TaxTypeCode>
          </cac:TaxScheme>
        </cac:TaxCategory>
      </cac:TaxSubtotal>
    </cac:TaxTotal>
{withholdingXml}
    <cac:Item>
      <cbc:Name>{Xml(line.Description)}</cbc:Name>
      <cac:SellersItemIdentification>
        <cbc:ID>{Xml(line.ProductCode)}</cbc:ID>
      </cac:SellersItemIdentification>
      <cac:ItemInstance>
{itemPropsXml}
      </cac:ItemInstance>
    </cac:Item>
    <cac:Price><cbc:PriceAmount currencyID=""{Xml(line.Currency)}"">{Fmt(line.UnitPrice)}</cbc:PriceAmount></cac:Price>
  </cac:InvoiceLine>";
    }

    private static IReadOnlyList<UblLine> NormalizeSpecialBaseExemptions(IReadOnlyList<UblLine> lines, string invoiceTypeCode)
    {
        if (!string.Equals(invoiceTypeCode, "OZELMATRAH", StringComparison.OrdinalIgnoreCase))
            return lines;

        var updated = new List<UblLine>(lines.Count);
        foreach (var line in lines)
        {
            var isZeroVatLine = line.TaxRate <= 0m || line.TaxAmount <= 0m;
            if (!isZeroVatLine)
            {
                updated.Add(line);
                continue;
            }

            var normalizedExemptionCode = string.IsNullOrWhiteSpace(line.ExemptionCode) || line.ExemptionCode == "351"
                ? SpecialBaseExemptionCode
                : line.ExemptionCode;
            var normalizedExemptionReason = string.IsNullOrWhiteSpace(line.ExemptionReason) || line.ExemptionReason == "KDV istisna"
                ? SpecialBaseExemptionReason
                : line.ExemptionReason;

            updated.Add(line with
            {
                ExemptionCode = normalizedExemptionCode,
                ExemptionReason = normalizedExemptionReason
            });
        }

        return updated;
    }

    private List<UblLine> BuildInvoiceLines(
        List<SaleItem> saleItems,
        IReadOnlyDictionary<Guid, ProductItem> productItemMap,
        IReadOnlyDictionary<Guid, Product> productById,
        IReadOnlyDictionary<string, Product> productByCode,
        string currency,
        IReadOnlyDictionary<string, decimal> adjustedSellRates,
        EInvoiceProfileSettings profileSettings)
    {
        var lines = new List<UblLine>();
        var salesVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(profileSettings.SalesInvoiceVatRatePercent);
        var specialCraftedVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(profileSettings.SpecialMatrahCraftedVatRatePercent);
        var specialZiynetVatRateRatio = EInvoiceProfileSettingsCodec.VatPercentToRatio(profileSettings.SpecialMatrahZiynetVatRatePercent);
        if (saleItems.Count == 0)
        {
            lines.Add(new UblLine(
                1,
                "Satış",
                1m,
                0m,
                0m,
                currency,
                0m,
                0m,
                0m,
                "",
                "",
                "SATIS",
                [],
                0m,
                0m,
                0m,
                0m,
                "C62"));
            return lines;
        }

        foreach (var item in saleItems)
        {
            if (item.Kind == ItemKind.Forex)
                continue;

            productItemMap.TryGetValue(item.ProductItemId ?? Guid.Empty, out var productItem);
            Product? product = null;
            if (productItem is not null)
                productById.TryGetValue(productItem.ProductId, out product);
            if (product is null && !string.IsNullOrWhiteSpace(item.ProductCode))
                productByCode.TryGetValue(item.ProductCode.Trim(), out product);

            var quantity = item.Quantity <= 0 ? 1 : item.Quantity;
            var unitPrice = item.UnitPrice < 0 ? 0 : item.UnitPrice;
            var discount = item.Discount < 0 ? 0 : item.Discount;
            var lineExtension = Math.Max(0, quantity * unitPrice - discount);
            var normalizedCategory = NormalizeCategory(item.Category, product?.Category);
            var normalizedKarat = NormalizeKarat(item.Karat, productItem?.Karat, product?.Karat);
            var productType = _productTypeMapper.Resolve(item.Kind, normalizedCategory, normalizedKarat, item.ProductName);
            var stoneInfo = ResolveStoneInfo(item.ProductName, normalizedCategory, product?.MalTanim, product?.Olcu);
            var workmanshipHas = product?.BirimSatisIscilikHas ?? 0m;
            var hasEquivalent = CalculateHasEquivalent(quantity, normalizedKarat);
            var isZiynetSale = IsZiynetSarrafiye(item.ProductName, normalizedCategory, product, productType);
            var isSpecialProductSale = IsSpecialProductSale(item.ProductName, normalizedCategory, product);

            var tax = _taxCalculator.Calculate(new JewelryTaxContext(
                item.Kind,
                productType,
                normalizedCategory,
                normalizedKarat,
                stoneInfo != "-",
                lineExtension,
                NormalizeTaxRate(item.TaxRate),
                workmanshipHas,
                item.ProductName));
            var taxAmount = Math.Round(lineExtension * tax.AppliedTaxRate, 2, MidpointRounding.AwayFromZero);
            var withholdingTaxAmount = Math.Round(taxAmount * tax.WithholdingRate, 2, MidpointRounding.AwayFromZero);
            var payable = lineExtension + taxAmount;
            var commercialUnitPrice = quantity > 0 ? Math.Round(lineExtension / quantity, 2, MidpointRounding.AwayFromZero) : unitPrice;
            var desc = string.IsNullOrWhiteSpace(item.ProductName) ? "Ürün" : item.ProductName.Trim();
            var barcode = productItem?.Barcode ?? product?.Barcode ?? "-";
            var serial = productItem?.Serial ?? "-";
            var productCode = string.IsNullOrWhiteSpace(item.ProductCode) ? "-" : item.ProductCode.Trim();
            var grossTotal = item.LineTotal > 0 ? item.LineTotal : payable;

            if (isSpecialProductSale)
            {
                var specialVatRate = salesVatRateRatio;
                var specialGross = grossTotal > 0m ? grossTotal : lineExtension;
                if (specialGross < 0m) specialGross = 0m;
                var specialNet = specialVatRate > 0m
                    ? Math.Round(specialGross / (1m + specialVatRate), 2, MidpointRounding.AwayFromZero)
                    : Math.Round(specialGross, 2, MidpointRounding.AwayFromZero);
                var specialTaxAmount = Math.Round(specialGross - specialNet, 2, MidpointRounding.AwayFromZero);
                var specialPayable = specialGross;
                var specialUnitPrice = quantity > 0m
                    ? Math.Round(specialNet / quantity, 2, MidpointRounding.AwayFromZero)
                    : specialNet;
                var specialProps = new List<UblAdditionalProperty>
                {
                    new("ÜRÜN ADI", desc),
                    new("BARKOD", barcode),
                    new("ÜRÜN KODU", productCode),
                    new("ÜRÜN TİPİ", "ÖZEL ÜRÜN"),
                    new("MİKTAR", Fmt(quantity)),
                    new("BİRİM FİYAT", Fmt(specialUnitPrice)),
                    new("KDV DAHİL SATIŞ TUTARI", Fmt(specialGross)),
                    new("KDV ORANI", Fmt(specialVatRate * 100m)),
                    new("KDV TUTARI", Fmt(specialTaxAmount)),
                    new("TOPLAM TUTAR", Fmt(specialPayable)),
                    new("FATURA MODELİ", "ÖZEL ÜRÜN - TEK SATIR")
                };

                lines.Add(new UblLine(
                    item.LineNo <= 0 ? lines.Count + 1 : item.LineNo,
                    desc,
                    quantity,
                    specialUnitPrice,
                    discount,
                    currency,
                    specialVatRate,
                    specialNet,
                    specialTaxAmount,
                    "",
                    "",
                    productCode,
                    specialProps,
                    specialPayable,
                    0m,
                    0m,
                    0m,
                    "C62"));
                continue;
            }

            if (isZiynetSale)
            {
                var ziynetName = ResolveZiynetDisplayName(item.ProductName, product?.ZiynetTipi);
                var ziynetUnitGram = ResolveZiynetUnitGram(ziynetName, product?.Olcu, product?.MalTanim);
                var karatMilyem = JewelrySpecialBaseCalculator.MilyemFromKarat(normalizedKarat);
                if (karatMilyem <= 0m) karatMilyem = 0.916m;
                var hasSell = _rates.GetKaratGramSellPrice("HAS", _goldReferenceSource, adjustedSellRates);
                if (hasSell <= 0m)
                    hasSell = _rates.GetKaratGramSellPrice(normalizedKarat, _goldReferenceSource, adjustedSellRates);

                var firstUnit = Math.Round(ziynetUnitGram * karatMilyem * hasSell, 2, MidpointRounding.AwayFromZero);
                var firstTotal = Math.Round(firstUnit * quantity, 2, MidpointRounding.AwayFromZero);
                var saleGross = grossTotal > 0m ? grossTotal : Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
                if (firstTotal >= saleGross && saleGross > 0m)
                    firstTotal = Math.Max(0m, saleGross - 0.01m);
                var firstUnitAdjusted = quantity > 0m
                    ? Math.Round(firstTotal / quantity, 2, MidpointRounding.AwayFromZero)
                    : firstTotal;

                var secondGross = Math.Max(0m, Math.Round(saleGross - firstTotal, 2, MidpointRounding.AwayFromZero));
                var secondNet = specialZiynetVatRateRatio > 0m
                    ? Math.Round(secondGross / (1m + specialZiynetVatRateRatio), 2, MidpointRounding.AwayFromZero)
                    : Math.Round(secondGross, 2, MidpointRounding.AwayFromZero);
                var secondTax = Math.Round(secondGross - secondNet, 2, MidpointRounding.AwayFromZero);
                var secondUnitNet = quantity > 0m
                    ? Math.Round(secondNet / quantity, 2, MidpointRounding.AwayFromZero)
                    : secondNet;

                var firstProps = new List<UblAdditionalProperty>
                {
                    new("ÜRÜN ADI", ziynetName),
                    new("BARKOD", barcode),
                    new("ÜRÜN KODU", productCode),
                    new("GRAM", Fmt(ziynetUnitGram * quantity)),
                    new("AYAR", string.IsNullOrWhiteSpace(normalizedKarat) ? "-" : normalizedKarat),
                    new("MİKTAR", Fmt(quantity)),
                    new("BİRİM FİYAT", Fmt(firstUnitAdjusted)),
                    new("KDV ORANI", "0"),
                    new("KDV TUTARI", "0"),
                    new("TOPLAM TUTAR", Fmt(firstTotal)),
                    new("DÖVİZ TİPİ", currency),
                    new("HAS ALTIN KARŞILIĞI", Fmt(ziynetUnitGram * karatMilyem * quantity)),
                    new("ÜRÜN KATEGORİSİ", "ÖZEL MATRAH"),
                    new("TAŞ BİLGİSİ", stoneInfo),
                    new("SERİ NUMARASI", serial),
                    new("ÖZEL MATRAH", "ALTIN BEDELİ KDV'DEN İSTİSNA")
                };

                var secondProps = new List<UblAdditionalProperty>
                {
                    new("ÜRÜN ADI", $"{ziynetName} İşçiliği"),
                    new("BARKOD", barcode),
                    new("ÜRÜN KODU", $"{productCode}-ISCILIK"),
                    new("GRAM", Fmt(ziynetUnitGram * quantity)),
                    new("AYAR", string.IsNullOrWhiteSpace(normalizedKarat) ? "-" : normalizedKarat),
                    new("MİKTAR", Fmt(quantity)),
                    new("BİRİM FİYAT", Fmt(secondUnitNet)),
                    new("KDV ORANI", Fmt(specialZiynetVatRateRatio * 100m)),
                    new("KDV TUTARI", Fmt(secondTax)),
                    new("TOPLAM TUTAR", Fmt(secondGross)),
                    new("DÖVİZ TİPİ", currency),
                    new("HAS ALTIN KARŞILIĞI", "0"),
                    new("ÜRÜN KATEGORİSİ", "ÖZEL MATRAH İŞÇİLİK"),
                    new("TAŞ BİLGİSİ", stoneInfo),
                    new("SERİ NUMARASI", serial),
                    new("ÖZEL MATRAH", "İŞÇİLİK BEDELİ KDV'YE TABİDİR")
                };

                lines.Add(new UblLine(
                    lines.Count + 1,
                    ziynetName,
                    quantity,
                    firstUnitAdjusted,
                    0m,
                    currency,
                    0m,
                    firstTotal,
                    0m,
                    "805",
                    SpecialBaseExemptionReason,
                    productCode,
                    firstProps,
                    firstTotal,
                    0m,
                    0m,
                    ziynetUnitGram * karatMilyem * quantity,
                    "C62"));

                lines.Add(new UblLine(
                    lines.Count + 1,
                    $"{ziynetName} İşçiliği",
                    quantity,
                    secondUnitNet,
                    0m,
                    currency,
                    specialZiynetVatRateRatio,
                    secondNet,
                    secondTax,
                    "",
                    "",
                    $"{productCode}-ISCILIK",
                    secondProps,
                    secondGross,
                    0m,
                    0m,
                    0m,
                    "C62"));
                continue;
            }

            if (!isZiynetSale && JewelrySpecialBaseCalculator.TryBuild(
                    quantity,
                    normalizedKarat,
                    ResolveGoldLineBaseAmount(quantity, normalizedKarat, lineExtension, adjustedSellRates),
                    product?.Cost ?? 0m,
                    workmanshipHas,
                    grossTotal,
                    tax.AppliedTaxRate,
                    out var special) &&
                special.KdvMatrahi > 0m &&
                special.AltinBedeli > 0m)
            {
                var goldLabel = JewelrySpecialBaseCalculator.BuildGoldLineName(normalizedKarat);
                var goldProps = new List<UblAdditionalProperty>
                {
                    new("ÜRÜN ADI", goldLabel),
                    new("BARKOD", barcode),
                    new("ÜRÜN KODU", productCode),
                    new("GRAM", Fmt(quantity)),
                    new("AYAR", string.IsNullOrWhiteSpace(normalizedKarat) ? "-" : normalizedKarat),
                    new("MİKTAR", Fmt(quantity)),
                    new("BİRİM FİYAT", Fmt(special.AltinBirimFiyat)),
                    new("TİCARİ SATIŞ BİRİM FİYATI", Fmt(commercialUnitPrice)),
                    new("FİYAT BAZI", $"{goldLabel} SATIŞ FİYATI"),
                    new("REFERANS HAS KURU", Fmt(special.HasAltinGramFiyat)),
                    new("KDV ORANI", "0"),
                    new("KDV TUTARI", "0"),
                    new("TOPLAM TUTAR", Fmt(special.AltinBedeli)),
                    new("DÖVİZ TİPİ", currency),
                    new("HAS ALTIN KARŞILIĞI", Fmt(special.SafHasGram)),
                    new("ÜRÜN KATEGORİSİ", normalizedCategory),
                    new("TAŞ BİLGİSİ", stoneInfo),
                    new("SERİ NUMARASI", serial),
                    new("ÖZEL MATRAH", "ALTIN BEDELİ KDV'DEN İSTİSNA")
                };

                var workmanshipLabel = JewelrySpecialBaseCalculator.BuildWorkmanshipLineName(normalizedKarat);
                var workmanshipCodeSuffix = JewelrySpecialBaseCalculator.BuildWorkmanshipCodeSuffix(normalizedKarat);
                var configuredCraftedRate = specialCraftedVatRateRatio > 0m ? specialCraftedVatRateRatio : special.KdvRateRatio;
                var configuredCraftedTax = Math.Round(special.KdvMatrahi * configuredCraftedRate, 2, MidpointRounding.AwayFromZero);
                var configuredCraftedTotal = Math.Round(special.KdvMatrahi + configuredCraftedTax, 2, MidpointRounding.AwayFromZero);
                var configuredCraftedUnit = quantity > 0m
                    ? Math.Round(special.KdvMatrahi / quantity, 2, MidpointRounding.AwayFromZero)
                    : special.KdvMatrahi;
                var workmanshipProps = new List<UblAdditionalProperty>
                {
                    new("ÜRÜN ADI", workmanshipLabel),
                    new("BARKOD", barcode),
                    new("ÜRÜN KODU", $"{productCode}-{workmanshipCodeSuffix}"),
                    new("GRAM", Fmt(quantity)),
                    new("AYAR", string.IsNullOrWhiteSpace(normalizedKarat) ? "-" : normalizedKarat),
                    new("İŞÇİLİK", Fmt(workmanshipHas)),
                    new("MİKTAR", Fmt(quantity)),
                    new("BİRİM FİYAT", Fmt(configuredCraftedUnit)),
                    new("TİCARİ SATIŞ BİRİM FİYATI", Fmt(commercialUnitPrice)),
                    new("FİYAT BAZI", $"{goldLabel} İŞÇİLİK FİYATI"),
                    new("REFERANS HAS KURU", Fmt(special.HasAltinGramFiyat)),
                    new("KDV ORANI", Fmt(configuredCraftedRate * 100m)),
                    new("KDV TUTARI", Fmt(configuredCraftedTax)),
                    new("TOPLAM TUTAR", Fmt(configuredCraftedTotal)),
                    new("DÖVİZ TİPİ", currency),
                    new("HAS ALTIN KARŞILIĞI", "0"),
                    new("ÜRÜN KATEGORİSİ", normalizedCategory),
                    new("TAŞ BİLGİSİ", stoneInfo),
                    new("SERİ NUMARASI", serial),
                    new("ÖZEL MATRAH", "İŞÇİLİK BEDELİ KDV'YE TABİDİR")
                };

                lines.Add(new UblLine(
                    lines.Count + 1,
                    goldLabel,
                    quantity,
                    special.AltinBirimFiyat,
                    0m,
                    currency,
                    0m,
                    special.AltinBedeli,
                    0m,
                    string.IsNullOrWhiteSpace(tax.ExemptionCode) ? "351" : tax.ExemptionCode,
                    string.IsNullOrWhiteSpace(tax.ExemptionReason) ? "KDV istisna" : tax.ExemptionReason,
                    productCode,
                    goldProps,
                    special.AltinBedeli,
                    0m,
                    0m,
                    special.SafHasGram,
                    "GRM"));

                var specialWithholdingTax = Math.Round(configuredCraftedTax * tax.WithholdingRate, 2, MidpointRounding.AwayFromZero);
                lines.Add(new UblLine(
                    lines.Count + 1,
                    workmanshipLabel,
                    quantity,
                    configuredCraftedUnit,
                    0m,
                    currency,
                    configuredCraftedRate,
                    special.KdvMatrahi,
                    configuredCraftedTax,
                    "",
                    "",
                    $"{productCode}-{workmanshipCodeSuffix}",
                    workmanshipProps,
                    configuredCraftedTotal,
                    tax.WithholdingRate,
                    specialWithholdingTax,
                    0m,
                    "GRM"));
                continue;
            }

            var props = new List<UblAdditionalProperty>
            {
                new("ÜRÜN ADI", desc),
                new("BARKOD", barcode),
                new("ÜRÜN KODU", productCode),
                new("GRAM", Fmt(quantity)),
                new("AYAR", string.IsNullOrWhiteSpace(normalizedKarat) ? "-" : normalizedKarat),
                new("İŞÇİLİK", Fmt(workmanshipHas)),
                new("MİKTAR", Fmt(quantity)),
                new("BİRİM FİYAT", Fmt(unitPrice)),
                new("KDV ORANI", Fmt(tax.AppliedTaxRate * 100m)),
                new("KDV TUTARI", Fmt(taxAmount)),
                new("TOPLAM TUTAR", Fmt(payable)),
                new("DÖVİZ TİPİ", currency),
                new("HAS ALTIN KARŞILIĞI", Fmt(hasEquivalent)),
                new("ÜRÜN KATEGORİSİ", normalizedCategory),
                new("TAŞ BİLGİSİ", stoneInfo),
                new("SERİ NUMARASI", serial),
                new("ÜRÜN TİPİ", productType)
            };

            lines.Add(new UblLine(
                item.LineNo <= 0 ? lines.Count + 1 : item.LineNo,
                desc,
                quantity,
                unitPrice,
                discount,
                currency,
                tax.AppliedTaxRate,
                lineExtension,
                taxAmount,
                tax.ExemptionCode ?? "",
                tax.ExemptionReason ?? "",
                productCode,
                props,
                payable,
                tax.WithholdingRate,
                withholdingTaxAmount,
                hasEquivalent,
                "GRM"));
        }

        return lines;
    }

    private static List<UblLine> BuildManualInvoiceLines(IReadOnlyList<ManualEInvoiceLineDraft> draftLines, string currency)
    {
        var lines = new List<UblLine>();
        foreach (var line in draftLines.OrderBy(x => x.LineNo))
        {
            var quantity = line.Quantity <= 0 ? 1m : line.Quantity;
            var unitPrice = line.UnitPrice < 0 ? 0 : line.UnitPrice;
            var lineExtension = Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);
            var taxRate = NormalizeTaxRate(line.KdvRate);
            var taxAmount = line.KdvAmount.HasValue
                ? Math.Max(0, line.KdvAmount.Value)
                : Math.Round(lineExtension * taxRate, 2, MidpointRounding.AwayFromZero);
            var payable = line.TotalAmount.HasValue
                ? Math.Max(0, line.TotalAmount.Value)
                : lineExtension + taxAmount;
            var description = string.IsNullOrWhiteSpace(line.ProductName) ? "Ürün" : line.ProductName.Trim();
            var productCode = string.IsNullOrWhiteSpace(line.ProductCode) ? "-" : line.ProductCode.Trim();
            var karat = string.IsNullOrWhiteSpace(line.Karat) ? "-" : line.Karat.Trim();
            var category = string.IsNullOrWhiteSpace(line.ProductCategory) ? "-" : line.ProductCategory.Trim();
            var stone = string.IsNullOrWhiteSpace(line.StoneInfo) ? "-" : line.StoneInfo.Trim();
            var serial = string.IsNullOrWhiteSpace(line.SerialNumber) ? "-" : line.SerialNumber.Trim();
            var barcode = string.IsNullOrWhiteSpace(line.Barcode) ? "-" : line.Barcode.Trim();

            var props = new List<UblAdditionalProperty>
            {
                new("ÜRÜN ADI", description),
                new("BARKOD", barcode),
                new("ÜRÜN KODU", productCode),
                new("GRAM", Fmt(line.Gram ?? quantity)),
                new("AYAR", karat),
                new("İŞÇİLİK", Fmt(line.Workmanship ?? 0m)),
                new("MİKTAR", Fmt(quantity)),
                new("BİRİM FİYAT", Fmt(unitPrice)),
                new("KDV ORANI", Fmt(taxRate * 100m)),
                new("KDV TUTARI", Fmt(taxAmount)),
                new("TOPLAM TUTAR", Fmt(payable)),
                new("DÖVİZ TİPİ", currency),
                new("HAS ALTIN KARŞILIĞI", Fmt(line.HasGoldEquivalent ?? 0m)),
                new("ÜRÜN KATEGORİSİ", category),
                new("TAŞ BİLGİSİ", stone),
                new("SERİ NUMARASI", serial)
            };

            lines.Add(new UblLine(
                line.LineNo <= 0 ? lines.Count + 1 : line.LineNo,
                description,
                quantity,
                unitPrice,
                0m,
                currency,
                taxRate,
                lineExtension,
                taxAmount,
                "",
                "",
                productCode,
                props,
                payable,
                0m,
                0m,
                line.HasGoldEquivalent ?? 0m,
                "GRM"));
        }

        if (lines.Count == 0)
        {
            lines.Add(new UblLine(
                1,
                "Satış",
                1m,
                0m,
                0m,
                currency,
                0m,
                0m,
                0m,
                "",
                "",
                "SATIS",
                [],
                0m,
                0m,
                0m,
                0m,
                "C62"));
        }
        return lines;
    }

    private static decimal NormalizeTaxRate(decimal value)
    {
        if (value <= 0) return 0m;
        if (value > 1m) return value / 100m;
        return value;
    }

    private static string NormalizeCategory(string? saleCategory, string? productCategory)
    {
        var raw = !string.IsNullOrWhiteSpace(saleCategory) ? saleCategory : productCategory;
        return string.IsNullOrWhiteSpace(raw) ? "DİĞER" : raw.Trim().ToUpperInvariant();
    }

    private static string NormalizeKarat(string? saleKarat, string? itemKarat, string? productKarat)
    {
        var raw = !string.IsNullOrWhiteSpace(saleKarat)
            ? saleKarat
            : (!string.IsNullOrWhiteSpace(itemKarat) ? itemKarat : productKarat);
        return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToUpperInvariant();
    }

    private static decimal CalculateHasEquivalent(decimal quantity, string normalizedKarat)
    {
        var k = normalizedKarat.Replace("AYAR", "", StringComparison.OrdinalIgnoreCase)
            .Replace("K", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", ".")
            .Trim();
        if (string.Equals(normalizedKarat, "HAS", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedKarat, "HAS ALTIN", StringComparison.OrdinalIgnoreCase))
            return quantity;
        if (!decimal.TryParse(k, NumberStyles.Number, CultureInfo.InvariantCulture, out var karat))
            return 0m;
        if (karat <= 0) return 0m;
        return Math.Round(quantity * (karat / 24m), 4, MidpointRounding.AwayFromZero);
    }

    private static string ResolveStoneInfo(string? productName, string normalizedCategory, string? malTanim, string? olcu)
    {
        var haystack = $"{productName} {normalizedCategory} {malTanim} {olcu}".ToUpperInvariant();
        var isStone = haystack.Contains("PIRLANTA", StringComparison.OrdinalIgnoreCase) ||
                      haystack.Contains("TAŞ", StringComparison.OrdinalIgnoreCase) ||
                      haystack.Contains("TAS", StringComparison.OrdinalIgnoreCase) ||
                      haystack.Contains("ELMAS", StringComparison.OrdinalIgnoreCase);
        if (!isStone) return "-";
        if (!string.IsNullOrWhiteSpace(malTanim)) return malTanim.Trim();
        if (!string.IsNullOrWhiteSpace(olcu)) return olcu.Trim();
        return "TAŞLI ÜRÜN";
    }

    private static bool IsZiynetSarrafiye(string? productName, string normalizedCategory, Product? product, string? productType)
    {
        if (product?.InventoryType == InventoryType.Ziynet)
            return true;
        if (string.Equals(productType, "ZİYNET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(productType, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            return true;

        var text = $"{productName} {normalizedCategory} {product?.ZiynetTipi} {product?.Category}".ToUpperInvariant();
        return text.Contains("ZİYNET", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ZIYNET", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ÇEYREK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("CEYREK", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("YARIM", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("TAM", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ATA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpecialProductSale(string? productName, string normalizedCategory, Product? product)
    {
        if (product?.IsSpecialProduct == true)
            return true;
        var text = $"{productName} {normalizedCategory} {product?.Category}".ToUpperInvariant();
        return text.Contains("ÖZEL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("OZEL", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("SAAT", StringComparison.OrdinalIgnoreCase);
    }

    private static string DigitsOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var chars = text.Where(char.IsDigit).ToArray();
        return new string(chars);
    }

    private static string Xml(string? text) => SecurityElement.Escape(text ?? string.Empty) ?? string.Empty;
    private static string Fmt(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string NormalizeForEdmSchema(string xml, string currency, int lineCount)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null) return xml;

            // Bazı EDM validator sürümlerinde yanlış namespace/sırada gelen Signature elemanı şema hatası üretir.
            var signatureNodes = root.Elements().Where(e => e.Name.LocalName == "Signature").ToList();
            foreach (var node in signatureNodes)
                node.Remove();

            var cbcNs = (XNamespace)"urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
            var hasTaxCurrency = root.Elements().Any(e => e.Name == cbcNs + "TaxCurrencyCode");
            var hasLineCount = root.Elements().Any(e => e.Name == cbcNs + "LineCountNumeric");

            var docCurrency = root.Elements().FirstOrDefault(e => e.Name == cbcNs + "DocumentCurrencyCode");
            if (docCurrency is not null && !hasTaxCurrency)
                docCurrency.AddAfterSelf(new XElement(cbcNs + "TaxCurrencyCode", currency));

            if (!hasLineCount)
            {
                var buyerRef = root.Elements().FirstOrDefault(e => e.Name == cbcNs + "BuyerReference");
                if (buyerRef is not null)
                    buyerRef.AddAfterSelf(new XElement(cbcNs + "LineCountNumeric", lineCount <= 0 ? 1 : lineCount));
                else
                {
                    var taxCurrency = root.Elements().FirstOrDefault(e => e.Name == cbcNs + "TaxCurrencyCode");
                    if (taxCurrency is not null)
                        taxCurrency.AddAfterSelf(new XElement(cbcNs + "LineCountNumeric", lineCount <= 0 ? 1 : lineCount));
                }
            }

            return doc.Declaration is null
                ? doc.ToString(SaveOptions.DisableFormatting)
                : doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return xml;
        }
    }

    private static string BuildSellerPostalAddressXml(EInvoiceProfile? profile)
    {
        var address = profile?.CompanyAddress;
        var (city, district) = ParseCityDistrictFromAddress(address);
        var postalCode = ResolvePostalCode(address, city, null);
        return BuildPostalAddressXml(address, city, district, postalCode, useFallbackDefaults: false);
    }

    private static string BuildPostalAddressXml(
        string? address,
        string? city = null,
        string? district = null,
        string? postalCode = null,
        bool useFallbackDefaults = true)
    {
        var street = string.IsNullOrWhiteSpace(address) ? "-" : address.Trim();
        var resolvedCity = string.IsNullOrWhiteSpace(city)
            ? ResolveCityFromAddress(address)
            : city.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(resolvedCity) && useFallbackDefaults)
            resolvedCity = "TRABZON";

        var resolvedDistrict = string.IsNullOrWhiteSpace(district)
            ? ResolveDistrictFromAddress(address, resolvedCity) ?? (useFallbackDefaults ? "MERKEZ" : null)
            : district.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(resolvedDistrict) && useFallbackDefaults)
            resolvedDistrict = "MERKEZ";

        var resolvedPostalCode = ResolvePostalCode(address, resolvedCity, postalCode);
        if (string.IsNullOrWhiteSpace(resolvedPostalCode) && useFallbackDefaults)
            resolvedPostalCode = "61000";

        return $@"<cac:PostalAddress>
      <cbc:StreetName>{Xml(street)}</cbc:StreetName>
      <cbc:CitySubdivisionName>{Xml(resolvedDistrict ?? "-")}</cbc:CitySubdivisionName>
      <cbc:CityName>{Xml(resolvedCity ?? "-")}</cbc:CityName>
      <cbc:PostalZone>{Xml(resolvedPostalCode ?? "-")}</cbc:PostalZone>
      <cac:Country>
        <cbc:IdentificationCode>TR</cbc:IdentificationCode>
        <cbc:Name>TURKEY</cbc:Name>
      </cac:Country>
    </cac:PostalAddress>";
    }

    private static string? ResolvePostalCode(string? address, string? city, string? explicitPostalCode = null)
    {
        var explicitCode = DigitsOnly(explicitPostalCode);
        if (explicitCode.Length == 5) return explicitCode;

        var inAddress = DigitsOnly(ExtractPostalCodeFromText(address));
        if (inAddress.Length == 5) return inAddress;

        if (string.IsNullOrWhiteSpace(city)) return null;
        if (!CityPlateCodes.TryGetValue(NormalizeCityForLookup(city), out var plate)) return null;
        return $"{plate:00}000";
    }

    private static string? ExtractPostalCodeFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b\d{5}\b");
        return match.Success ? match.Value : null;
    }

    private static (string? City, string? District) ParseCityDistrictFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return (null, null);

        var text = address.Trim();
        var slashMatch = Regex.Match(text, @"([^\s/]+)\s*/\s*([^\s/]+)\s*$", RegexOptions.IgnoreCase);
        if (slashMatch.Success)
        {
            var district = slashMatch.Groups[1].Value.Trim();
            var cityToken = slashMatch.Groups[2].Value.Trim();
            var city = ResolveCityToken(cityToken) ?? NormalizeCityForLookup(cityToken);
            return (city, district.ToUpperInvariant());
        }

        var resolvedCity = ResolveCityFromAddress(text);
        return (resolvedCity, ResolveDistrictFromAddress(text, resolvedCity));
    }

    private static string? ResolveDistrictFromAddress(string? address, string? city)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var slashMatch = Regex.Match(address.Trim(), @"([^\s/]+)\s*/\s*([^\s/]+)\s*$", RegexOptions.IgnoreCase);
        if (slashMatch.Success)
            return slashMatch.Groups[1].Value.Trim().ToUpperInvariant();

        return null;
    }

    private static string? ResolveCityToken(string token)
    {
        var normalized = NormalizeCityForLookup(token);
        if (CityPlateCodes.ContainsKey(normalized))
            return normalized;
        return ResolveCityFromAddress(token);
    }

    private static string? ResolveCityFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return null;

        var normalizedAddress = NormalizeCityForLookup(address);
        return CityPlateCodes.Keys
            .OrderByDescending(x => x.Length)
            .FirstOrDefault(city => normalizedAddress.Contains(NormalizeCityForLookup(city), StringComparison.Ordinal));
    }

    private static string NormalizeCityForLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim().ToUpperInvariant();
        text = text
            .Replace('İ', 'I')
            .Replace('I', 'I')
            .Replace('Ş', 'S')
            .Replace('Ğ', 'G')
            .Replace('Ü', 'U')
            .Replace('Ö', 'O')
            .Replace('Ç', 'C');
        return text;
    }

    private static string BuildPersonXmlIfTckn(string? taxNo, string buyerName)
    {
        if (DigitsOnly(taxNo).Length != 11)
            return string.Empty;
        var parts = (buyerName ?? string.Empty)
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstName = parts.Length > 0 ? parts[0] : "AD";
        var familyName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : "SOYAD";
        return $@"<cac:Person>
      <cbc:FirstName>{Xml(firstName)}</cbc:FirstName>
      <cbc:FamilyName>{Xml(familyName)}</cbc:FamilyName>
    </cac:Person>";
    }

    private static string ResolveTaxSchemeId(string? taxNo)
    {
        var digits = DigitsOnly(taxNo);
        return digits.Length == 11 ? "TCKN" : "VKN";
    }

    private static readonly Dictionary<string, int> CityPlateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADANA"] = 1, ["ADIYAMAN"] = 2, ["AFYONKARAHISAR"] = 3, ["AĞRI"] = 4, ["AMASYA"] = 5, ["ANKARA"] = 6, ["ANTALYA"] = 7, ["ARTVIN"] = 8, ["AYDIN"] = 9,
        ["BALIKESIR"] = 10, ["BILECIK"] = 11, ["BINGÖL"] = 12, ["BITLIS"] = 13, ["BOLU"] = 14, ["BURDUR"] = 15, ["BURSA"] = 16, ["ÇANAKKALE"] = 17, ["ÇANKIRI"] = 18,
        ["ÇORUM"] = 19, ["DENIZLI"] = 20, ["DIYARBAKIR"] = 21, ["EDIRNE"] = 22, ["ELAZIĞ"] = 23, ["ERZINCAN"] = 24, ["ERZURUM"] = 25, ["ESKIŞEHIR"] = 26,
        ["GAZIANTEP"] = 27, ["GIRESUN"] = 28, ["GÜMÜŞHANE"] = 29, ["HAKKARI"] = 30, ["HATAY"] = 31, ["ISPARTA"] = 32, ["MERSIN"] = 33, ["İSTANBUL"] = 34, ["ISTANBUL"] = 34,
        ["İZMIR"] = 35, ["IZMIR"] = 35, ["KARS"] = 36, ["KASTAMONU"] = 37, ["KAYSERI"] = 38, ["KIRKLARELI"] = 39, ["KIRŞEHIR"] = 40, ["KIRSEHIR"] = 40, ["KOCAELI"] = 41, ["KONYA"] = 42,
        ["KÜTAHYA"] = 43, ["MALATYA"] = 44, ["MANISA"] = 45, ["KAHRAMANMARAŞ"] = 46, ["MARDIN"] = 47, ["MUĞLA"] = 48, ["MUŞ"] = 49, ["NEVŞEHIR"] = 50,
        ["NIĞDE"] = 51, ["NIGDE"] = 51, ["ORDU"] = 52, ["RIZE"] = 53, ["SAKARYA"] = 54, ["SAMSUN"] = 55, ["SIIRT"] = 56, ["SINOP"] = 57, ["SIVAS"] = 58, ["TEKIRDAĞ"] = 59, ["TEKIRDAG"] = 59,
        ["TOKAT"] = 60, ["TRABZON"] = 61, ["TUNCELI"] = 62, ["ŞANLIURFA"] = 63, ["SANLIURFA"] = 63, ["UŞAK"] = 64, ["USAK"] = 64, ["VAN"] = 65, ["YOZGAT"] = 66, ["ZONGULDAK"] = 67,
        ["AKSARAY"] = 68, ["BAYBURT"] = 69, ["KARAMAN"] = 70, ["KIRIKKALE"] = 71, ["BATMAN"] = 72, ["ŞIRNAK"] = 73, ["BARTIN"] = 74, ["ARDAHAN"] = 75,
        ["IĞDIR"] = 76, ["YALOVA"] = 77, ["KARABÜK"] = 78, ["KILIS"] = 79, ["OSMANIYE"] = 80, ["DÜZCE"] = 81
    };

    private static string BuildUblInvoiceId(string sourceInvoiceNo, DateTime issueDate, Guid invoiceId)
    {
        var prefix = new string((sourceInvoiceNo ?? "AUR")
            .Where(char.IsLetter)
            .Take(3)
            .ToArray())
            .ToUpperInvariant();
        if (prefix.Length < 3)
            prefix = (prefix + "AUR")[..3];

        var raw = BitConverter.ToUInt32(invoiceId.ToByteArray(), 0) % 1_000_000_000;
        var serial9 = raw.ToString("000000000", CultureInfo.InvariantCulture);
        return $"{prefix}{issueDate:yyyy}{serial9}";
    }

    private decimal ResolveGoldLineBaseAmount(
        decimal quantity,
        string? karat,
        decimal fallbackLineExtension,
        IReadOnlyDictionary<string, decimal> adjustedSellRates)
    {
        var gramPrice = _rates.GetKaratGramSellPrice(karat, _goldReferenceSource, adjustedSellRates);
        if (gramPrice <= 0m || quantity <= 0m)
            return Math.Max(0m, fallbackLineExtension);
        return Math.Round(quantity * gramPrice, 2, MidpointRounding.AwayFromZero);
    }

    private string ResolveInvoiceTypeCode(IReadOnlyList<UblLine> lines)
    {
        if (lines.Count == 0)
            return "SATIS";

        var hasGoldOrZiynet = lines.Any(IsGoldOrZiynetLine);
        var hasSpecialOnly = lines.All(IsSpecialProductLine);

        if (hasSpecialOnly)
            return "SATIS";
        if (hasGoldOrZiynet)
            return "OZELMATRAH";

        // Gümüş ve diğer normal ürün akışları varsayılan SATIŞ olmalıdır.
        return "SATIS";
    }

    private static bool IsSpecialProductLine(UblLine line)
    {
        var type = GetProp(line, "ÜRÜN TİPİ");
        if (Contains(type, "ÖZEL ÜRÜN")) return true;
        var model = GetProp(line, "FATURA MODELİ");
        if (Contains(model, "ÖZEL ÜRÜN")) return true;
        var category = GetProp(line, "ÜRÜN KATEGORİSİ");
        // "ÖZEL MATRAH" kategori metni, özel ürün değil altın matrah senaryosudur.
        if (Contains(category, "ÖZEL MATRAH") || Contains(category, "OZEL MATRAH"))
            return false;
        if (Contains(category, "ÖZEL") || Contains(category, "OZEL"))
            return true;
        return Contains(line.Description, "SAAT");
    }

    private static bool IsGoldOrZiynetLine(UblLine line)
    {
        if (!string.IsNullOrWhiteSpace(GetProp(line, "ÖZEL MATRAH")))
            return true;

        var category = GetProp(line, "ÜRÜN KATEGORİSİ");
        if (Contains(category, "MATRAH"))
            return true;

        var ayar = GetProp(line, "AYAR");
        if (!string.IsNullOrWhiteSpace(ayar) && ayar != "-")
            return true;

        var hasEquivalentRaw = GetProp(line, "HAS ALTIN KARŞILIĞI");
        if (decimal.TryParse(
                (hasEquivalentRaw ?? string.Empty).Replace(",", "."),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var hasEquivalent) &&
            hasEquivalent > 0m)
            return true;

        var type = GetProp(line, "ÜRÜN TİPİ");
        if (Contains(type, "ZİYNET") || Contains(type, "ZIYNET") || Contains(type, "ALTIN") || Contains(type, "AYAR"))
            return true;
        if (Contains(category, "ZİYNET") || Contains(category, "ZIYNET") || Contains(category, "ALTIN"))
            return true;
        return Contains(line.Description, "ALTIN")
            || Contains(line.Description, "ÇEYREK")
            || Contains(line.Description, "CEYREK")
            || Contains(line.Description, "YARIM")
            || Contains(line.Description, "TAM")
            || Contains(line.Description, "ATA");
    }

    private static string GetProp(UblLine line, string name)
    {
        var p = line.AdditionalProperties.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return p?.Value ?? string.Empty;
    }

    private static bool Contains(string? source, string token)
        => !string.IsNullOrWhiteSpace(source) && source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string ResolveZiynetDisplayName(string? itemName, string? ziynetTip)
    {
        var value = !string.IsNullOrWhiteSpace(ziynetTip) ? ziynetTip : itemName;
        return string.IsNullOrWhiteSpace(value) ? "Ziynet Altın" : value.Trim();
    }

    private static decimal ResolveZiynetUnitGram(string? ziynetName, string? olcu, string? malTanim)
    {
        if (TryReadDecimal(olcu, out var fromOlcu) && fromOlcu > 0m)
            return fromOlcu;
        if (TryReadDecimal(malTanim, out var fromMal) && fromMal > 0m)
            return fromMal;

        var text = (ziynetName ?? string.Empty).ToUpperInvariant();
        if (text.Contains("ÇEYREK") || text.Contains("CEYREK")) return 1.75m;
        if (text.Contains("YARIM")) return 3.50m;
        if (text.Contains("TAM")) return 7.00m;
        if (text.Contains("ATA")) return 7.20m;
        if (text.Contains("GRAM")) return 1.00m;
        return 1.00m;
    }

    private static bool TryReadDecimal(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = new string(text.Where(ch => char.IsDigit(ch) || ch == ',' || ch == '.').ToArray());
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        normalized = normalized.Replace(",", ".");
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string NormalizeInvoiceTypeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "SATIS";

        var normalized = raw.Trim().ToUpperInvariant();
        return normalized switch
        {
            "SATIS" => "SATIS",
            "IADE" => "IADE",
            "TEVKIFAT" => "TEVKIFAT",
            "ISTISNA" => "ISTISNA",
            "OZELMATRAH" => "OZELMATRAH",
            _ => "SATIS"
        };
    }

    private sealed record UblLine(
        int LineNo,
        string Description,
        decimal Quantity,
        decimal UnitPrice,
        decimal Discount,
        string Currency,
        decimal TaxRate,
        decimal LineExtensionAmount,
        decimal TaxAmount,
        string ExemptionCode,
        string ExemptionReason,
        string ProductCode,
        IReadOnlyList<UblAdditionalProperty> AdditionalProperties,
        decimal PayableAmount,
        decimal WithholdingRate,
        decimal WithholdingTaxAmount,
        decimal HasEquivalentAmount,
        string UnitCode = "C62"
    )
    {
        public decimal TaxExclusiveAmount => LineExtensionAmount;
    }

    private sealed record UblAdditionalProperty(string Name, string Value);
}

internal static class InvoiceUblExtensions
{
    public static string CurrencyOrDefault(this Invoice _)
    {
        return "TRY";
    }
}
