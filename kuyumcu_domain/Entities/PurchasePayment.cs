using System;
using kuyumcu_domain.Enums;

namespace kuyumcu_domain.Entities;

/// <summary>Alış faturasına bağlı ödeme satırı (nakit, havale, veresiye, hurda altın vb.).</summary>
public class PurchasePayment : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid PurchaseId { get; set; }
    public PurchasePaymentType PaymentType { get; set; }

    /// <summary>TL tutarı (hurda için gram × fiyat).</summary>
    public decimal Amount { get; set; }

    public decimal? GoldWeight { get; set; }
    public string? GoldKarat { get; set; }
    public decimal? GoldPrice { get; set; }

    public string? BankName { get; set; }
    public string? IBAN { get; set; }
    public DateTime? DueDate { get; set; }
    public string? CashAccount { get; set; }
    public string? UnitCode { get; set; } // TL, USD, EUR, HAS, GUMUS
    public decimal? UnitAmount { get; set; } // UnitCode cinsinden miktar
    public decimal? SilverWeight { get; set; } // Geriye uyumluluk/rapor amaçlı

    /// <summary>0 = DepoStok (hammadde depo), 1 = ScrapStock (müşteri hurdası).</summary>
    public int GoldSource { get; set; }

    public Purchase Purchase { get; set; } = null!;
}
