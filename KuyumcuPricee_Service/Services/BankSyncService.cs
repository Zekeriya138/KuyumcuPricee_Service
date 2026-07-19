using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public interface IBankSyncService
{
    Task<BankSyncImportResult> ImportVomsisTransactionsAsync(
        Guid tenantId,
        Guid branchId,
        IReadOnlyList<VomsisTransactionImportDto> transactions,
        CancellationToken ct);

    Task<BankImportListResult> ListAsync(
        Guid tenantId,
        Guid branchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken ct);

    Task<BankImportActionResult> MatchAndCreateDraftAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        Guid customerId,
        CancellationToken ct);

    Task<BankImportActionResult> RejectAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        string? reason,
        CancellationToken ct);

    Task<BankSyncPullResult> PullFromVomsisAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct);
}

public sealed class BankSyncPullResult
{
    public int FetchedFromVomsis { get; set; }
    public int AfterAccountFilter { get; set; }
    public int Received { get; set; }
    public int Imported { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedFilter { get; set; }
    public int DraftCreated { get; set; }
    public int PendingReview { get; set; }
    public int MissingTaxId { get; set; }
    public int NoCustomerMatch { get; set; }
    public string? DetectedAccountIds { get; set; }
    public string? SummaryMessage { get; set; }
}

public sealed class BankSyncImportResult
{
    public int Received { get; set; }
    public int Imported { get; set; }
    public int SkippedDuplicate { get; set; }
    public int SkippedFilter { get; set; }
    public int DraftCreated { get; set; }
    public int PendingReview { get; set; }
    public int MissingTaxId { get; set; }
    public int NoCustomerMatch { get; set; }
}

public sealed class BankImportListResult
{
    public int Total { get; set; }
    public List<BankImportTransactionDto> Items { get; set; } = new();
}

public sealed class BankImportTransactionDto
{
    public Guid Id { get; set; }
    public long ExternalId { get; set; }
    public string ExternalKey { get; set; } = "";
    public int? VomsisAccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public string TransactionType { get; set; } = "";
    public string? Description { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyTaxNo { get; set; }
    public DateTime TransactionDateUtc { get; set; }
    public string Status { get; set; } = "";
    public string? StatusMessage { get; set; }
    public Guid? MatchedCustomerId { get; set; }
    public string? MatchedCustomerName { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? EInvoiceDocumentId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class BankImportActionResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public Guid? InvoiceId { get; set; }
    public Guid? EInvoiceDocumentId { get; set; }
    public string? Status { get; set; }
}

public sealed class VomsisTransactionImportDto
{
    public long ExternalId { get; set; }
    public string ExternalKey { get; set; } = "";
    public int? VomsisAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
    public string? Type { get; set; }
    public string? Description { get; set; }
    public DateTime? TransactionDateUtc { get; set; }
    public string? SenderName { get; set; }
    public string? SenderTaxNo { get; set; }
    public string? SenderIban { get; set; }
}

public sealed class BankSyncService : IBankSyncService
{
    private const string ProviderVomsis = "Vomsis";
    private readonly AppDbContext _db;
    private readonly IEInvoiceWorkflowService _workflow;
    private readonly VomsisApiClient _vomsis;
    private readonly ICounterpartyTaxResolver _taxResolver;

    public BankSyncService(
        AppDbContext db,
        IEInvoiceWorkflowService workflow,
        VomsisApiClient vomsis,
        ICounterpartyTaxResolver taxResolver)
    {
        _db = db;
        _workflow = workflow;
        _vomsis = vomsis;
        _taxResolver = taxResolver;
    }

    public async Task<BankSyncPullResult> PullFromVomsisAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        var profile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct)
            ?? throw new InvalidOperationException("Vomsis ayarları bulunamadı. Önce Vomsis Ayarları sekmesinden kaydedin.");

        if (!profile.IsEnabled)
            throw new InvalidOperationException("Banka sync profili devre dışı.");
        if (string.IsNullOrWhiteSpace(profile.VomsisAppKey) || string.IsNullOrWhiteSpace(profile.VomsisAppSecret))
            throw new InvalidOperationException("Vomsis App Key / Secret eksik.");

        _vomsis.Configure(profile.VomsisAppKey, profile.VomsisAppSecret);

        var lookbackDays = Math.Clamp(profile.LookbackDays, 1, 7);
        var endUtc = DateTime.UtcNow;
        var beginUtc = endUtc.AddDays(-lookbackDays);

        var raw = await _vomsis.GetTransactionsAsync(beginUtc, endUtc, ct);
        var allowed = ParseAccountIds(profile.AllowedAccountIds).ToHashSet();
        var detectedAccountIds = raw
            .Select(tx => tx.BankAccountId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var mapped = raw.Select(VomsisTransactionMapper.ToImportDto).ToList();

        var import = await ImportVomsisTransactionsAsync(tenantId, branchId, mapped, ct);
        var eligibleCount = mapped.Count(tx =>
            allowed.Count == 0 || (tx.VomsisAccountId.HasValue && allowed.Contains(tx.VomsisAccountId.Value)));
        var pull = new BankSyncPullResult
        {
            FetchedFromVomsis = raw.Count,
            AfterAccountFilter = eligibleCount,
            Received = import.Received,
            Imported = import.Imported,
            SkippedDuplicate = import.SkippedDuplicate,
            SkippedFilter = import.SkippedFilter,
            DraftCreated = import.DraftCreated,
            PendingReview = import.PendingReview,
            MissingTaxId = import.MissingTaxId,
            NoCustomerMatch = import.NoCustomerMatch,
            DetectedAccountIds = detectedAccountIds.Length == 0 ? null : string.Join(", ", detectedAccountIds),
            SummaryMessage = BuildPullSummary(raw.Count, eligibleCount, allowed, detectedAccountIds, import)
        };
        return pull;
    }

    private static string BuildPullSummary(
        int fetched,
        int eligibleForProcessing,
        HashSet<int> allowedAccounts,
        int[] detectedAccountIds,
        BankSyncImportResult import)
    {
        if (fetched == 0)
            return "Vomsis'te seçilen tarih aralığında hareket bulunamadı.";

        if (import.Imported == 0 && import.SkippedDuplicate == fetched)
            return $"{fetched} hareket zaten ERP'de kayıtlı (mükerrer).";

        if (eligibleForProcessing == 0 && import.Imported > 0)
        {
            return $"Vomsis'ten {fetched} hareket ERP'ye kaydedildi ({import.SkippedFilter} adet atlandı — TL hesap dışı/EUR). " +
                   "Listede 'Atlandı' olarak görünür; e-fatura taslağı yalnızca TL gelen havaleler için oluşturulur.";
        }

        if (eligibleForProcessing == 0)
        {
            var allowedText = string.Join(", ", allowedAccounts.OrderBy(x => x));
            var detectedText = detectedAccountIds.Length == 0 ? "bilinmiyor" : string.Join(", ", detectedAccountIds);
            return $"Vomsis'ten {fetched} hareket geldi; işlenecek TL hesap ({allowedText}) hareketi yok. " +
                   $"Gelen hareketler hesap {detectedText} üzerinde.";
        }

        return $"Vomsis: {fetched} hareket, ERP'ye {import.Imported} kayıt " +
               $"(taslak: {import.DraftCreated}, bekleyen: {import.PendingReview}, atlandı: {import.SkippedFilter}).";
    }

    private static int[] ParseAccountIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [46];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .ToArray();
    }

