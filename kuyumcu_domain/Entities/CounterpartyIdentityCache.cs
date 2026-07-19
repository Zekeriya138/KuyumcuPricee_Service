namespace kuyumcu_domain.Entities;

/// <summary>
/// Banka hareketlerinden öğrenilen karşı taraf kimlik önbelleği (tüm işletmeler arası).
/// IBAN, isim ve TCKN/VKN eşleştirmesi için kullanılır.
/// </summary>
public class CounterpartyIdentityCache : Entity
{
    public string? NormalizedIban { get; set; }
    public string NormalizedName { get; set; } = "";
    public string TaxNo { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>Customer, Supplier, BankImport, Edm, NihaiTuketici</summary>
    public string Source { get; set; } = "";
    public Guid? LinkedCustomerId { get; set; }
    public Guid? LinkedSupplierId { get; set; }
    public Guid? LearnedByTenantId { get; set; }
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}

public static class CounterpartyIdentitySources
{
    public const string Customer = "Customer";
    public const string Supplier = "Supplier";
    public const string BankImport = "BankImport";
    public const string Edm = "Edm";
    public const string IdentityCache = "IdentityCache";
    public const string NihaiTuketici = "NihaiTuketici";
    public const string Description = "Description";
}
