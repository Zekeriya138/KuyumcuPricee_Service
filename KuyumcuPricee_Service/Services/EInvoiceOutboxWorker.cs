using System.Text.Json;
using System.Collections.Concurrent;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KUYUMCU.Price_Service.Services;

public sealed class EInvoiceOutboxWorker : BackgroundService
{
    private static readonly ConcurrentDictionary<Guid, DateTime> StatusPollCache = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> ExpenseStatusPollCache = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EInvoiceOutboxWorker> _logger;
    private readonly IConfiguration _cfg;

    public EInvoiceOutboxWorker(IServiceScopeFactory scopeFactory, ILogger<EInvoiceOutboxWorker> logger, IConfiguration cfg)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
                await PollQueuedStatusesAsync(stoppingToken);
                await ProcessExpenseSlipBatchAsync(stoppingToken);
                await PollExpenseSlipStatusesAsync(stoppingToken);
                await SyncIncomingInvoicesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "E-invoice outbox worker failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providerResolver = scope.ServiceProvider.GetRequiredService<IEInvoiceProviderResolver>();

        var now = DateTime.UtcNow;
        var lockTimeoutSeconds = Math.Clamp(_cfg.GetValue<int?>("EInvoice:Outbox:LockTimeoutSeconds") ?? 180, 30, 3600);
        var staleLockBefore = now.AddSeconds(-lockTimeoutSeconds);
        var items = await db.EInvoiceOutboxes
            .IgnoreQueryFilters()
            .Where(x => !x.IsDeleted &&
                        x.Status == "Pending" &&
                        x.NextAttemptAt <= now &&
                        (x.LockedAt == null || x.LockedAt < staleLockBefore))
            .OrderBy(x => x.NextAttemptAt)
            .Take(25)
            .ToListAsync(ct);

        foreach (var item in items)
        {
            item.LockedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        foreach (var outbox in items)
        {
            await ProcessItemAsync(db, providerResolver, outbox, ct);
        }
    }

    private async Task PollQueuedStatusesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providerResolver = scope.ServiceProvider.GetRequiredService<IEInvoiceProviderResolver>();
        var now = DateTime.UtcNow;

        var options = StatusPollingOptions.FromConfiguration(_cfg);
        var cutoff = now.AddMinutes(-Math.Max(1, options.PendingTimeoutMinutes));

        var docs = await db.EInvoiceDocuments
            .IgnoreQueryFilters()
            .Where(x =>
                !x.IsDeleted &&
                x.Direction == "Outgoing" &&
                (x.Status == "Queued" || x.Status == "Sent") &&
                x.SubmittedAt != null)
            .OrderBy(x => x.SubmittedAt)
            .Take(options.BatchSize)
            .ToListAsync(ct);

