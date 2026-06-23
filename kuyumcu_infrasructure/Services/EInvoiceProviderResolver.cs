using kuyumcu_application.Abstractions;

namespace kuyumcu_infrastructure.Services;

public sealed class EInvoiceProviderResolver : IEInvoiceProviderResolver
{
    private readonly IReadOnlyDictionary<string, IEInvoiceProviderAdapter> _adapters;

    public EInvoiceProviderResolver(IEnumerable<IEInvoiceProviderAdapter> adapters)
    {
        _adapters = adapters
            .GroupBy(x => (x.ProviderCode ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IEInvoiceProviderAdapter Resolve(string providerCode)
    {
        var key = (providerCode ?? string.Empty).Trim();
        if (_adapters.TryGetValue(key, out var adapter))
            return adapter;

        throw new InvalidOperationException($"E-invoice provider adapter not found: '{providerCode}'.");
    }
}
