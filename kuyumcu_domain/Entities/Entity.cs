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
    public string? NationalId { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid BranchId { get; set; }
    public Branch Branch { get; set; } = null!;
}

public class Customer : Entity
{
    public Guid TenantId { get; set; }
    public string FullName { get; set; } = null!;
    public string? NationalId { get; set; }
    public DateTime? BirthDate { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? Address { get; set; }
}
public class Sale : Entity
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public Guid UserId { get; set; }
    public Guid? CustomerId { get; set; }

    // Ürün detayları kaldırıldı. Bu detaylar artık sadece SaleItem içinde yer alacak.

    public User User { get; set; } = null!;
    public Branch Branch { get; set; } = null!;
    public Customer? Customer { get; set; }

    // ÖNEMLİ: null referans hatası olmaması için başlat
    public List<SaleItem> Items { get; set; } = new();
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
