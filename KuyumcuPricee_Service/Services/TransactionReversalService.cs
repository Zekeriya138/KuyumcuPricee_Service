using System.Globalization;
using System.Text.Json;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public sealed class TransactionReversalService
{
    private readonly AppDbContext _db;

    public TransactionReversalService(AppDbContext db) => _db = db;

    public sealed record ReverseRequest(string Reason);
    public sealed record ReverseResult(bool Ok, string? Error, Guid? ReversalLogId, Guid? BatchId);

    public sealed record TransactionDetailDto(
        Guid? TransactionId,
        Guid? BatchId,
        string SourceKind,
        string RefType,
        Guid? RefId,
        DateTime IslemTarihi,
        string Grup,
        string Kalem,
        string Deger,
        string CariDurum,
        string Aciklama,
        string Kullanici,
        bool CanReverse,
        bool IsReversed,
        string OperationType,
        List<string> RelatedLines,
        List<ZiynetUrunStokItemDto> ZiynetUrunStokItems);

    public sealed record ZiynetUrunStokItemDto(string Ad, string Tip, decimal Adet);

    public sealed record ReversedTransactionDto(
        Guid Id,
        DateTime ReversedAt,
        DateTime OriginalTxDate,
        string Grup,
        string Kalem,
        string Deger,
        string CariDurum,
        string OriginalAciklama,
        string Reason,
        string OriginalPerformedBy,
        string ReversedByUserName);

    public async Task<ReverseResult> ReverseCustomerAsync(
        Guid tenantId, Guid branchId, Guid customerId, Guid anchorTransactionId,
        string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new ReverseResult(false, "Geri alma açıklaması zorunludur.", null, null);

        var anchor = await _db.CustomerTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == anchorTransactionId &&
                x.TenantId == tenantId &&
                x.CustomerId == customerId &&
                !x.IsDeleted, ct);
        if (anchor is null)
            return new ReverseResult(false, "İşlem bulunamadı.", null, null);
        if (anchor.IsReversed)
            return new ReverseResult(false, "Bu işlem zaten geri alınmış.", null, null);

        if (string.Equals(anchor.RefType, "SALE", StringComparison.OrdinalIgnoreCase) && anchor.RefId.HasValue)
        {
            var saleCheck = await ValidateSaleReversalAsync(tenantId, anchor.RefId.Value, ct);
            if (saleCheck != null) return new ReverseResult(false, saleCheck, null, null);
            return await ReverseSaleAsync(tenantId, anchor.RefId.Value, customerId, null, reason, userId, userName, ct);
        }
        if (string.Equals(anchor.RefType, "PURCHASE", StringComparison.OrdinalIgnoreCase) && anchor.RefId.HasValue)
            return await ReversePurchaseAsync(tenantId, anchor.RefId.Value, customerId, null, reason, userId, userName, ct);

        if (string.Equals(anchor.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(anchor.ItemName, "SALE_EVENT", StringComparison.OrdinalIgnoreCase) &&
            anchor.RefId.HasValue)
        {
            var saleCheck = await ValidateSaleReversalAsync(tenantId, anchor.RefId.Value, ct);
            if (saleCheck != null) return new ReverseResult(false, saleCheck, null, null);
            return await ReverseSaleAsync(tenantId, anchor.RefId.Value, customerId, null, reason, userId, userName, ct);
        }
        if (string.Equals(anchor.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(anchor.ItemName, "PURCHASE_EVENT", StringComparison.OrdinalIgnoreCase) &&
            anchor.RefId.HasValue)
            return await ReversePurchaseAsync(tenantId, anchor.RefId.Value, customerId, null, reason, userId, userName, ct);

        if (TryResolveCustomerTransferId(anchor, out var customerTransferId))
            return await ReverseCariTransferAsync(tenantId, branchId, customerTransferId, reason, userId, userName, ct);

        var batchId = anchor.BatchId ?? await ResolveLegacyCustomerBatchIdAsync(anchor, ct);
        return await ReverseCustomerBatchAsync(tenantId, branchId, customerId, batchId, anchor, reason, userId, userName, ct);
    }

    public async Task<ReverseResult> ReverseCustomerByBatchAsync(
        Guid tenantId, Guid branchId, Guid customerId, Guid batchId,
        string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new ReverseResult(false, "Geri alma açıklaması zorunludur.", null, null);

        var anchor = await _db.CustomerTransactions
            .Where(x => x.TenantId == tenantId && x.CustomerId == customerId && x.BatchId == batchId && !x.IsDeleted)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (anchor is null)
            return new ReverseResult(false, "Batch bulunamadı.", null, null);

        return await ReverseCustomerBatchAsync(tenantId, branchId, customerId, batchId, anchor, reason, userId, userName, ct);
    }

    public async Task<ReverseResult> ReverseSupplierAsync(
        Guid tenantId, Guid branchId, Guid supplierId, Guid anchorTransactionId,
        string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return new ReverseResult(false, "Geri alma açıklaması zorunludur.", null, null);

        var anchor = await _db.SupplierTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == anchorTransactionId &&
                x.TenantId == tenantId &&
                x.SupplierId == supplierId &&
                !x.IsDeleted, ct);
        if (anchor is null)
            return new ReverseResult(false, "İşlem bulunamadı.", null, null);
        if (anchor.IsReversed)
            return new ReverseResult(false, "Bu işlem zaten geri alınmış.", null, null);

        if (string.Equals(anchor.RefType, "PURCHASE", StringComparison.OrdinalIgnoreCase) && anchor.RefId.HasValue)
            return await ReversePurchaseAsync(tenantId, anchor.RefId.Value, null, supplierId, reason, userId, userName, ct);
        if (string.Equals(anchor.RefType, "SALE", StringComparison.OrdinalIgnoreCase) && anchor.RefId.HasValue)
        {
            var saleCheck = await ValidateSaleReversalAsync(tenantId, anchor.RefId.Value, ct);
            if (saleCheck != null) return new ReverseResult(false, saleCheck, null, null);
            return await ReverseSaleAsync(tenantId, anchor.RefId.Value, null, supplierId, reason, userId, userName, ct);
        }

        if (TryResolveSupplierTransferId(anchor, out var supplierTransferId))
            return await ReverseCariTransferAsync(tenantId, branchId, supplierTransferId, reason, userId, userName, ct);

        var batchId = anchor.BatchId ?? await ResolveLegacySupplierBatchIdAsync(anchor, ct);
        return await ReverseSupplierBatchAsync(tenantId, branchId, supplierId, batchId, anchor, reason, userId, userName, ct);
    }

    public async Task<ReverseResult> ReverseSaleAsync(
        Guid tenantId, Guid saleId, Guid? customerId, Guid? supplierId,
        string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        var block = await ValidateSaleReversalAsync(tenantId, saleId, ct);
        if (block != null) return new ReverseResult(false, block, null, null);

        var sale = await _db.Sales.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == saleId && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (sale is null) return new ReverseResult(false, "Satış bulunamadı.", null, null);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var batchId = Guid.NewGuid();
            var reversedAt = DateTime.UtcNow;

            // Cari satırları geri al
            var cariRows = await _db.CustomerTransactions
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && !x.IsReversed &&
                            x.RefType == "SALE" && x.RefId == saleId)
                .ToListAsync(ct);
            if (sale.CustomerId.HasValue)
            {
                var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, sale.CustomerId.Value, ct);
                foreach (var row in cariRows)
                    ReverseCustomerTransactionRow(bal, row);
                bal.UpdatedAt = reversedAt;
            }
            MarkCustomerRowsReversed(cariRows, reversedAt, null);

            // Audit satırları
            var auditRows = await _db.CustomerTransactions
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && !x.IsReversed &&
                            x.RefId == saleId &&
                            (x.GroupCode == "AUDIT" || x.RefType == "SALE"))
                .ToListAsync(ct);
            MarkCustomerRowsReversed(auditRows, reversedAt, null);

            // Ziynet settlement restore: rows soft-deleted during payment - restore if linked
            foreach (var row in cariRows.Where(x => x.GroupCode == "ZIYNET"))
            {
                var deleted = await _db.CustomerTransactions
                    .IgnoreQueryFilters()
                    .Where(x => x.TenantId == tenantId && x.CustomerId == row.CustomerId &&
                                x.IsDeleted && !x.IsReversed && x.GroupCode == "ZIYNET" &&
                                x.ItemName == row.ItemName && x.ItemType == row.ItemType)
                    .OrderByDescending(x => x.TxDate)
                    .FirstOrDefaultAsync(ct);
                if (deleted != null)
                {
                    deleted.IsDeleted = false;
                    deleted.Quantity = Math.Max(deleted.Quantity, row.Quantity);
                }
            }

            // Kasa
            await ReverseCashByRefAsync(tenantId, "SALE", saleId, batchId, reversedAt, ct);

            // Tedarikçi veresiye satırları
            var supTx = await _db.SupplierTransactions
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && !x.IsReversed &&
                            x.Description != null && x.Description.Contains(saleId.ToString()))
                .ToListAsync(ct);
            if (supTx.Count > 0)
            {
                var supIds = supTx.Select(x => x.SupplierId).Distinct().ToList();
                foreach (var sid in supIds)
                {
                    var sb = await _db.SupplierBalances.FirstOrDefaultAsync(x => x.SupplierId == sid && x.TenantId == tenantId && !x.IsDeleted, ct);
                    if (sb is null) continue;
                    foreach (var st in supTx.Where(x => x.SupplierId == sid))
                        ReverseSupplierTransactionRow(sb, st);
                    sb.UpdatedAt = reversedAt;
                }
                MarkSupplierRowsReversed(supTx, reversedAt, null);
            }

            sale.IsDeleted = true;

            var log = BuildLog(tenantId, "Customer", sale.CustomerId ?? Guid.Empty, sale.BranchId, batchId, "SALE",
                sale.CreatedAt, null, reason, userId, userName, reversedAt,
                "AUDIT", "Satış İşlemi", "İşlem kaydı", "İşlem", $"Satış geri alındı ({saleId})",
                JsonSerializer.Serialize(new { saleId, itemCount = sale.Items.Count }));
            _db.TransactionReversalLogs.Add(log);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReverseResult(true, null, log.Id, batchId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new ReverseResult(false, ex.Message, null, null);
        }
    }

    public async Task<ReverseResult> ReversePurchaseAsync(
        Guid tenantId, Guid purchaseId, Guid? customerId, Guid? supplierId,
        string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        var purchase = await _db.Purchases.Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == purchaseId && x.TenantId == tenantId, ct);
        if (purchase is null) return new ReverseResult(false, "Alış bulunamadı.", null, null);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var batchId = Guid.NewGuid();
            var reversedAt = DateTime.UtcNow;

            var custRows = await _db.CustomerTransactions
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && !x.IsReversed &&
                            x.RefType == "PURCHASE" && x.RefId == purchaseId)
                .ToListAsync(ct);
            if (purchase.CustomerId.HasValue && custRows.Count > 0)
            {
                var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, purchase.CustomerId.Value, ct);
                foreach (var row in custRows)
                    ReverseCustomerTransactionRow(bal, row);
                bal.UpdatedAt = reversedAt;
            }
            MarkCustomerRowsReversed(custRows, reversedAt, null);

            var supRows = await _db.SupplierTransactions
                .Where(x => x.TenantId == tenantId && !x.IsDeleted && !x.IsReversed &&
                            (x.RefId == purchaseId ||
                             (x.Description != null && x.Description.Contains(purchaseId.ToString()))))
                .ToListAsync(ct);
            if (purchase.SupplierId.HasValue && supRows.Count > 0)
            {
                var sb = await _db.SupplierBalances
                    .FirstOrDefaultAsync(x => x.SupplierId == purchase.SupplierId && x.TenantId == tenantId && !x.IsDeleted, ct);
                if (sb is not null)
                {
                    foreach (var row in supRows)
                        ReverseSupplierTransactionRow(sb, row);
                    sb.UpdatedAt = reversedAt;
                }
            }
            MarkSupplierRowsReversed(supRows, reversedAt, null);

            await ReverseCashByRefAsync(tenantId, "PURCHASE", purchaseId, batchId, reversedAt, ct);

            purchase.IsDeleted = true;

            var partyType = purchase.SupplierId.HasValue ? "Supplier" : "Customer";
            var partyId = purchase.SupplierId ?? purchase.CustomerId ?? Guid.Empty;
            var log = BuildLog(tenantId, partyType, partyId, purchase.BranchId, batchId, "PURCHASE",
                purchase.Date, null, reason, userId, userName, reversedAt,
                "AUDIT", "Alış İşlemi", "İşlem kaydı", "İşlem", $"Alış geri alındı ({purchaseId})",
                JsonSerializer.Serialize(new { purchaseId }));
            _db.TransactionReversalLogs.Add(log);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReverseResult(true, null, log.Id, batchId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new ReverseResult(false, ex.Message, null, null);
        }
    }

    public async Task<List<ReversedTransactionDto>> GetCustomerReversedAsync(Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var rows = await _db.TransactionReversalLogs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PartyType == "Customer" && x.PartyId == customerId && !x.IsDeleted)
            .OrderByDescending(x => x.ReversedAt)
            .Take(300)
            .ToListAsync(ct);
        return rows.Select(MapReversedDto).ToList();
    }

    public async Task<List<ReversedTransactionDto>> GetSupplierReversedAsync(Guid tenantId, Guid supplierId, CancellationToken ct)
    {
        var rows = await _db.TransactionReversalLogs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.PartyType == "Supplier" && x.PartyId == supplierId && !x.IsDeleted)
            .OrderByDescending(x => x.ReversedAt)
            .Take(300)
            .ToListAsync(ct);
        return rows.Select(MapReversedDto).ToList();
    }

    public async Task<TransactionDetailDto?> GetCustomerDetailAsync(Guid tenantId, Guid customerId, Guid transactionId, CancellationToken ct)
    {
        var x = await _db.CustomerTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.TenantId == tenantId && t.CustomerId == customerId && !t.IsDeleted, ct);
        if (x is null) return null;
        var stockItems = await ResolveCustomerZiynetUrunStockAsync(tenantId, customerId, x, ct);
        return MapCustomerDetail(x) with { ZiynetUrunStokItems = stockItems };
    }

    public async Task<TransactionDetailDto?> GetSupplierDetailAsync(Guid tenantId, Guid supplierId, Guid transactionId, CancellationToken ct)
    {
        var x = await _db.SupplierTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.TenantId == tenantId && t.SupplierId == supplierId && !t.IsDeleted, ct);
        if (x is null) return null;
        var stockItems = await ResolveSupplierZiynetUrunStockAsync(tenantId, supplierId, x, ct);
        return MapSupplierDetail(x) with { ZiynetUrunStokItems = stockItems };
    }

    private async Task<List<ZiynetUrunStokItemDto>> ResolveCustomerZiynetUrunStockAsync(
        Guid tenantId, Guid customerId, CustomerTransaction anchor, CancellationToken ct)
    {
        if (anchor.BatchId.HasValue && anchor.BatchId.Value != Guid.Empty)
        {
            var auditNotes = await _db.CustomerTransactions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.CustomerId == customerId &&
                            t.BatchId == anchor.BatchId && !t.IsDeleted &&
                            t.GroupCode == "AUDIT" &&
                            t.ItemName == "ZIYNET_URUN_STOK")
                .Select(t => t.Note)
                .ToListAsync(ct);
            if (auditNotes.Count > 0)
            {
                var fromAudit = MapZiynetUrunStokItems(ZiynetUrunStokMarker.Parse(auditNotes[0]));
                if (fromAudit.Count > 0) return fromAudit;
            }

            var notes = await _db.CustomerTransactions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.CustomerId == customerId &&
                            t.BatchId == anchor.BatchId && !t.IsDeleted)
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Note)
                .ToListAsync(ct);
            foreach (var note in notes)
            {
                var parsed = ZiynetUrunStokMarker.Parse(note);
                if (parsed.Count > 0)
                    return MapZiynetUrunStokItems(parsed);
            }
            return [];
        }

        return MapZiynetUrunStokItems(ZiynetUrunStokMarker.Parse(anchor.Note));
    }

    private async Task<List<ZiynetUrunStokItemDto>> ResolveSupplierZiynetUrunStockAsync(
        Guid tenantId, Guid supplierId, SupplierTransaction anchor, CancellationToken ct)
    {
        if (anchor.BatchId.HasValue && anchor.BatchId.Value != Guid.Empty)
        {
            var descriptions = await _db.SupplierTransactions.AsNoTracking()
                .Where(t => t.TenantId == tenantId && t.SupplierId == supplierId &&
                            t.BatchId == anchor.BatchId && !t.IsDeleted)
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Description)
                .ToListAsync(ct);
            foreach (var description in descriptions)
            {
                var parsed = ZiynetUrunStokMarker.Parse(description);
                if (parsed.Count > 0)
                    return MapZiynetUrunStokItems(parsed);
            }
            return [];
        }

        return MapZiynetUrunStokItems(ZiynetUrunStokMarker.Parse(anchor.Description));
    }

    private static List<ZiynetUrunStokItemDto> MapZiynetUrunStokItems(List<ZiynetUrunStokMarker.Item> items)
        => ZiynetUrunStokMarker.MergeDistinct(items)
            .Select(x => new ZiynetUrunStokItemDto(x.Ad, x.Tip, x.Adet))
            .ToList();

    private async Task<ReverseResult> ReverseCustomerBatchAsync(
        Guid tenantId, Guid branchId, Guid customerId, Guid batchId,
        CustomerTransaction anchor, string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var reversedAt = DateTime.UtcNow;
            var rows = await LoadCustomerBatchRowsAsync(tenantId, customerId, batchId, anchor, ct);
            if (rows.Any(x => x.IsReversed))
                return new ReverseResult(false, "Bu işlem zaten geri alınmış.", null, null);

            var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, customerId, ct);
            foreach (var row in rows)
            {
                if (string.Equals(row.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.RefType, "MANUAL_SETTLE", StringComparison.OrdinalIgnoreCase))
                {
                    // Restore settled iscilikli row
                    var baseName = row.ItemType ?? row.ItemName;
                    var restored = await _db.CustomerTransactions
                        .IgnoreQueryFilters()
                        .Where(r => r.TenantId == tenantId && r.CustomerId == customerId &&
                                    r.IsDeleted && r.GroupCode == "ISCILIKLI" &&
                                    (r.ItemName == baseName || r.ItemType == baseName))
                        .OrderByDescending(r => r.TxDate)
                        .FirstOrDefaultAsync(ct);
                    if (restored != null) restored.IsDeleted = false;
                }
                else if (string.Equals(row.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(row.ItemName, "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
                {
                    // Restore consumed ziynet quantity - best effort
                    await RestoreZiynetFromAuditAsync(tenantId, customerId, branchId, row, ct);
                }
                else
                {
                    ReverseCustomerTransactionRow(bal, row);
                }
            }
            bal.UpdatedAt = reversedAt;

            var log = BuildLog(tenantId, "Customer", customerId, branchId, batchId,
                ResolveCustomerOperationType(anchor),
                anchor.TxDate, anchor.KullaniciAdi, reason, userId, userName, reversedAt,
                anchor.GroupCode ?? "", ResolveCustomerKalem(anchor), FormatCustomerValue(anchor),
                anchor.CariDurum ?? "", anchor.Note ?? "",
                JsonSerializer.Serialize(rows.Select(r => new { r.Id, r.GroupCode, r.ItemName, r.Quantity, r.Direction })));

            MarkCustomerRowsReversed(rows, reversedAt, log.Id);
            await ReverseCashByBatchAsync(tenantId, batchId, reversedAt, ct);

            _db.TransactionReversalLogs.Add(log);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReverseResult(true, null, log.Id, batchId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new ReverseResult(false, ex.Message, null, null);
        }
    }

    private async Task<ReverseResult> ReverseSupplierBatchAsync(
        Guid tenantId, Guid branchId, Guid supplierId, Guid batchId,
        SupplierTransaction anchor, string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var reversedAt = DateTime.UtcNow;
            var rows = await LoadSupplierBatchRowsAsync(tenantId, supplierId, batchId, anchor, ct);
            if (rows.Any(x => x.IsReversed))
                return new ReverseResult(false, "Bu işlem zaten geri alınmış.", null, null);

            var bal = await _db.SupplierBalances
                .FirstOrDefaultAsync(x => x.SupplierId == supplierId && x.TenantId == tenantId && !x.IsDeleted, ct);
            if (bal is null)
            {
                bal = new SupplierBalance { TenantId = tenantId, SupplierId = supplierId };
                _db.SupplierBalances.Add(bal);
            }

            foreach (var row in rows)
                ReverseSupplierTransactionRow(bal, row);
            bal.UpdatedAt = reversedAt;

            var log = BuildLog(tenantId, "Supplier", supplierId, branchId, batchId,
                anchor.TxType ?? "MANUAL",
                anchor.TxDate, anchor.KullaniciAdi, reason, userId, userName, reversedAt,
                anchor.TxType ?? "", FormatSupplierKalem(anchor), FormatSupplierValue(anchor),
                anchor.TxType ?? "", anchor.Description ?? "",
                JsonSerializer.Serialize(rows.Select(r => new { r.Id, r.TxType, r.TargetUnit, r.TargetAmount })));

            MarkSupplierRowsReversed(rows, reversedAt, log.Id);
            await ReverseCashByBatchAsync(tenantId, batchId, reversedAt, ct);

            _db.TransactionReversalLogs.Add(log);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReverseResult(true, null, log.Id, batchId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new ReverseResult(false, ex.Message, null, null);
        }
    }

    private async Task<string?> ValidateSaleReversalAsync(Guid tenantId, Guid saleId, CancellationToken ct)
    {
        var invoice = await _db.Invoices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SaleId == saleId && x.TenantId == tenantId, ct);
        if (invoice is null) return null;

        var doc = await _db.EInvoiceDocuments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.InvoiceId == invoice.Id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (doc is null) return null;
        if (doc.DeliveredAt.HasValue)
            return "EDM'ye teslim edilmiş resmi fatura olduğu için satış geri alınamaz.";
        if (!string.Equals(doc.Status, "Draft", StringComparison.OrdinalIgnoreCase) &&
            doc.SubmittedAt.HasValue)
            return "Resmi fatura süreci başlatıldığı için geri alınamaz.";
        return null;
    }

    private static void ReverseCustomerTransactionRow(CustomerBalance bal, CustomerTransaction row)
    {
        if (string.Equals(row.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase))
        {
            var signed = row.Direction >= 0 ? row.Quantity : -row.Quantity;
            ApplyCustomerBalanceDelta(bal, row.ItemName, -signed);
            return;
        }
        if (string.Equals(row.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.RefType, "MANUAL_SETTLE", StringComparison.OrdinalIgnoreCase))
        {
            // ISCILIKLI opening - no balance table effect
        }
    }

    private static void ReverseSupplierTransactionRow(SupplierBalance bal, SupplierTransaction row)
    {
        if (row.TxType == "ZIYNET") return; // ziynet tracked via description
        var unit = NormalizeUnit(row.TargetUnit);
        var signed = row.TxType == "PAYMENT" ? row.TargetAmount : -row.TargetAmount;
        if (row.TxType is "COLLECTION" or "PAYMENT")
            ApplySupplierBalanceDelta(bal, unit, -signed);
        else if (row.TxType == "OPENING_BALANCE" || row.TxType == "BALANCE_CONVERSION")
            ApplySupplierBalanceDelta(bal, unit, -row.TargetAmount);
    }

    private async Task ReverseCashByBatchAsync(Guid tenantId, Guid? batchId, DateTime reversedAt, CancellationToken ct)
    {
        if (!batchId.HasValue || batchId == Guid.Empty) return;
        var cashRows = await _db.CashTransactions
            .Where(x => x.TenantId == tenantId && x.BatchId == batchId && !x.IsDeleted && !x.IsReversed)
            .ToListAsync(ct);
        foreach (var c in cashRows)
            await ReverseSingleCashAsync(c, reversedAt, ct);
    }

    private async Task ReverseCashByRefAsync(Guid tenantId, string refType, Guid refId, Guid batchId, DateTime reversedAt, CancellationToken ct)
    {
        var cashRows = await _db.CashTransactions
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && !x.IsReversed &&
                        x.RefType == refType && x.RefId == refId)
            .ToListAsync(ct);
        foreach (var c in cashRows)
            await ReverseSingleCashAsync(c, reversedAt, ct);
    }

    private async Task ReverseSingleCashAsync(CashTransaction c, DateTime reversedAt, CancellationToken ct)
    {
        var account = await _db.CashAccounts.FirstOrDefaultAsync(x => x.Id == c.CashAccountId, ct);
        if (account != null)
        {
            account.CurrentBalance += string.Equals(c.TxType, "Income", StringComparison.OrdinalIgnoreCase)
                ? -c.Amount
                : c.Amount;
        }
        c.IsReversed = true;
        c.ReversedAt = reversedAt;
    }

    private async Task<List<CustomerTransaction>> LoadCustomerBatchRowsAsync(
        Guid tenantId, Guid customerId, Guid batchId, CustomerTransaction anchor, CancellationToken ct)
    {
        if (batchId != Guid.Empty)
        {
            return await _db.CustomerTransactions
                .Where(x => x.TenantId == tenantId && x.CustomerId == customerId &&
                            x.BatchId == batchId && !x.IsDeleted && !x.IsReversed)
                .ToListAsync(ct);
        }
        var windowStart = anchor.CreatedAt.AddSeconds(-2);
        var windowEnd = anchor.CreatedAt.AddSeconds(2);
        return await _db.CustomerTransactions
            .Where(x => x.TenantId == tenantId && x.CustomerId == customerId &&
                        !x.IsDeleted && !x.IsReversed &&
                        x.TxDate == anchor.TxDate &&
                        (x.Note ?? "") == (anchor.Note ?? "") &&
                        x.CreatedAt >= windowStart && x.CreatedAt <= windowEnd)
            .ToListAsync(ct);
    }

    private async Task<List<SupplierTransaction>> LoadSupplierBatchRowsAsync(
        Guid tenantId, Guid supplierId, Guid batchId, SupplierTransaction anchor, CancellationToken ct)
    {
        if (batchId != Guid.Empty)
        {
            return await _db.SupplierTransactions
                .Where(x => x.TenantId == tenantId && x.SupplierId == supplierId &&
                            x.BatchId == batchId && !x.IsDeleted && !x.IsReversed)
                .ToListAsync(ct);
        }
        var windowStart = anchor.CreatedAt.AddSeconds(-2);
        var windowEnd = anchor.CreatedAt.AddSeconds(2);
        return await _db.SupplierTransactions
            .Where(x => x.TenantId == tenantId && x.SupplierId == supplierId &&
                        !x.IsDeleted && !x.IsReversed &&
                        x.TxDate == anchor.TxDate &&
                        (x.Description ?? "") == (anchor.Description ?? "") &&
                        x.CreatedAt >= windowStart && x.CreatedAt <= windowEnd)
            .ToListAsync(ct);
    }

    private async Task<Guid> ResolveLegacyCustomerBatchIdAsync(CustomerTransaction anchor, CancellationToken ct)
        => anchor.BatchId ?? Guid.Empty;

    private async Task<Guid> ResolveLegacySupplierBatchIdAsync(SupplierTransaction anchor, CancellationToken ct)
        => anchor.BatchId ?? Guid.Empty;

    private async Task RestoreZiynetFromAuditAsync(Guid tenantId, Guid customerId, Guid? branchId, CustomerTransaction audit, CancellationToken ct)
    {
        if (!TryParseZiynetDusumAudit(audit, out var ad, out var tip, out var qty) || qty <= 0m)
            return;

        static string Nz(string? value, string fallback = "")
            => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToUpperInvariant();

        var adKey = Nz(ad);
        var tipKey = Nz(tip, "YENI");
        var restoreDir = audit.Direction >= 0 ? -1 : 1;
        var remaining = qty;

        var deletedRows = await _db.CustomerTransactions
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId &&
                        r.CustomerId == customerId &&
                        r.IsDeleted &&
                        r.GroupCode == "ZIYNET" &&
                        r.Direction == restoreDir)
            .OrderByDescending(r => r.TxDate)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        foreach (var row in deletedRows)
        {
            if (remaining <= 0m) break;
            if (!string.Equals(Nz(row.ItemName), adKey, StringComparison.OrdinalIgnoreCase)) continue;

            var rowTip = Nz(string.IsNullOrWhiteSpace(row.ItemType) ? "YENI" : row.ItemType, "YENI");
            if (!string.Equals(rowTip, tipKey, StringComparison.OrdinalIgnoreCase)) continue;

            row.IsDeleted = false;
            row.Quantity = decimal.Round(remaining, 3, MidpointRounding.AwayFromZero);
            row.RefType = "REVERSAL";
            row.Note = $"Ziynet düşüm geri alma: {adKey} ({tipKey})";
            remaining = 0m;
        }

        if (remaining <= 0m) return;

        _db.CustomerTransactions.Add(new CustomerTransaction
        {
            TenantId = tenantId,
            CustomerId = customerId,
            BranchId = branchId,
            GroupCode = "ZIYNET",
            ItemName = adKey,
            ItemType = string.Equals(tipKey, "YENI", StringComparison.OrdinalIgnoreCase) ? null : tip.Trim(),
            Quantity = decimal.Round(remaining, 3, MidpointRounding.AwayFromZero),
            Direction = restoreDir,
            TxDate = DateTime.UtcNow,
            CariDurum = restoreDir >= 0 ? "Alacakli" : "Borclu",
            RefType = "REVERSAL",
            Note = $"Ziynet düşüm geri alma: {adKey} ({tipKey})",
            BatchId = audit.BatchId
        });
    }

    private static bool TryParseZiynetDusumAudit(CustomerTransaction audit, out string ad, out string tip, out decimal qty)
    {
        ad = "";
        tip = "";
        qty = audit.Quantity;
        if (qty <= 0m) return false;

        var itemType = (audit.ItemType ?? "").Trim();
        if (itemType.Contains('|', StringComparison.Ordinal))
        {
            var parts = itemType.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                ad = parts[0];
                tip = parts[1];
                return !string.IsNullOrWhiteSpace(ad);
            }
        }

        var note = audit.Note ?? "";
        var marker = "Ziynet düşüm:";
        var idx = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var tail = note[(idx + marker.Length)..].Trim();
        var pipeIdx = tail.IndexOf('|');
        var detail = pipeIdx >= 0 ? tail[..pipeIdx].Trim() : tail;
        var openParen = detail.IndexOf('(');
        if (openParen < 0) return false;

        ad = detail[..openParen].Trim();
        tip = detail[(openParen + 1)..].Replace(")", "").Trim();
        return !string.IsNullOrWhiteSpace(ad);
    }

    private static void MarkCustomerRowsReversed(List<CustomerTransaction> rows, DateTime reversedAt, Guid? logId)
    {
        foreach (var r in rows)
        {
            r.IsReversed = true;
            r.ReversedAt = reversedAt;
            if (logId.HasValue) r.ReversalLogId = logId;
        }
    }

    private static void MarkSupplierRowsReversed(List<SupplierTransaction> rows, DateTime reversedAt, Guid? logId)
    {
        foreach (var r in rows)
        {
            r.IsReversed = true;
            r.ReversedAt = reversedAt;
            if (logId.HasValue) r.ReversalLogId = logId;
        }
    }

    private static TransactionReversalLog BuildLog(
        Guid tenantId,
        string partyType, Guid partyId, Guid? branchId, Guid batchId, string operationType,
        DateTime originalTxDate, string? originalPerformedBy, string reason,
        Guid? userId, string? userName, DateTime reversedAt,
        string displayGrup, string displayKalem, string displayDeger, string displayCariDurum, string displayAciklama,
        string snapshotJson)
    {
        return new TransactionReversalLog
        {
            TenantId = tenantId,
            BranchId = branchId,
            PartyType = partyType,
            PartyId = partyId,
            BatchId = batchId,
            OperationType = operationType,
            OriginalTxDate = originalTxDate,
            OriginalPerformedBy = originalPerformedBy,
            ReversedByUserId = userId,
            ReversedByUserName = userName,
            Reason = reason.Trim(),
            ReversedAt = reversedAt,
            SnapshotJson = snapshotJson,
            DisplayGrup = displayGrup,
            DisplayKalem = displayKalem,
            DisplayDeger = displayDeger,
            DisplayCariDurum = displayCariDurum,
            DisplayAciklama = displayAciklama
        };
    }

    private static ReversedTransactionDto MapReversedDto(TransactionReversalLog x) => new(
        x.Id, x.ReversedAt, x.OriginalTxDate, x.DisplayGrup, x.DisplayKalem, x.DisplayDeger,
        x.DisplayCariDurum, x.DisplayAciklama, x.Reason, x.OriginalPerformedBy ?? "-", x.ReversedByUserName ?? "-");

    private static TransactionDetailDto MapCustomerDetail(CustomerTransaction x)
    {
        var canReverse = !x.IsReversed && x.GroupCode != "AUDIT" || 
            (x.GroupCode == "AUDIT" && (x.ItemName == "SALE_EVENT" || x.ItemName == "PURCHASE_EVENT"));
        return new TransactionDetailDto(
            x.Id, x.BatchId, "Transaction", x.RefType ?? x.GroupCode ?? "", x.RefId,
            x.TxDate, x.GroupCode ?? "", ResolveCustomerKalem(x), FormatCustomerValue(x),
            x.CariDurum ?? "", x.Note ?? "", x.KullaniciAdi ?? "-",
            !x.IsReversed, x.IsReversed, ResolveCustomerOperationType(x),
            new List<string> { FormatCustomerValue(x) },
            new List<ZiynetUrunStokItemDto>());
    }

    private static TransactionDetailDto MapSupplierDetail(SupplierTransaction x) => new(
        x.Id, x.BatchId, "Transaction", x.RefType ?? x.TxType ?? "", x.RefId,
        x.TxDate, x.TxType ?? "", FormatSupplierKalem(x), FormatSupplierValue(x),
        x.TxType ?? "", x.Description ?? "", x.KullaniciAdi ?? "-",
        !x.IsReversed, x.IsReversed, x.TxType ?? "MANUAL",
        new List<string> { FormatSupplierValue(x) },
        new List<ZiynetUrunStokItemDto>());

    private static string ResolveCustomerOperationType(CustomerTransaction x)
    {
        var rt = (x.RefType ?? "").Trim().ToUpperInvariant();
        if (rt == "OPENING_BALANCE") return "OPENING_BALANCE";
        if (rt == "BALANCE_CONVERSION") return "BALANCE_CONVERSION";
        if (rt == "SALE") return "SALE";
        if (rt == "PURCHASE") return "PURCHASE";
        if (x.GroupCode == "AUDIT" && x.ItemName == "SALE_EVENT") return "SALE";
        if (x.GroupCode == "AUDIT" && x.ItemName == "PURCHASE_EVENT") return "PURCHASE";
        if (x.CariDurum?.Contains("Tahsilat", StringComparison.OrdinalIgnoreCase) == true) return "COLLECTION";
        if (x.CariDurum?.Contains("Odeme", StringComparison.OrdinalIgnoreCase) == true ||
            x.CariDurum?.Contains("Ödeme", StringComparison.OrdinalIgnoreCase) == true) return "PAYMENT";
        return rt.Length > 0 ? rt : "MANUAL";
    }

    private static string ResolveCustomerKalem(CustomerTransaction x)
    {
        if (x.GroupCode == "DOVIZ") return x.ItemName;
        if (x.GroupCode == "ZIYNET") return $"{x.ItemName} / {x.ItemType ?? "Yeni"}";
        if (x.GroupCode == "ISCILIKLI") return x.ItemName;
        if (x.GroupCode == "AUDIT") return x.ItemName ?? "İşlem";
        return x.ItemName;
    }

    private static string FormatCustomerValue(CustomerTransaction x)
    {
        var sign = x.Direction >= 0 ? "+" : "-";
        if (x.GroupCode == "DOVIZ") return $"{sign}{Math.Abs(x.Quantity):N4} {x.ItemName}";
        if (x.GroupCode == "ZIYNET") return $"{sign}{Math.Abs(x.Quantity):N3} adet";
        if (x.GroupCode == "ISCILIKLI")
            return $"{sign}{Math.Abs(x.Gram ?? 0m):N3} gr | {Math.Abs(x.HasEquivalent ?? 0m):N6} has | {Math.Abs(x.TotalPriceTl ?? 0m):N2} TL";
        if (x.TotalPriceTl.HasValue) return $"{Math.Abs(x.TotalPriceTl.Value):N2} TL";
        return x.Quantity.ToString("N4", CultureInfo.CurrentCulture);
    }

    private static string FormatSupplierKalem(SupplierTransaction x)
        => x.IsConverted ? $"{x.SourceUnit} → {x.TargetUnit}" : x.TargetUnit;

    private static string FormatSupplierValue(SupplierTransaction x)
    {
        if (x.IsConverted)
            return $"-{x.SourceAmount:N4} {x.SourceUnit} => {x.TargetAmount:N4} {x.TargetUnit}";
        var sign = x.TxType == "PAYMENT" ? "-" : "+";
        return $"{sign}{x.TargetAmount:N4} {x.TargetUnit}";
    }

    private static void ApplyCustomerBalanceDelta(CustomerBalance b, string unit, decimal delta)
    {
        switch (NormalizeUnit(unit))
        {
            case "USD": b.BalanceUSD += delta; break;
            case "EUR": b.BalanceEUR += delta; break;
            case "GBP": b.BalanceGBP += delta; break;
            case "HAS": b.BalanceHAS += delta; break;
            default: b.BalanceTL += delta; break;
        }
    }

    private static void ApplySupplierBalanceDelta(SupplierBalance b, string unit, decimal delta)
    {
        switch (NormalizeUnit(unit))
        {
            case "USD": b.BalanceUSD += delta; break;
            case "EUR": b.BalanceEUR += delta; break;
            case "GBP": b.BalanceGBP += delta; break;
            case "HAS": b.BalanceHAS += delta; break;
            case "GUMUS": b.BalanceGUMUS += delta; break;
            default: b.BalanceTL += delta; break;
        }
    }

    private static string NormalizeUnit(string? raw)
    {
        var u = (raw ?? "").Trim().ToUpperInvariant();
        return u switch
        {
            "TRY" => "TL",
            "TL" => "TL",
            "USD" => "USD",
            "EUR" => "EUR",
            "GBP" => "GBP",
            "HAS" => "HAS",
            "GUMUS" => "GUMUS",
            "GÜMÜŞ" => "GUMUS",
            _ => "TL"
        };
    }

    private static bool TryResolveCustomerTransferId(CustomerTransaction anchor, out Guid transferId)
    {
        transferId = Guid.Empty;
        if (CariTransferMarker.TryParse(anchor.Note, out transferId, out _, out _, out _, out _, out _) &&
            transferId != Guid.Empty)
            return true;

        if (anchor.RefId.HasValue && anchor.RefId != Guid.Empty &&
            string.Equals(anchor.RefType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
        {
            transferId = anchor.RefId.Value;
            return true;
        }

        return false;
    }

    private static bool TryResolveSupplierTransferId(SupplierTransaction anchor, out Guid transferId)
    {
        transferId = Guid.Empty;
        if (CariTransferMarker.TryParse(anchor.Description, out transferId, out _, out _, out _, out _, out _) &&
            transferId != Guid.Empty)
            return true;

        if (anchor.RefId.HasValue && anchor.RefId != Guid.Empty &&
            string.Equals(anchor.RefType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
        {
            transferId = anchor.RefId.Value;
            return true;
        }

        return false;
    }

    private static bool IsCustomerCariTransferRow(CustomerTransaction row, Guid transferId)
    {
        if (row.RefId != transferId) return false;
        if (!CariTransferMarker.TryParse(row.Note, out var parsedId, out _, out _, out _, out _, out _))
            return false;
        return parsedId == transferId;
    }

    private static bool IsSupplierCariTransferRow(SupplierTransaction row, Guid transferId)
    {
        if (row.RefId != transferId) return false;
        if (string.Equals(row.RefType, "TRANSFER", StringComparison.OrdinalIgnoreCase))
            return true;
        return CariTransferMarker.TryParse(row.Description, out var parsedId, out _, out _, out _, out _, out _) &&
               parsedId == transferId;
    }

    private static string BuildTransferReversalAciklama(string transferAciklama, string reason)
    {
        var transferText = (transferAciklama ?? "").Trim();
        var reasonText = (reason ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(transferText) && transferText != "Transfer")
            return string.IsNullOrWhiteSpace(reasonText) ? transferText : $"{transferText} — {reasonText}";
        return reasonText;
    }

    private async Task<ReverseResult> ReverseCariTransferAsync(
        Guid tenantId, Guid branchId, Guid transferId,
        string reason, Guid? userId, string? userName, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var reversedAt = DateTime.UtcNow;
            var customerCandidates = await _db.CustomerTransactions
                .Where(x => x.TenantId == tenantId && x.RefId == transferId && !x.IsDeleted && !x.IsReversed)
                .ToListAsync(ct);
            var customerRows = customerCandidates
                .Where(x => IsCustomerCariTransferRow(x, transferId))
                .ToList();
            var supplierCandidates = await _db.SupplierTransactions
                .Where(x => x.TenantId == tenantId && x.RefId == transferId && !x.IsDeleted && !x.IsReversed)
                .ToListAsync(ct);
            var supplierRows = supplierCandidates
                .Where(x => IsSupplierCariTransferRow(x, transferId))
                .ToList();

            if (customerRows.Count == 0 && supplierRows.Count == 0)
                return new ReverseResult(false, "Transfer kaydı bulunamadı veya zaten geri alınmış.", null, null);

            foreach (var grp in customerRows.GroupBy(x => x.CustomerId))
            {
                var bal = await CustomerFinanceHelper.GetOrCreateBalanceAsync(_db, tenantId, grp.Key, ct);
                foreach (var row in grp)
                {
                    if (string.Equals(row.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase))
                    {
                        row.IsDeleted = true;
                    }
                    else if (string.Equals(row.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(row.ItemName, "ISCILIKLI_GUNCELLEME", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseName = row.ItemType ?? row.ItemName;
                        var restored = await _db.CustomerTransactions
                            .IgnoreQueryFilters()
                            .Where(r => r.TenantId == tenantId && r.CustomerId == grp.Key &&
                                        r.IsDeleted && r.GroupCode == "ISCILIKLI" &&
                                        (r.ItemName == baseName || r.ItemType == baseName))
                            .OrderByDescending(r => r.TxDate)
                            .FirstOrDefaultAsync(ct);
                        if (restored != null) restored.IsDeleted = false;
                    }
                    else
                    {
                        ReverseCustomerTransactionRow(bal, row);
                    }

                    row.IsReversed = true;
                    row.ReversedAt = reversedAt;
                }
                bal.UpdatedAt = reversedAt;
            }

            foreach (var grp in supplierRows.GroupBy(x => x.SupplierId))
            {
                var bal = await _db.SupplierBalances
                    .FirstOrDefaultAsync(x => x.SupplierId == grp.Key && x.TenantId == tenantId && !x.IsDeleted, ct);
                if (bal is null)
                {
                    bal = new SupplierBalance { TenantId = tenantId, SupplierId = grp.Key };
                    _db.SupplierBalances.Add(bal);
                }

                foreach (var row in grp)
                {
                    if (string.Equals(row.TxType, "ZIYNET", StringComparison.OrdinalIgnoreCase))
                    {
                        _db.SupplierTransactions.Add(new SupplierTransaction
                        {
                            TenantId = tenantId,
                            SupplierId = grp.Key,
                            BranchId = row.BranchId,
                            TxType = "ZIYNET",
                            SourceUnit = row.SourceUnit,
                            SourceAmount = row.SourceAmount,
                            TargetUnit = row.TargetUnit,
                            TargetAmount = -row.TargetAmount,
                            IsConverted = false,
                            SourceUnitTlRate = 1m,
                            TargetUnitTlRate = 1m,
                            Description = $"[TRANSFER_REVERSE]|REF={transferId:D}|{row.Description}",
                            TxDate = reversedAt,
                            RefType = "TRANSFER_REVERSE",
                            RefId = transferId,
                            BatchId = row.BatchId,
                            UserId = userId,
                            KullaniciAdi = userName
                        });
                    }
                    else
                    {
                        ApplySupplierBalanceDelta(bal, row.TargetUnit, -row.TargetAmount);
                    }

                    row.IsReversed = true;
                    row.ReversedAt = reversedAt;
                }
                bal.UpdatedAt = reversedAt;
            }

            var logs = new List<TransactionReversalLog>();
            Guid? firstLogId = null;

            foreach (var grp in customerRows.GroupBy(x => x.CustomerId))
            {
                var anchorRow = grp.OrderBy(x => x.TxDate).First();
                var transferAciklama = CariTransferMarker.FormatDisplayNote(anchorRow.Note);
                var displayAciklama = BuildTransferReversalAciklama(transferAciklama, reason);
                var log = BuildLog(
                    tenantId,
                    "Customer",
                    grp.Key,
                    branchId,
                    transferId,
                    "TRANSFER",
                    anchorRow.TxDate,
                    anchorRow.KullaniciAdi,
                    reason,
                    userId,
                    userName,
                    reversedAt,
                    "TRANSFER",
                    "Cari Transfer",
                    grp.Count() > 1 ? $"{grp.Count()} kalem" : FormatCustomerValue(anchorRow),
                    "Transfer",
                    displayAciklama,
                    JsonSerializer.Serialize(new
                    {
                        transferId,
                        partyType = "Customer",
                        partyId = grp.Key,
                        customerRows = grp.Select(r => r.Id)
                    }));
                logs.Add(log);
                firstLogId ??= log.Id;
                foreach (var row in grp)
                    row.ReversalLogId = log.Id;
            }

            foreach (var grp in supplierRows.GroupBy(x => x.SupplierId))
            {
                var anchorRow = grp.OrderBy(x => x.TxDate).First();
                var transferAciklama = CariTransferMarker.FormatDisplayNote(anchorRow.Description);
                var displayAciklama = BuildTransferReversalAciklama(transferAciklama, reason);
                var log = BuildLog(
                    tenantId,
                    "Supplier",
                    grp.Key,
                    branchId,
                    transferId,
                    "TRANSFER",
                    anchorRow.TxDate,
                    anchorRow.KullaniciAdi,
                    reason,
                    userId,
                    userName,
                    reversedAt,
                    "TRANSFER",
                    "Cari Transfer",
                    grp.Count() > 1 ? $"{grp.Count()} kalem" : FormatSupplierValue(anchorRow),
                    "Transfer",
                    displayAciklama,
                    JsonSerializer.Serialize(new
                    {
                        transferId,
                        partyType = "Supplier",
                        partyId = grp.Key,
                        supplierRows = grp.Select(r => r.Id)
                    }));
                logs.Add(log);
                firstLogId ??= log.Id;
                foreach (var row in grp)
                    row.ReversalLogId = log.Id;
            }

            foreach (var log in logs)
                _db.TransactionReversalLogs.Add(log);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ReverseResult(true, null, firstLogId, transferId);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            return new ReverseResult(false, ex.Message, null, null);
        }
    }
}
