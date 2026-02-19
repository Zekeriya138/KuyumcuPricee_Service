using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace kuyumcu_infrastructure.Services;

public class CustomerService : ICustomerService
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public CustomerService(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    private Guid TenantIdOrThrow()
    {
        var id = _tenant.TenantId;
        if (id == Guid.Empty)
            throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
        return id;
    }

    public async Task<Customer> CreateAsync(Customer c, CancellationToken ct = default)
    {
        // her yeni müşteri mutlaka aktif tenant’a yazılsın
        c.TenantId = TenantIdOrThrow();

        _db.Customers.Add(c);
        await _db.SaveChangesAsync(ct);
        return c;
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();

        return _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
    }

    public async Task<IReadOnlyList<Customer>> ListAsync(string? q, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();

        var query = _db.Customers
            .AsNoTracking()
            .Where(x => x.TenantId == tid && !x.IsDeleted)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            q = q.Trim();
            query = query.Where(x =>
                x.FullName.Contains(q) ||
                (x.NationalId ?? "").Contains(q) ||
                (x.Phone ?? "").Contains(q));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(500)
            .ToListAsync(ct);
    }

    public async Task<bool> UpdateAsync(Guid id, Customer input, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();

        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (c is null) return false;

        c.FullName = input.FullName;
        c.NationalId = input.NationalId;
        c.BirthDate = input.BirthDate;
        c.Phone = input.Phone;
        c.Email = input.Email;
        c.City = input.City;
        c.District = input.District;
        c.Address = input.Address;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();

        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid, ct);
        if (c is null) return false;

        c.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
