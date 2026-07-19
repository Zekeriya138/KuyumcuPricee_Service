namespace kuyumcu_domain.Entities;

/// <summary>Vomsis vb. banka entegrasyonlarından gelen ham hareket kaydı.</summary>
public class BankImportTransaction : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>Örn. Vomsis</summary>
    public string Provider { get; set; } = "Vomsis";

    /// <summary>Sağlayıcıdaki hareket id (Vomsis id).</summary>
    public long ExternalId { get; set; }

    /// <summary>Sağlayıcı benzersiz anahtarı (Vomsis key) — idempotent import için.</summary>
    public string ExternalKey { get; set; } = "";

    public int? VomsisAccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public string TransactionType { get; set; } = "";
    public string? Description { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyTaxNo { get; set; }
    public string? CounterpartyIban { get; set; }
    public DateTime TransactionDateUtc { get; set; }

    /// <summary>Pending, DraftCreated, MissingTaxId, NoCustomerMatch, Rejected, Skipped</summary>
    public string Status { get; set; } = BankImportStatuses.Pending;

    public string? StatusMessage { get; set; }
    public Guid? MatchedCustomerId { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? EInvoiceDocumentId { get; set; }
}

public static class BankImportStatuses
{
    public const string Pending = "Pending";
    public const string DraftCreated = "DraftCreated";
    public const string MissingTaxId = "MissingTaxId";
    public const string NoCustomerMatch = "NoCustomerMatch";
    public const string Rejected = "Rejected";
    public const string Skipped = "Skipped";
}

/// <summary>Şube bazlı Vomsis banka sync worker ayarları (WPF'ten yönetilir).</summary>
public class BankSyncProfile : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid BranchId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? VomsisAppKey { get; set; }
    public string? VomsisAppSecret { get; set; }
    public string ErpApiBaseUrl { get; set; } = "";
    public string? ErpApiAppKey { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
    /// <summary>Virgülle ayrılmış Vomsis hesap id listesi (örn. 46).</summary>
    public string AllowedAccountIds { get; set; } = "46";
    public int LookbackDays { get; set; } = 7;
}
