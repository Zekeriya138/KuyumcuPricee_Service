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

    private Guid BranchIdOrThrow()
    {
        var id = _tenant.BranchId ?? Guid.Empty;
        if (id == Guid.Empty)
            throw new InvalidOperationException("BranchId missing (JWT branch_id veya X-Branch-Id).");
        return id;
    }

    public async Task<Customer> CreateAsync(Customer c, CancellationToken ct = default)
    {
        // her yeni müşteri mutlaka aktif tenant’a yazılsın
        c.TenantId = TenantIdOrThrow();
        c.BranchId = BranchIdOrThrow();

        _db.Customers.Add(c);
        await _db.SaveChangesAsync(ct);
        var bal = new CustomerBalance
        {
            TenantId = c.TenantId,
            CustomerId = c.Id,
            BalanceTL = 0m,
            BalanceUSD = 0m,
            BalanceEUR = 0m,
            BalanceHAS = 0m,
            UpdatedAt = DateTime.UtcNow
        };
        _db.CustomerBalances.Add(bal);
        await _db.SaveChangesAsync(ct);
        return c;
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();
        var bid = BranchIdOrThrow();

        return _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && x.BranchId == bid, ct);
    }

    public async Task<IReadOnlyList<Customer>> ListAsync(string? q, int? cariTip = null, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();
        var bid = BranchIdOrThrow();

        var query = _db.Customers
            .AsNoTracking()
            .Where(x => x.TenantId == tid && x.BranchId == bid && !x.IsDeleted)
            .AsQueryable();

        if (cariTip.HasValue)
            query = query.Where(x => x.CariTip == cariTip.Value);

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
        var bid = BranchIdOrThrow();

        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && x.BranchId == bid, ct);
        if (c is null) return false;

        c.FullName = input.FullName;
        c.CariTip = input.CariTip;
        c.NationalId = input.NationalId;
        c.BirthDate = input.BirthDate;
        c.Phone = input.Phone;
        c.Email = input.Email;
        c.City = input.City;
        c.District = input.District;
        c.Address = input.Address;
        c.Note = input.Note;
        c.TedarikciExtJson = input.TedarikciExtJson;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tid = TenantIdOrThrow();
        var bid = BranchIdOrThrow();

        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid && x.BranchId == bid, ct);
        if (c is null) return false;

        c.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