        foreach (var doc in docs)
        {
            if (!ShouldPoll(doc.Id, now, options.PollIntervalSeconds))
                continue;

            try
            {
                if (doc.SubmittedAt.HasValue && doc.SubmittedAt.Value < cutoff)
                {
                    doc.Status = "Failed";
                    doc.LastError = $"Durum zaman aşımı: {options.PendingTimeoutMinutes} dakika içinde nihai yanıt alınamadı.";
                    doc.RetryCount = Math.Max(doc.RetryCount, options.MaxStatusRetryCount);
                    StatusPollCache.TryRemove(doc.Id, out _);
                    continue;
                }

                var profile = await db.EInvoiceProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId, ct);
                var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
                var adapter = providerResolver.Resolve(providerCode);

                var integratorRef = string.IsNullOrWhiteSpace(doc.IntegratorDocumentId)
                    ? (doc.Uuid ?? string.Empty)
                    : doc.IntegratorDocumentId!;
                if (string.IsNullOrWhiteSpace(integratorRef) && string.IsNullOrWhiteSpace(doc.Uuid))
                {
                    doc.RetryCount++;
                    doc.LastError = "Durum sorgu atlandı: integratorDocumentId/UUID bulunamadı.";
                    if (doc.RetryCount >= options.MaxStatusRetryCount)
                    {
                        doc.Status = "Failed";
                        StatusPollCache.TryRemove(doc.Id, out _);
                    }
                    continue;
                }

                var statusRes = await adapter.GetStatusAsync(new EInvoiceStatusRequest(
                    doc.TenantId,
                    doc.BranchId,
                    doc.Id,
                    integratorRef,
                    doc.Uuid,
                    profile?.IntegratorUsername,
                    profile?.IntegratorSecretRef), ct);

                if (!statusRes.IsSuccess)
                {
                    doc.RetryCount++;
                    doc.LastError = statusRes.ErrorMessage ?? "Durum sorgusu başarısız.";
                    if (doc.RetryCount >= options.MaxStatusRetryCount)
                    {
                        doc.Status = "Failed";
                        StatusPollCache.TryRemove(doc.Id, out _);
                    }
                    continue;
                }

                var nextStatus = EInvoiceWorkflowService.NormalizeStatus(statusRes.ProviderStatus, doc.Status);
                doc.Status = nextStatus;
                doc.LastError = null;
                doc.RawLastResponse = statusRes.RawResponse ?? doc.RawLastResponse;
                doc.RetryCount = 0;

                if (nextStatus == "Delivered")
                    doc.DeliveredAt ??= DateTime.UtcNow;
                if (nextStatus == "Cancelled")
                    doc.CancelledAt ??= DateTime.UtcNow;

                if (nextStatus == "Sent" || nextStatus == "Delivered")
                {
                    var invoice = await db.Invoices
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.Id == doc.InvoiceId, ct);
                    if (invoice is not null)
                        invoice.IsExported = true;
                }

