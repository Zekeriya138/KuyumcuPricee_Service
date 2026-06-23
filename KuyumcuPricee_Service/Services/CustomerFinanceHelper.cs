using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public static class CustomerFinanceHelper
{
    public static async Task<CustomerBalance> GetOrCreateBalanceAsync(
        AppDbContext db, Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var bal = await db.CustomerBalances
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CustomerId == customerId && !x.IsDeleted, ct);
        if (bal is not null) return bal;

        bal = new CustomerBalance
        {
            TenantId = tenantId,
            CustomerId = customerId
        };
        db.CustomerBalances.Add(bal);
        return bal;
    }

    public static async Task AddTransactionAsync(
        AppDbContext db,
        Guid tenantId,
        Guid customerId,
        Guid? branchId,
        string groupCode,
        string itemName,
        string? itemType,
        decimal quantity,
        int direction,
        decimal? gram,
        string? ayar,
        decimal? milyem,
        decimal? hasEq,
        decimal? unitPriceTl,
        decimal? totalPriceTl,
        string? refType,
        Guid? refId,
        string? note,
        DateTime? txDate,
        CancellationToken ct,
        string? cariDurumOverride = null)
    {
        var tx = new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = (groupCode ?? "").Trim().ToUpperInvariant(),
            ItemName = (itemName ?? "").Trim().ToUpperInvariant(),
            ItemType = string.IsNullOrWhiteSpace(itemType) ? null : itemType.Trim(),
            Quantity = quantity,
            Direction = direction >= 0 ? 1 : -1,
            Gram = gram,
            Ayar = ayar,
            Milyem = milyem,
            HasEquivalent = hasEq,
            UnitPriceTl = unitPriceTl,
            TotalPriceTl = totalPriceTl,
            CariDurum = string.IsNullOrWhiteSpace(cariDurumOverride)
                ? (direction >= 0 ? "Alacakli" : "Borclu")
                : cariDurumOverride.Trim(),
            RefType = refType,
            RefId = refId,
            Note = note,
            TxDate = txDate ?? DateTime.UtcNow
        };
        db.CustomerTransactions.Add(tx);
        await Task.CompletedTask;
    }

    public static decimal MilyemFromAyar(string? ayar)
    {
        var a = (ayar ?? "").Trim().ToUpperInvariant();
        if (a.Contains("22") || a.Contains("916")) return 0.916m;
        if (a.Contains("24") || a.Contains("999")) return 1.000m;
        if (a.Contains("18") || a.Contains("750")) return 0.750m;
        if (a.Contains("14") || a.Contains("585")) return 0.585m;
        return 0.000m;
    }
}
