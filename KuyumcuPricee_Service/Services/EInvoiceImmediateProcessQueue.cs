using System.Collections.Concurrent;

namespace KUYUMCU.Price_Service.Services;

public sealed class EInvoiceImmediateProcessQueue
{
    private readonly ConcurrentQueue<(Guid TenantId, Guid InvoiceId)> _queue = new();

    public void Enqueue(Guid tenantId, Guid invoiceId)
        => _queue.Enqueue((tenantId, invoiceId));

    public bool TryDequeue(out (Guid TenantId, Guid InvoiceId) job)
        => _queue.TryDequeue(out job);
}
