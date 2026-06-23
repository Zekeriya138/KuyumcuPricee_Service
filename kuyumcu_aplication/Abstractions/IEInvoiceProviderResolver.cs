namespace kuyumcu_application.Abstractions;

public interface IEInvoiceProviderResolver
{
    IEInvoiceProviderAdapter Resolve(string providerCode);
}