    public async Task<BankSyncImportResult> ImportVomsisTransactionsAsync(
        Guid tenantId,
        Guid branchId,
        IReadOnlyList<VomsisTransactionImportDto> transactions,
        CancellationToken ct)
    {
        var result = new BankSyncImportResult { Received = transactions?.Count ?? 0 };
        if (transactions is null || transactions.Count == 0)
            return result;

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId, ct);

        var bankProfile = await _db.BankSyncProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        var allowedAccounts = ParseAccountIds(bankProfile?.AllowedAccountIds).ToHashSet();

        foreach (var tx in transactions)
        {
            if (string.IsNullOrWhiteSpace(tx.ExternalKey))
            {
                result.SkippedFilter++;
                continue;
            }

            var exists = await _db.BankImportTransactions
                .IgnoreQueryFilters()
                .AnyAsync(x =>
                    x.TenantId == tenantId &&
                    x.BranchId == branchId &&
                    x.Provider == ProviderVomsis &&
                    x.ExternalKey == tx.ExternalKey &&
                    !x.IsDeleted, ct);
            if (exists)
            {
                result.SkippedDuplicate++;
                continue;
            }

            var currency = NormalizeCurrency(tx.Currency);
            var txType = (tx.Type ?? "").Trim().ToLowerInvariant();
            var amount = decimal.Round(Math.Abs(tx.Amount), 2, MidpointRounding.AwayFromZero);

            if (allowedAccounts.Count > 0 &&
                (!tx.VomsisAccountId.HasValue || !allowedAccounts.Contains(tx.VomsisAccountId.Value)))
            {
                var skippedAccount = CreateEntity(tenantId, branchId, tx, currency, txType, amount);
                ApplyCounterpartyFields(skippedAccount, tx);
                skippedAccount.Status = BankImportStatuses.Skipped;
                skippedAccount.StatusMessage = tx.VomsisAccountId.HasValue
                    ? $"Hesap {tx.VomsisAccountId} izin dışı (yalnızca TL hesap {string.Join(",", allowedAccounts.OrderBy(x => x))})."
                    : "Hesap bilgisi yok; TL hesap filtresi uygulanamadı.";
                _db.BankImportTransactions.Add(skippedAccount);
                result.SkippedFilter++;
                result.Imported++;
                continue;
            }

            if (!IsQualifyingIncomingTransfer(txType, amount, currency, tx.Description))
            {
                var skipped = CreateEntity(tenantId, branchId, tx, currency, txType, amount);
                ApplyCounterpartyFields(skipped, tx);
                skipped.Status = BankImportStatuses.Skipped;
                skipped.StatusMessage = IsTryCurrency(currency)
                    ? "Filtre dışı hareket (TL gelen havale değil)."
                    : $"Filtre dışı hareket ({currency} — yalnızca TL gelen havale işlenir).";
                _db.BankImportTransactions.Add(skipped);
                result.SkippedFilter++;
                result.Imported++;
                continue;
            }

            var entity = CreateEntity(tenantId, branchId, tx, currency, txType, amount);
            ApplyCounterpartyFields(entity, tx);

            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                entity.CounterpartyName,
                entity.CounterpartyTaxNo,
                entity.CounterpartyIban,
                entity.Description,
                IsIncomingTransfer: true), ct);