                if (IsFinalStatus(nextStatus))
                    StatusPollCache.TryRemove(doc.Id, out _);
            }
            catch (Exception ex)
            {
                doc.RetryCount++;
                doc.LastError = $"Durum sorgu hatası: {ex.Message}";
                if (doc.RetryCount >= options.MaxStatusRetryCount)
                {
                    doc.Status = "Failed";
                    StatusPollCache.TryRemove(doc.Id, out _);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task ProcessItemAsync(
        AppDbContext db,
        IEInvoiceProviderResolver providerResolver,
        EInvoiceOutbox outbox,
        CancellationToken ct)
    {
        var doc = await db.EInvoiceDocuments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == outbox.TenantId && x.Id == outbox.DocumentId, ct);
        if (doc is null)
        {
            outbox.Status = "Done";
            outbox.ProcessedAt = DateTime.UtcNow;
            outbox.LockedAt = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var profile = await db.EInvoiceProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId, ct);

            var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
            var adapter = providerResolver.Resolve(providerCode);

            var payload = ParsePayload(outbox.PayloadJson);
            var sendReq = new EInvoiceSendRequest(
                doc.TenantId,
                doc.BranchId,
                doc.Id,
                doc.DocumentType,
                doc.InvoiceNumber,
                doc.CreatedAt,
                doc.GrandTotal,
                doc.Currency,
                payload.BuyerName,
                payload.BuyerTaxNo,
                outbox.PayloadJson,
                profile?.IntegratorUsername,
                profile?.IntegratorSecretRef);

            var sendResult = await adapter.SendOutgoingAsync(sendReq, ct);
            if (!sendResult.IsSuccess)
            {
                MarkFailure(outbox, doc, sendResult.ErrorMessage ?? "Provider send failed.");
            }
            else
            {
                doc.Status = EInvoiceWorkflowService.NormalizeStatus(sendResult.ProviderStatus, "Sent");
                doc.IntegratorDocumentId = sendResult.IntegratorDocumentId ?? doc.IntegratorDocumentId;
                doc.Uuid = sendResult.Uuid ?? doc.Uuid;
                doc.Ettn = sendResult.Ettn ?? doc.Ettn;
                doc.RawLastResponse = sendResult.RawResponse;
                doc.LastError = null;
                doc.SubmittedAt ??= DateTime.UtcNow;
                if (doc.Status == "Delivered")
                    doc.DeliveredAt = DateTime.UtcNow;

                var invoice = await db.Invoices
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.Id == doc.InvoiceId, ct);
                if (invoice is not null)
                    invoice.IsExported = true;

                outbox.Status = "Done";
                outbox.ProcessedAt = DateTime.UtcNow;
                outbox.LastError = null;
            }
        }
        catch (Exception ex)
        {
            MarkFailure(outbox, doc, ex.Message);
        }
        finally
        {
            outbox.LockedAt = null;
            await db.SaveChangesAsync(ct);
        }
    }

    private static void MarkFailure(EInvoiceOutbox outbox, EInvoiceDocument doc, string error)
    {
        outbox.RetryCount++;
        outbox.Status = outbox.RetryCount >= 8 ? "DeadLetter" : "Pending";
        outbox.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Min(30, Math.Pow(2, outbox.RetryCount)));
        outbox.LastError = ToDbSafeError(error);

        doc.Status = "Failed";
        doc.RetryCount = outbox.RetryCount;
        doc.LastError = outbox.LastError;
    }

    private static bool ShouldPoll(Guid docId, DateTime now, int pollIntervalSeconds)
    {
        var intervalSeconds = Math.Max(5, pollIntervalSeconds);
        if (!StatusPollCache.TryGetValue(docId, out var last))
        {
            StatusPollCache[docId] = now;
            return true;
        }

        if ((now - last).TotalSeconds < intervalSeconds)
            return false;

        StatusPollCache[docId] = now;
        return true;
    }

    private static bool IsFinalStatus(string status)
        => status is "Delivered" or "Rejected" or "Cancelled" or "Failed";

    // Gelen e-Fatura senkronu için en son çekim zamanı (profil/şube bazlı, bellekte).
    private static readonly ConcurrentDictionary<Guid, DateTime> IncomingSyncCache = new();

    private async Task SyncIncomingInvoicesAsync(CancellationToken ct)
    {
        // Yapılandırılabilir aralık; varsayılan 15 dakikada bir.
        var intervalMinutes = Math.Clamp(_cfg.GetValue<int?>("EInvoice:Incoming:SyncIntervalMinutes") ?? 15, 1, 24 * 60);
        var lookbackDays = Math.Clamp(_cfg.GetValue<int?>("EInvoice:Incoming:LookbackDays") ?? 365, 1, 3650);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providerResolver = scope.ServiceProvider.GetRequiredService<IEInvoiceProviderResolver>();
        var now = DateTime.UtcNow;

        var profiles = await db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => !x.IsDeleted && x.IsActive)
            .ToListAsync(ct);

        foreach (var profile in profiles)
        {
            if (IncomingSyncCache.TryGetValue(profile.BranchId, out var last) &&
                (now - last).TotalMinutes < intervalMinutes)
                continue;
            IncomingSyncCache[profile.BranchId] = now;

            try
            {
                var adapter = providerResolver.Resolve(string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode);
                var end = DateTime.UtcNow;
                var start = end.AddDays(-lookbackDays);
                var result = await adapter.GetIncomingInvoicesAsync(
                    new EInvoiceIncomingRequest(profile.TenantId, profile.BranchId, start, end, 5000, profile.IntegratorUsername, profile.IntegratorSecretRef), ct);
                if (!result.IsSuccess || result.Items.Count == 0)
                    continue;

                foreach (var item in result.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.Uuid)) continue;
                    var existing = await db.IncomingEInvoices
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(x => x.TenantId == profile.TenantId && x.Uuid == item.Uuid, ct);
                    if (existing is null)
                    {
                        db.IncomingEInvoices.Add(new IncomingEInvoice
                        {
                            TenantId = profile.TenantId,
                            BranchId = profile.BranchId,
                            Uuid = item.Uuid,
                            InvoiceNumber = item.InvoiceNumber ?? "",
                            SenderName = Clamp(item.SenderName, 400),
                            SenderTaxNumber = Clamp(item.SenderTaxNumber, 16),
                            DocumentType = string.IsNullOrWhiteSpace(item.DocumentType) ? "EFatura" : item.DocumentType,
                            Status = Clamp(item.Status, 64),
                            StatusDescription = Clamp(item.StatusDescription, 400),
                            PayableAmount = item.PayableAmount,
                            Currency = string.IsNullOrWhiteSpace(item.Currency) ? "TRY" : item.Currency,
                            IssueDate = item.IssueDate,
                            EnvelopeIdentifier = Clamp(item.EnvelopeIdentifier, 128),
                            RawContent = item.RawContent,
                            FetchedAt = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        existing.Status = Clamp(item.Status, 64);
                        existing.StatusDescription = Clamp(item.StatusDescription, 400);
                        existing.PayableAmount = item.PayableAmount;
                        existing.FetchedAt = DateTime.UtcNow;
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incoming e-invoice sync failed for branch {BranchId}.", profile.BranchId);
            }
        }
    }

    private static string Clamp(string? value, int maxLen)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= maxLen ? v : v[..maxLen];
    }

    private async Task ProcessExpenseSlipBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providerResolver = scope.ServiceProvider.GetRequiredService<IEInvoiceProviderResolver>();
        var now = DateTime.UtcNow;

        var docs = await db.ExpenseSlipDocuments
            .IgnoreQueryFilters()
            .Where(x => !x.IsDeleted && x.Status == "Queued" && (x.SubmittedAt == null || x.SubmittedAt <= now))
            .OrderBy(x => x.SubmittedAt ?? x.CreatedAt)
            .Take(25)
            .ToListAsync(ct);

        foreach (var doc in docs)
            await ProcessExpenseSlipItemAsync(db, providerResolver, doc, ct);
    }

    private async Task PollExpenseSlipStatusesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providerResolver = scope.ServiceProvider.GetRequiredService<IEInvoiceProviderResolver>();
        var now = DateTime.UtcNow;
        var options = StatusPollingOptions.FromConfiguration(_cfg);
        var cutoff = now.AddMinutes(-Math.Max(1, options.PendingTimeoutMinutes));

        var docs = await db.ExpenseSlipDocuments
            .IgnoreQueryFilters()
            .Where(x => !x.IsDeleted &&
                        (x.Status == "Sent" || x.Status == "Queued") &&
                        x.SubmittedAt != null)
            .OrderBy(x => x.SubmittedAt)
            .Take(options.BatchSize)
            .ToListAsync(ct);

        foreach (var doc in docs)
        {
            if (!ShouldPollExpense(doc.Id, now, options.PollIntervalSeconds))
                continue;

            try
            {
                if (doc.SubmittedAt.HasValue && doc.SubmittedAt.Value < cutoff)
                {
                    doc.Status = "Failed";
                    doc.LastError = $"Durum zaman aşımı: {options.PendingTimeoutMinutes} dakika içinde nihai yanıt alınamadı.";
                    doc.RetryCount = Math.Max(doc.RetryCount, options.MaxStatusRetryCount);
                    ExpenseStatusPollCache.TryRemove(doc.Id, out _);
                    continue;
                }

                var profile = await db.EInvoiceProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId, ct);
                var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
                var adapter = providerResolver.Resolve(providerCode);

                if (adapter is not kuyumcu_infrastructure.Services.EdmSoapEInvoiceProviderAdapter edm)
                {
                    doc.RetryCount++;
                    doc.LastError = $"Gider pusulası EDM e-MM için desteklenmiyor. Provider={providerCode}";
                    if (doc.RetryCount >= options.MaxStatusRetryCount)
                    {
                        doc.Status = "Failed";
                        ExpenseStatusPollCache.TryRemove(doc.Id, out _);
                    }
                    continue;
                }

                var integratorRef = string.IsNullOrWhiteSpace(doc.IntegratorDocumentId)
                    ? doc.Uuid
                    : doc.IntegratorDocumentId;
                if (string.IsNullOrWhiteSpace(integratorRef) && string.IsNullOrWhiteSpace(doc.DocumentNo))
                    continue;

                var statusResult = await edm.GetMmStatusAsync(new kuyumcu_infrastructure.Services.EdmMmStatusRequest(
                    doc.TenantId,
                    doc.BranchId,
                    doc.Id,
                    doc.DocumentNo,
                    integratorRef,
                    doc.Uuid,
                    profile?.IntegratorUsername,
                    profile?.IntegratorSecretRef), ct);

                if (!statusResult.IsSuccess)
                {
                    doc.RetryCount++;
                    doc.LastError = statusResult.ErrorMessage ?? "Gider pusulası durum sorgusu başarısız.";
                    if (doc.RetryCount >= options.MaxStatusRetryCount)
                    {
                        doc.Status = "Failed";
                        ExpenseStatusPollCache.TryRemove(doc.Id, out _);
                    }
                    continue;
                }

                var nextStatus = NormalizeExpenseSlipStatus(statusResult.ProviderStatus, doc.Status);
                var prevStatus = doc.Status;
                doc.Status = nextStatus;
                doc.LastError = null;
                doc.RetryCount = 0;
                if (!string.IsNullOrWhiteSpace(statusResult.RawResponse))
                    doc.RawLastResponse = statusResult.RawResponse!;
                db.ExpenseSlipAuditLogs.Add(BuildExpenseAudit(
                    doc,
                    "PollStatus",
                    prevStatus,
                    doc.Status,
                    true,
                    null,
                    statusResult.RawResponse,
                    null));

                if (IsFinalStatus(nextStatus))
                    ExpenseStatusPollCache.TryRemove(doc.Id, out _);
            }
            catch (Exception ex)
            {
                doc.RetryCount++;
                doc.LastError = ToDbSafeError($"Gider pusulası durum sorgu hatası: {ex.Message}");
                db.ExpenseSlipAuditLogs.Add(BuildExpenseAudit(
                    doc,
                    "PollStatus",
                    doc.Status,
                    doc.Status,
                    false,
                    null,
                    null,
                    ex.Message));
                if (doc.RetryCount >= options.MaxStatusRetryCount)
                {
                    doc.Status = "Failed";
                    ExpenseStatusPollCache.TryRemove(doc.Id, out _);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task ProcessExpenseSlipItemAsync(
        AppDbContext db,
        IEInvoiceProviderResolver providerResolver,
        ExpenseSlipDocument doc,
        CancellationToken ct)
    {
        try
        {
            var profile = await db.EInvoiceProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.BranchId == doc.BranchId, ct);
            var branch = await db.Branches
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.TenantId == doc.TenantId && x.Id == doc.BranchId, ct);
            var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
            var adapter = providerResolver.Resolve(providerCode);

            if (adapter is not kuyumcu_infrastructure.Services.EdmSoapEInvoiceProviderAdapter edm)
            {
                doc.RetryCount++;
                doc.LastError = ToDbSafeError($"Gider pusulası EDM e-MM için desteklenmiyor. Provider={providerCode}");
                doc.Status = doc.RetryCount >= 8 ? "Failed" : "Queued";
                db.ExpenseSlipAuditLogs.Add(BuildExpenseAudit(
                    doc,
                    "Send",
                    doc.Status,
                    doc.Status,
                    false,
                    null,
                    null,
                    doc.LastError));
                return;
            }

            var sendResult = await edm.SendMmAsync(new kuyumcu_infrastructure.Services.EdmMmSendRequest(
                doc.TenantId,
                doc.BranchId,
                doc.Id,
                doc.DocumentNo,
                profile?.TaxNumber,
                string.IsNullOrWhiteSpace(profile?.CompanyName) ? branch?.Name : profile?.CompanyName,
                string.IsNullOrWhiteSpace(profile?.CompanyAddress) ? branch?.Address : profile?.CompanyAddress,
                string.IsNullOrWhiteSpace(profile?.TaxOffice) ? "MERKEZ" : profile?.TaxOffice,
                null,
                branch?.City ?? branch?.Name,
                doc.BuyerTaxNumber,
                doc.PayloadJson,
                profile?.IntegratorUsername,
                profile?.IntegratorSecretRef), ct);

            if (!sendResult.IsSuccess)
            {
                doc.RetryCount++;
                var providerError = string.IsNullOrWhiteSpace(sendResult.ErrorMessage)
                    ? BuildMmProviderErrorFromRaw(sendResult.RawResponse)
                    : sendResult.ErrorMessage!;
                doc.LastError = ToDbSafeError(
                    string.IsNullOrWhiteSpace(providerError)
                        ? "Gider pusulası gönderimi başarısız (EDM detay mesajı boş döndü)."
                        : providerError);
                doc.Status = doc.RetryCount >= 8 ? "Failed" : "Queued";
                db.ExpenseSlipAuditLogs.Add(BuildExpenseAudit(
                    doc,
                    "Send",
                    doc.Status,
                    doc.Status,
                    false,
                    doc.PayloadJson,
                    sendResult.RawResponse,
                    doc.LastError));
                return;
            }

            var prevStatus = doc.Status;
            doc.Status = NormalizeExpenseSlipStatus(sendResult.ProviderStatus, "Sent");
            doc.IntegratorDocumentId = sendResult.IntegratorDocumentId ?? doc.IntegratorDocumentId;
            doc.Uuid = sendResult.Uuid ?? doc.Uuid;
            doc.LastError = null;
            doc.RetryCount = 0;
            doc.SubmittedAt ??= DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(sendResult.RawResponse))
                doc.RawLastResponse = sendResult.RawResponse!;

            db.ExpenseSlipAuditLogs.Add(BuildExpenseAudit(
                doc,
                "Send",
                prevStatus,
                doc.Status,
                true,
                doc.PayloadJson,
                sendResult.RawResponse,
                null));
        }
        catch (Exception ex)
        {
            doc.RetryCount++;
            doc.LastError = ToDbSafeError(ex.Message);
            doc.Status = doc.RetryCount >= 8 ? "Failed" : "Queued";
            db.ExpenseSlipAuditLogs.Add(BuildExpenseAudit(
                doc,
                "Send",
                doc.Status,
                doc.Status,
                false,
                doc.PayloadJson,
                null,
                ex.Message));
        }
        finally
        {
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool ShouldPollExpense(Guid docId, DateTime now, int pollIntervalSeconds)
    {
        var intervalSeconds = Math.Max(5, pollIntervalSeconds);
        if (!ExpenseStatusPollCache.TryGetValue(docId, out var last))
        {
            ExpenseStatusPollCache[docId] = now;
            return true;
        }

        if ((now - last).TotalSeconds < intervalSeconds)
            return false;

        ExpenseStatusPollCache[docId] = now;
        return true;
    }

    private static string NormalizeExpenseSlipStatus(string? providerStatus, string fallback)
    {
        var status = (providerStatus ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(status))
            return fallback;
        var u = status.ToUpperInvariant();
        if (u.Contains("DELIVER") || u.Contains("SUCCEED")) return "Delivered";
        if (u.Contains("SENT") || u.Contains("PROCESS") || u.Contains("QUEUE")) return "Sent";
        if (u.Contains("CANCEL")) return "Cancelled";
        if (u.Contains("REJECT")) return "Rejected";
        if (u.Contains("FAIL") || u.Contains("ERROR")) return "Failed";
        return fallback;
    }

    private static string? BuildMmProviderErrorFromRaw(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
            return null;
        try
        {
            var rc = GetXmlValueSafe(rawResponse, "RETURN_CODE");
            var shortErr = GetXmlValueSafe(rawResponse, "ERROR_SHORT_DES");
            var longErr = GetXmlValueSafe(rawResponse, "ERROR_LONG_DES");
            var status = GetXmlValueSafe(rawResponse, "STATUS");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(rc)) parts.Add($"RETURN_CODE={rc}");
            if (!string.IsNullOrWhiteSpace(status)) parts.Add($"STATUS={status}");
            if (!string.IsNullOrWhiteSpace(shortErr)) parts.Add(shortErr!);
            if (!string.IsNullOrWhiteSpace(longErr)) parts.Add(longErr!);
            return parts.Count == 0 ? "EDM ham yanıtında hata bilgisi çözümlenemedi." : string.Join(" | ", parts);
        }
        catch
        {
            return "EDM ham yanıtı parse edilemedi.";
        }
    }

    private static string? GetXmlValueSafe(string xml, string localName)
    {
        try
        {
            var x = System.Xml.Linq.XDocument.Parse(xml);
            return x.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static ExpenseSlipAuditLog BuildExpenseAudit(
        ExpenseSlipDocument doc,
        string action,
        string? statusBefore,
        string? statusAfter,
        bool isSuccess,
        string? requestJson,
        string? responseRaw,
        string? errorMessage)
    {
        return new ExpenseSlipAuditLog
        {
            TenantId = doc.TenantId,
            BranchId = doc.BranchId,
            DocumentId = doc.Id,
            Action = action,
            StatusBefore = statusBefore,
            StatusAfter = statusAfter,
            IsSuccess = isSuccess,
            RequestJson = requestJson,
            ResponseRaw = responseRaw,
            ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : ToDbSafeError(errorMessage)
        };
    }

    private static string ToDbSafeError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Bilinmeyen hata";
        var text = raw.Trim();
        const int maxLen = 950;
        return text.Length <= maxLen ? text : text[..maxLen];
    }

    private static (string BuyerName, string BuyerTaxNo) ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return ("", "");

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("customer", out var customer) && customer.ValueKind == JsonValueKind.Object)
            {
                var name = customer.TryGetProperty("FullName", out var n1) ? n1.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) && customer.TryGetProperty("fullName", out var n2))
                    name = n2.GetString();
                var taxNo = customer.TryGetProperty("NationalId", out var t1) ? t1.GetString() : null;
                if (string.IsNullOrWhiteSpace(taxNo) && customer.TryGetProperty("nationalId", out var t2))
                    taxNo = t2.GetString();
                return (name ?? "", taxNo ?? "");
            }
        }
        catch
        {
            // payload bozuksa provider yine de kabul edebilir; boş alanla devam edilecek.
        }

        return ("", "");
    }

    private sealed record StatusPollingOptions(
        int PollIntervalSeconds,
        int MaxStatusRetryCount,
        int PendingTimeoutMinutes,
        int BatchSize)
    {
        public static StatusPollingOptions FromConfiguration(IConfiguration cfg)
        {
            var section = cfg.GetSection("EInvoice:StatusPolling");
            var pollIntervalSeconds = section.GetValue<int?>("PollIntervalSeconds") ?? 30;
            var maxStatusRetryCount = section.GetValue<int?>("MaxStatusRetryCount") ?? 8;
            var pendingTimeoutMinutes = section.GetValue<int?>("PendingTimeoutMinutes") ?? 120;
            var batchSize = section.GetValue<int?>("BatchSize") ?? 50;

            return new StatusPollingOptions(
                Math.Clamp(pollIntervalSeconds, 5, 600),
                Math.Clamp(maxStatusRetryCount, 1, 50),
                Math.Clamp(pendingTimeoutMinutes, 5, 24 * 60),
                Math.Clamp(batchSize, 1, 500));
        }
    }
}
