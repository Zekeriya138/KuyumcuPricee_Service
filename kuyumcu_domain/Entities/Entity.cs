namespace kuyumcu_domain.Entities;

public abstract class Entity
{
   
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public class Branch : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public ICollection<User> Users { get; set; } = new List<User>();
    public string Name { get; set; } = "";      // zorunlu
    public string? Code { get; set; }           // şube kodu (opsiyonel, benzersiz yapmak istersen migration’da index ekleriz)
    public string? City { get; set; }           // opsiyonel
    public string? Address { get; set; }        // <<< eklendi
    public string? Phone { get; set; }          // <<< eklendi
    public string? Email { get; set; }          // opsiyonel
    public bool IsActive { get; set; } = true;  // aktif/pasif
    public Tenant Tenant { get; set; } = null!;
                                      
}

public class User : Entity
{
    public Guid? DefaultBranchId {get; set;}
    public Guid TenantId { get; set; }
    public string Username { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string Role { get; set; } = "User";
    public bool IsActive { get; set; } = true;
    public string? NationalId { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool CanManageUsers { get; set; }
    public bool CanManageBranches { get; set; }
    public bool CanSwitchBranches { get; set; }
    public bool CanUseEInvoice { get; set; }
    public bool CanUseEArchive { get; set; }
    public bool CanUseExpenseSlip { get; set; }
    public bool CanManageRates { get; set; }
    public bool CanViewBalanceSheet { get; set; }
    public bool CanAccessCustomers { get; set; }
    public bool CanAccessSuppliers { get; set; }
    public bool CanAccessPurchase { get; set; }
    public bool CanAccessSales { get; set; }
    public bool CanCreateIncomeExpense { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;
}

public class Customer : Entity
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    /// <summary>0=Müşteri, 1=Tedarikçi. Cari yönetiminde filtreleme için.</summary>
    public int CariTip { get; set; } = 0; // 0=Müşteri, 1=Tedarikçi
    public string FullName { get; set; } = null!;
    public string? NationalId { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? Address { get; set; }
    /// <summary>Genel not alanı.</summary>
    public string? Note { get; set; }
    /// <summary>Tedarikçiye özel genişletilmiş alanlar (JSON: yetkiliKisi, whatsapp, vergiDairesi, vb.).</summary>
    public string? TedarikciExtJson { get; set; }
    public Branch Branch { get; set; } = null!;
}
public class Sale : Entity
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public Guid? CustomerId { get; set; }

    /// <summary>Nakit, Kart, Iban, Takas, Veresiye</summary>
    public string PaymentType { get; set; } = "Nakit";

    /// <summary>Teslimat tipi: "Teslim" (teslim edildi) veya "EMANET" (teslim edilmedi). Boş/null = teslim edildi.</summary>
    public string? DeliveryType { get; set; }

    public User User { get; set; } = null!;
    public Branch Branch { get; set; } = null!;
    public Customer? Customer { get; set; }

    public List<SaleItem> Items { get; set; } = new();
    public List<SalePayment> Payments { get; set; } = new();
}

public class Invoice : Entity
{
    public Guid TenantId { get; set; }
    public Guid? SaleId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? CustomerId { get; set; }
    public DateTime InvoiceDate { get; set; } = DateTime.UtcNow;
    public decimal GrandTotal { get; set; }
    public string PaymentType { get; set; } = "IBAN";
    /// <summary>
    /// Çoklu ödeme bölünmesinde bu faturanın toplam satış tutarına oranı (0 &lt; ratio ≤ 1).
    /// Tek ödeme / tam fatura için varsayılan değer 1.0'dır.
    /// </summary>
    public decimal PaymentSplitRatio { get; set; } = 1.0m;
    public bool IsExported { get; set; }
    /// <summary>
    /// Satışa bağlı olmayan (tahsilat kaynaklı) taslak faturalar için alıcı + has altın kalem bilgisi (JSON).
    /// Doluysa fatura, satış kalemleri yerine bu meta üzerinden (has altın tek kalem) oluşturulur.
    /// </summary>
    public string? CollectionMetaJson { get; set; }

    public Sale? Sale { get; set; }
    public Branch Branch { get; set; } = null!;
    public Customer? Customer { get; set; }
}
//public class Sale : Entity
//{
//    public Guid TenantId { get; set; }
//    public Guid BranchId { get; set; }
//    public Guid UserId { get; set; }
//    public Guid? CustomerId { get; set; }

//    public string ProductCode { get; set; } = "";
//    public string ProductName { get; set; } = "";
//    public string Karat { get; set; } = "";
//    public decimal Quantity { get; set; }
//    public decimal UnitPrice { get; set; }
//    public decimal TotalPrice { get; set; }

//    public User User { get; set; } = null!;
//    public Branch Branch { get; set; } = null!;
//    public Customer? Customer { get; set; }

//    // ÖNEMLİ: null referans hatası olmaması için başlat
//    public List<SaleItem> Items { get; set; } = new();
//}



//public class Sale : Entity
//{
//    public Guid BranchId { get; set; }
//    public Guid UserId { get; set; }
//    public Guid? CustomerId { get; set; }

//    public string ProductCode { get; set; } = "";   // ileride stok kodu
//    public string ProductName { get; set; } = "";   // kullanıcıya özel ad
//    public string Karat { get; set; } = "";         // 14K/22K/24K...
//    public decimal Quantity { get; set; }           // gram / adet
//    public decimal UnitPrice { get; set; }
//    public decimal TotalPrice { get; set; }

//    // Fiş/Fatura bilgileri
//    public User User { get; set; } = null!;
//    public Branch Branch { get; set; } = null!;
//    public Customer? Customer { get; set; }

//    public List<SaleItem> Items { get; set; }
//}