            if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.TaxNo))
            {
                entity.Status = BankImportStatuses.MissingTaxId;
                entity.StatusMessage = resolved.Message ?? "TCKN/VKN bulunamadı.";
                if (resolved.CustomerId.HasValue)
                    entity.MatchedCustomerId = resolved.CustomerId;
                result.MissingTaxId++;
                result.PendingReview++;
                _db.BankImportTransactions.Add(entity);
                result.Imported++;
                continue;
            }

            entity.CounterpartyTaxNo = resolved.TaxNo;
            if (!string.IsNullOrWhiteSpace(resolved.DisplayName))
                entity.CounterpartyName = resolved.DisplayName;

            CustomerMatchRow? customer = null;
            if (resolved.CustomerId.HasValue)
            {
                customer = await _db.Customers.AsNoTracking()
                    .Where(x => x.Id == resolved.CustomerId.Value && !x.IsDeleted)
                    .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                var customerRows = await _db.Customers.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.CariTip == 0)
                    .Select(x => new CustomerMatchRow(x.Id, x.FullName, x.NationalId, x.Address, x.City, x.District, x.Email))
                    .ToListAsync(ct);
                customer = ResolveCustomer(customerRows, entity.CounterpartyName, entity.CounterpartyTaxNo);
            }

            if (customer is not null)
            {
                entity.MatchedCustomerId = customer.Id;
                var taxNo = entity.CounterpartyTaxNo!;

                if (ShouldAutoDraft(profile, amount))
                {
                    try
                    {
                        var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                            tenantId, branchId, customer, taxNo, amount, entity, ct);
                        entity.Status = BankImportStatuses.DraftCreated;
                        entity.StatusMessage = resolved.IsNihaiTuketici
                            ? "Otomatik taslak (nihai tüketici)."
                            : $"Otomatik taslak oluşturuldu ({resolved.Source}).";
                        entity.InvoiceId = invoiceId;
                        entity.EInvoiceDocumentId = documentId;
                        result.DraftCreated++;
                        await _taxResolver.LearnAsync(new CounterpartyLearnInput(
                            tenantId, entity.CounterpartyName, entity.CounterpartyIban, taxNo,
                            resolved.Source ?? CounterpartyIdentitySources.BankImport, customer.Id), ct);
                    }
                    catch (Exception ex)
                    {
                        entity.Status = BankImportStatuses.Pending;
                        entity.StatusMessage = "Taslak oluşturulamadı: " + ex.Message;
                        result.PendingReview++;
                    }
                }
                else
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = "Eşleşti; e-fatura otomatik taslak ayarları kapalı veya tutar aralığı dışında.";
                    result.PendingReview++;
                }
            }
            else if (resolved.IsNihaiTuketici)
            {
                try
                {
                    var nihaiCustomer = await EnsureNihaiCustomerAsync(tenantId, branchId, ct);
                    entity.MatchedCustomerId = nihaiCustomer.Id;
                    var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                        tenantId, branchId, nihaiCustomer, resolved.TaxNo!, amount, entity, ct);
                    entity.Status = BankImportStatuses.DraftCreated;
                    entity.StatusMessage = "Otomatik taslak (nihai tüketici).";
                    entity.InvoiceId = invoiceId;
                    entity.EInvoiceDocumentId = documentId;
                    result.DraftCreated++;
                    await _taxResolver.LearnAsync(new CounterpartyLearnInput(
                        tenantId, entity.CounterpartyName, entity.CounterpartyIban, resolved.TaxNo!,
                        CounterpartyIdentitySources.NihaiTuketici, nihaiCustomer.Id), ct);
                }
                catch (Exception ex)
                {
                    entity.Status = BankImportStatuses.Pending;
                    entity.StatusMessage = "Nihai tüketici taslağı oluşturulamadı: " + ex.Message;
                    result.PendingReview++;
                }
            }
            else
            {
                entity.Status = BankImportStatuses.NoCustomerMatch;
                entity.StatusMessage = resolved.Message ?? "Karşı taraf cari kayıtlarda bulunamadı.";
                result.NoCustomerMatch++;
                result.PendingReview++;
            }

            _db.BankImportTransactions.Add(entity);
            result.Imported++;
        }

        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<BankImportListResult> ListAsync(
        Guid tenantId,
        Guid branchId,
        string? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var q = _db.BankImportTransactions
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId);

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status.Trim());

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(x => x.TransactionDateUtc)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.ExternalId,
                x.ExternalKey,
                x.VomsisAccountId,
                x.Amount,
                x.Currency,
                x.TransactionType,
                x.Description,
                x.CounterpartyName,
                x.CounterpartyTaxNo,
                x.TransactionDateUtc,
                x.Status,
                x.StatusMessage,
                x.MatchedCustomerId,
                MatchedCustomerName = x.MatchedCustomerId.HasValue
                    ? _db.Customers.Where(c => c.Id == x.MatchedCustomerId.Value).Select(c => c.FullName).FirstOrDefault()
                    : null,
                x.InvoiceId,
                x.EInvoiceDocumentId,
                x.CreatedAt
            })
            .ToListAsync(ct);

        return new BankImportListResult
        {
            Total = total,
            Items = rows.Select(x => new BankImportTransactionDto
            {
                Id = x.Id,
                ExternalId = x.ExternalId,
                ExternalKey = x.ExternalKey,
                VomsisAccountId = x.VomsisAccountId,
                Amount = x.Amount,
                Currency = x.Currency,
                TransactionType = x.TransactionType,
                Description = x.Description,
                CounterpartyName = x.CounterpartyName,
                CounterpartyTaxNo = x.CounterpartyTaxNo,
                TransactionDateUtc = x.TransactionDateUtc,
                Status = x.Status,
                StatusMessage = x.StatusMessage,
                MatchedCustomerId = x.MatchedCustomerId,
                MatchedCustomerName = x.MatchedCustomerName,
                InvoiceId = x.InvoiceId,
                EInvoiceDocumentId = x.EInvoiceDocumentId,
                CreatedAt = x.CreatedAt
            }).ToList()
        };
    }

    public async Task<BankImportActionResult> MatchAndCreateDraftAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        Guid customerId,
        CancellationToken ct)
    {
        var tx = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == transactionId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId, ct);
        if (tx is null)
            return Fail("Hareket bulunamadı.");

        if (tx.Status == BankImportStatuses.DraftCreated && tx.InvoiceId.HasValue)
            return Fail("Bu hareket için zaten taslak oluşturulmuş.");

        if (tx.Status == BankImportStatuses.Rejected)
            return Fail("Reddedilmiş hareket için taslak oluşturulamaz.");

        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.Id == customerId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                !x.IsDeleted, ct);
        if (customer is null)
            return Fail("Müşteri bulunamadı.");

        var taxNo = NormalizeTaxNo(customer.NationalId);
        if (!CounterpartyTaxResolverService.IsAcceptableTaxNo(taxNo))
        {
            var resolved = await _taxResolver.ResolveAsync(new CounterpartyResolveInput(
                tenantId,
                branchId,
                tx.CounterpartyName,
                tx.CounterpartyTaxNo,
                tx.CounterpartyIban,
                tx.Description,
                IsIncomingTransfer: true,
                AllowNihaiTuketici: true), ct);
            taxNo = resolved.TaxNo ?? NormalizeTaxNo(tx.CounterpartyTaxNo);
            if (!CounterpartyTaxResolverService.IsAcceptableTaxNo(taxNo))
            {
                tx.MatchedCustomerId = customer.Id;
                tx.Status = BankImportStatuses.MissingTaxId;
                tx.StatusMessage = resolved.Message ?? "Müşteri seçildi ancak geçerli TCKN/VKN bulunamadı.";
                await _db.SaveChangesAsync(ct);
                return new BankImportActionResult
                {
                    Success = false,
                    Message = tx.StatusMessage,
                    Status = tx.Status
                };
            }
        }

        try
        {
            var row = new CustomerMatchRow(customer.Id, customer.FullName, customer.NationalId, customer.Address, customer.City, customer.District, customer.Email);
            var (invoiceId, documentId) = await CreateDraftForCustomerAsync(
                tenantId, branchId, row, taxNo, tx.Amount, tx, ct);
            tx.MatchedCustomerId = customer.Id;
            tx.CounterpartyTaxNo = taxNo;
            tx.Status = BankImportStatuses.DraftCreated;
            tx.StatusMessage = "Manuel eşleştirme ile taslak oluşturuldu.";
            tx.InvoiceId = invoiceId;
            tx.EInvoiceDocumentId = documentId;
            await _db.SaveChangesAsync(ct);
            await _taxResolver.LearnAsync(new CounterpartyLearnInput(
                tenantId, tx.CounterpartyName, tx.CounterpartyIban, taxNo,
                CounterpartyIdentitySources.BankImport, customer.Id), ct);
            return new BankImportActionResult
            {
                Success = true,
                InvoiceId = invoiceId,
                EInvoiceDocumentId = documentId,
                Status = tx.Status
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<BankImportActionResult> RejectAsync(
        Guid tenantId,
        Guid branchId,
        Guid transactionId,
        string? reason,
        CancellationToken ct)
    {
        var tx = await _db.BankImportTransactions
            .FirstOrDefaultAsync(x =>
                x.Id == transactionId &&
                x.TenantId == tenantId &&
                x.BranchId == branchId, ct);
        if (tx is null)
            return Fail("Hareket bulunamadı.");

        tx.Status = BankImportStatuses.Rejected;
        tx.StatusMessage = string.IsNullOrWhiteSpace(reason) ? "Kullanıcı tarafından reddedildi." : reason.Trim();
        await _db.SaveChangesAsync(ct);
        return new BankImportActionResult { Success = true, Status = tx.Status, Message = tx.StatusMessage };
    }

    private async Task<CustomerMatchRow> EnsureNihaiCustomerAsync(
        Guid tenantId,
        Guid branchId,
        CancellationToken ct)
    {
        var nihaiTax = CounterpartyTaxResolverService.NihaiTuketiciTckn;
        var existing = await _db.Customers
            .AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                !x.IsDeleted &&
                x.CariTip == 0 &&
                x.NationalId != null)
            .ToListAsync(ct);

        var match = existing.FirstOrDefault(c =>
            string.Equals(NormalizeTaxNo(c.NationalId), nihaiTax, StringComparison.Ordinal));
        if (match is not null)
        {
            return new CustomerMatchRow(
                match.Id, match.FullName, match.NationalId, match.Address, match.City, match.District, match.Email);
        }

        var customer = new Customer
        {
            TenantId = tenantId,
            BranchId = branchId,
            CariTip = 0,
            FullName = CounterpartyTaxResolverService.NihaiTuketiciName,
            NationalId = nihaiTax
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);
        return new CustomerMatchRow(
            customer.Id, customer.FullName, customer.NationalId, customer.Address, customer.City, customer.District, customer.Email);
    }

    private async Task<(Guid InvoiceId, Guid DocumentId)> CreateDraftForCustomerAsync(
        Guid tenantId,
        Guid branchId,
        CustomerMatchRow customer,
        string taxNo,
        decimal amount,
        BankImportTransaction entity,
        CancellationToken ct)
    {
        var description = string.IsNullOrWhiteSpace(entity.Description)
            ? $"Banka tahsilatı - {customer.FullName}"
            : entity.Description;

        return await _workflow.CreateCollectionDraftAsync(new CollectionDraftInput(
            tenantId,
            branchId,
            customer.Id,
            customer.FullName,
            taxNo,
            customer.Address,
            customer.City,
            customer.District,
            customer.Email,
            amount,
            description,
            entity.TransactionDateUtc == default ? DateTime.UtcNow : entity.TransactionDateUtc,
            null), ct);
    }

    private static void ApplyCounterpartyFields(BankImportTransaction entity, VomsisTransactionImportDto tx)
    {
        entity.CounterpartyName = CoalesceText(tx.SenderName, BankMovementParser.ExtractCounterpartyName(tx.Description));
        entity.CounterpartyTaxNo = NormalizeTaxNo(CoalesceText(
            tx.SenderTaxNo,
            BankMovementParser.ExtractTaxNoFromDescription(tx.Description)));
        entity.CounterpartyIban = NormalizeIban(tx.SenderIban);
    }

    private static BankImportTransaction CreateEntity(
        Guid tenantId,
        Guid branchId,
        VomsisTransactionImportDto tx,
        string currency,
        string txType,
        decimal amount)
    {
        return new BankImportTransaction
        {
            TenantId = tenantId,
            BranchId = branchId,
            Provider = ProviderVomsis,
            ExternalId = tx.ExternalId,
            ExternalKey = tx.ExternalKey.Trim(),
            VomsisAccountId = tx.VomsisAccountId,
            Amount = amount,
            Currency = currency,
            TransactionType = txType,
            Description = tx.Description?.Trim(),
            TransactionDateUtc = tx.TransactionDateUtc ?? DateTime.UtcNow
        };
    }

    private static bool IsQualifyingIncomingTransfer(string txType, decimal amount, string currency, string? description)
    {
        if (amount <= 0m) return false;
        if (!string.Equals(txType, "alacakli", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsTryCurrency(currency)) return false;

        var desc = (description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(desc)) return true;

        var upper = desc.ToUpperInvariant();
        if (upper.Contains("GELEN HAVALE") || upper.Contains("GELEN EFT") || upper.Contains("GELEN"))
            return true;
        if (upper.Contains("GİDEN") || upper.Contains("GIDEN") || upper.Contains("BORÇ") || upper.Contains("BORC"))
            return false;

        return true;
    }

    private static bool ShouldAutoDraft(EInvoiceProfile? profile, decimal amountTl)
    {
        var settings = EInvoiceProfileSettingsCodec.Decode(profile?.IntegratorCompanyCode);
        if (!settings.AutoDraftEnabled) return false;

        var allowed = settings.AutoDraftAllowedPaymentMethods ?? [];
        if (allowed.Count > 0 &&
            !allowed.Any(m => string.Equals(m, "Tahsilat", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (settings.AutoDraftMinTotal.HasValue && settings.AutoDraftMinTotal.Value > 0m && amountTl < settings.AutoDraftMinTotal.Value)
            return false;
        if (settings.AutoDraftMaxTotal.HasValue && settings.AutoDraftMaxTotal.Value > 0m && amountTl > settings.AutoDraftMaxTotal.Value)
            return false;

        return true;
    }

    private static CustomerMatchRow? ResolveCustomer(
        IReadOnlyList<CustomerMatchRow> customers,
        string? counterpartyName,
        string? counterpartyTaxNo)
    {
        if (!string.IsNullOrWhiteSpace(counterpartyTaxNo))
        {
            var byTax = customers.FirstOrDefault(c =>
                string.Equals(NormalizeTaxNo(c.NationalId), counterpartyTaxNo, StringComparison.Ordinal));
            if (byTax is not null) return byTax;
        }

        if (string.IsNullOrWhiteSpace(counterpartyName))
            return null;

        var normalizedTarget = BankMovementParser.NormalizeName(counterpartyName);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
            return null;

        var exact = customers.FirstOrDefault(c =>
            string.Equals(BankMovementParser.NormalizeName(c.FullName), normalizedTarget, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var containsMatches = customers
            .Where(c =>
            {
                var n = BankMovementParser.NormalizeName(c.FullName);
                return n.Contains(normalizedTarget, StringComparison.Ordinal) ||
                       normalizedTarget.Contains(n, StringComparison.Ordinal);
            })
            .ToList();
        if (containsMatches.Count == 1)
            return containsMatches[0];

        return containsMatches
            .OrderByDescending(c => BankMovementParser.NameSimilarity(c.FullName, counterpartyName))
            .FirstOrDefault(c => BankMovementParser.NameSimilarity(c.FullName, counterpartyName) >= 0.72);
    }

    private static bool IsTryCurrency(string currency)
        => string.Equals(currency, "TRY", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(currency, "TL", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCurrency(string? value)
    {
        var c = (value ?? "TRY").Trim().ToUpperInvariant();
        return c switch
        {
            "TL" => "TRY",
            "TRY" => "TRY",
            "EUR" or "EURO" => "EUR",
            "USD" => "USD",
            _ => c
        };
    }

    private static string NormalizeTaxNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static bool IsValidTaxNo(string taxNo)
        => CounterpartyTaxResolverService.IsAcceptableTaxNo(taxNo);

    private static string? NormalizeIban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = new string(value.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        return string.IsNullOrWhiteSpace(compact) ? null : compact;
    }

    private static string? CoalesceText(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return null;
    }

    private static BankImportActionResult Fail(string message)
        => new() { Success = false, Message = message };

    private sealed record CustomerMatchRow(
        Guid Id,
        string FullName,
        string? NationalId,
        string? Address,
        string? City,
        string? District,
        string? Email);
}

public static class BankMovementParser
{
    private static readonly string[] DescriptionPrefixes =
    [
        "Gelen Havale Ödemesi",
        "Gelen Havale",
        "Gelen EFT",
        "Gelen Fast",
        "Gelen FAST",
        "GELEN HAVALE ÖDEMESİ",
        "GELEN HAVALE",
        "HAVALE"
    ];

    public static string? ExtractCounterpartyName(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var text = description.Trim();
        foreach (var prefix in DescriptionPrefixes.OrderByDescending(p => p.Length))
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim(' ', '-', ':', '.');
                break;
            }
        }

        text = Regex.Replace(text, @"\b(VKN|TCKN|TC)\s*:?\s*\d+\b", "", RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Açıklamadaki 11 haneli rakamı TCKN olarak döndürür; etiketli TCKN/TC ve 10 haneli VKN de desteklenir.</summary>
    public static string? ExtractTaxNoFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var labeledTc = Regex.Match(description, @"(?:TCKN|TC\s*KIMLIK|TC)\s*:?\s*(\d{11})", RegexOptions.IgnoreCase);
        if (labeledTc.Success)
            return labeledTc.Groups[1].Value;

        foreach (Match match in Regex.Matches(description, @"(?<!\d)(\d{11})(?!\d)"))
            return match.Groups[1].Value;

        var labeledVkn = Regex.Match(description, @"(?:VKN|VERGI\s*NO)\s*:?\s*(\d{10})", RegexOptions.IgnoreCase);
        if (labeledVkn.Success)
            return labeledVkn.Groups[1].Value;

        foreach (Match match in Regex.Matches(description, @"(?<!\d)(\d{10})(?!\d)"))
            return match.Groups[1].Value;

        return null;
    }

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim().ToUpperInvariant();
        text = ReplaceTurkishChars(text);
        text = Regex.Replace(text, @"[^A-Z0-9\s]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        var legalSuffixes = new[]
        {
            "SAN TIC LTD STI", "SAN TIC AS", "TIC LTD STI", "LTD STI", "LTD STI",
            "LIMITED SIRKETI", "ANONIM SIRKETI", "AS", "STI", "LTD"
        };
        foreach (var suffix in legalSuffixes.OrderByDescending(s => s.Length))
        {
            if (text.EndsWith(" " + suffix, StringComparison.Ordinal))
                text = text[..^(suffix.Length + 1)].Trim();
        }

        return text;
    }

    public static double NameSimilarity(string? a, string? b)
    {
        var na = NormalizeName(a);
        var nb = NormalizeName(b);
        if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb))
            return 0;
        if (string.Equals(na, nb, StringComparison.Ordinal)) return 1;
        if (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal))
            return 0.85;

        var longer = na.Length >= nb.Length ? na : nb;
        var shorter = na.Length >= nb.Length ? nb : na;
        var common = shorter.Count(ch => longer.Contains(ch));
        return (double)common / longer.Length;
    }

    private static string ReplaceTurkishChars(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                'İ' or 'I' or 'ı' or 'i' => 'I',
                'Ş' or 'ş' => 'S',
                'Ğ' or 'ğ' => 'G',
                'Ü' or 'ü' => 'U',
                'Ö' or 'ö' => 'O',
                'Ç' or 'ç' => 'C',
                _ => char.ToUpperInvariant(ch)
            });
        }
        return sb.ToString();
    }
}
