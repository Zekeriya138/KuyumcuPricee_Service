using System.Security.Claims;
using kuyumcu_domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KUYUMCU.Price_Service.Persistence;

/// <summary>
/// Cari ve kasa hareketlerine (CustomerTransaction / SupplierTransaction / CashTransaction)
/// işlemi yapan kullanıcıyı (UserId + görünen ad) otomatik damgalar.
/// JWT'den okur; HTTP bağlamı yoksa (arka plan işleri) hiçbir şey yazmaz.
/// </summary>
public sealed class UserStampInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _http;

    public UserStampInterceptor(IHttpContextAccessor http) => _http = http;

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? ctx)
    {
        if (ctx is null) return;
        var (userId, displayName) = ResolveUser();
        if (userId is null && string.IsNullOrEmpty(displayName)) return;

        foreach (var entry in ctx.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;
            switch (entry.Entity)
            {
                case CustomerTransaction c:
                    c.UserId ??= userId;
                    if (string.IsNullOrEmpty(c.KullaniciAdi)) c.KullaniciAdi = displayName;
                    break;
                case SupplierTransaction s:
                    s.UserId ??= userId;
                    if (string.IsNullOrEmpty(s.KullaniciAdi)) s.KullaniciAdi = displayName;
                    break;
                case CashTransaction k:
                    k.UserId ??= userId;
                    if (string.IsNullOrEmpty(k.KullaniciAdi)) k.KullaniciAdi = displayName;
                    break;
            }
        }
    }

    private (Guid? userId, string? displayName) ResolveUser()
    {
        var user = _http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return (null, null);

        Guid? id = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : null;
        var full = user.FindFirstValue("full_name");
        var name = !string.IsNullOrWhiteSpace(full) ? full : user.FindFirstValue(ClaimTypes.Name);
        return (id, string.IsNullOrWhiteSpace(name) ? null : name);
    }
}
