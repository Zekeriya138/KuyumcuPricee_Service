using kuyumcu_domain.Entities;

namespace kuyumcu_application.Abstractions;

public interface ICustomerService
{
    Task<Customer> CreateAsync(Customer c, CancellationToken ct = default);
    Task<Customer?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> ListAsync(string? q, CancellationToken ct = default);
    Task<bool> UpdateAsync(Guid id, Customer input, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default); // soft delete
}
