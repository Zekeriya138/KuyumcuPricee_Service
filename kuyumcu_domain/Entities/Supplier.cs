using kuyumcu_domain.Enums;

namespace kuyumcu_domain.Entities;

/// <summary>Tedarikçi (Toptancı, Atölye, Hurda vb.) - kuyumculuk sektörüne özel.</summary>
public class Supplier : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid? BranchId { get; set; }

    // === Temel Bilgiler ===
    public string SupplierCode { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string? ContactName { get; set; }
    public SupplierType SupplierType { get; set; } = SupplierType.Wholesaler;
    public string? Phone { get; set; }
    public string? Whatsapp { get; set; }
    public string? Email { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? Address { get; set; }
    public string? TaxOffice { get; set; }
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }

    // === Finansal ===
    public decimal CurrentDebt { get; set; }
    public decimal CurrentCredit { get; set; }
    public decimal Balance { get; set; }
    public SupplierPaymentType DefaultPaymentType { get; set; } = SupplierPaymentType.Cash;
    public string? BankName { get; set; }
    public string? IBAN { get; set; }
    public int PaymentTermDays { get; set; }
    public SupplierCurrencyType CurrencyType { get; set; } = SupplierCurrencyType.TRY;
    public decimal RiskLimit { get; set; }

    // === Kuyumcuya Özel ===
    public string? ProductCategoriesWorkedWith { get; set; }  // virgülle ayrılmış: Yüzük, Kolye, Bilezik
    public string? KaratTypes { get; set; }                   // virgülle ayrılmış: 22K, 24K, 18K, 14K
    public SupplierPricingType PricingType { get; set; } = SupplierPricingType.GramPlusLabor;
    public decimal DefaultLaborCostPerGram { get; set; }
    public decimal FireRate { get; set; }                      // fire oranı (%)
    public bool WorksOnConsignment { get; set; }
    public bool AllowsManufacturing { get; set; }
    public bool IsActive { get; set; } = true;

    public DateTime? UpdatedAt { get; set; }

    /// <summary>Geriye uyumluluk: Eski tabloda Name sütunu. Yeni kayıtlarda CompanyName ile doldurulur.</summary>
    public string Name { get; set; } = "";

    public Branch? Branch { get; set; }
}
