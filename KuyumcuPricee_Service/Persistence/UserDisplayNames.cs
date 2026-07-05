using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Persistence;

/// <summary>
/// İşlemi yapan kullanıcıların görünen adlarını (Ad Soyad, yoksa kullanıcı adı) çözer.
/// Silinmiş kullanıcılar da geçmiş işlemlerde gösterilebilsin diye query filter yok sayılır.
/// </summary>
public static class UserDisplayNames
{
    public static async Task<Dictionary<Guid, string>> BuildUserNameMapAsync(
        AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        var ids = userIds.Where(x => x != Guid.Empty).Distinct().ToHashSet();
        if (ids.Count == 0) return new Dictionary<Guid, string>();

        // Not: sorgu içinde ids.Contains(...) kullanılırsa EF Core, eski uyumluluk seviyesindeki
        // SQL Server'larda desteklenmeyen "OPENJSON ... WITH" üretir. Kiracı kullanıcı sayısı az
        // olduğundan kullanıcıları belleğe alıp filtreliyoruz.
        var rows = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId)
            .Select(u => new { u.Id, u.FullName, u.Username })
            .ToListAsync(ct);

        return rows
            .Where(u => ids.Contains(u.Id))
            .ToDictionary(
                u => u.Id,
                u => string.IsNullOrWhiteSpace(u.FullName) ? (u.Username ?? "") : u.FullName!);
    }
}
