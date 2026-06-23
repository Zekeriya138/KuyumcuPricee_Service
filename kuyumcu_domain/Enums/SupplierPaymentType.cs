namespace kuyumcu_domain.Enums;

/// <summary>Tedarikçi varsayılan ödeme türü.</summary>
public enum SupplierPaymentType
{
    Cash = 0,        // Nakit
    BankTransfer = 1, // Havale/EFT
    Gold = 2,       // Altın
    Check = 3       // Çek
}
