using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Services;

public interface ITaxpayerLookupService
{
    Task<TaxpayerLookupResult?> VerifyTaxNoAsync(
        Guid tenantId,
        Guid branchId,
        string taxNo,
        CancellationToken ct);
}

public sealed record TaxpayerLookupResult(
    string TaxNo,
    string? Title,
    bool IsEInvoiceTaxpayer,
    string Source);

public interface ICounterpartyTaxResolver
{
    Task<CounterpartyResolveResult> ResolveAsync(CounterpartyResolveInput input, CancellationToken ct);
    Task LearnAsync(CounterpartyLearnInput input, CancellationToken ct);
}

public sealed record CounterpartyResolveInput(
    Guid TenantId,
    Guid BranchId,
    string? CounterpartyName,
    string? CounterpartyTaxNo,
    string? CounterpartyIban,
    string? Description,
    bool IsIncomingTransfer,
    bool AllowNihaiTuketici = true);

public sealed record CounterpartyResolveResult
{
    public bool Success { get; init; }
    public string? TaxNo { get; init; }
    public string? DisplayName { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? SupplierId { get; init; }
    public string? Source { get; init; }
    public bool IsNihaiTuketici { get; init; }
    public string? Message { get; init; }

    public static CounterpartyResolveResult Fail(string message) => new()
    {
        Success = false,
        Message = message
    };

    public static CounterpartyResolveResult Ok(
        string taxNo,
        string? displayName,
        Guid? customerId,
        Guid? supplierId,
        string source,
        bool isNihai = false,
        string? message = null) => new()
    {
        Success = true,
        TaxNo = taxNo,
        DisplayName = displayName,
        CustomerId = customerId,
        SupplierId = supplierId,
        Source = source,
        IsNihaiTuketici = isNihai,
        Message = message
    };
}

public sealed record CounterpartyLearnInput(
    Guid TenantId,
    string? CounterpartyName,
    string? CounterpartyIban,
    string TaxNo,
    string Source,
    Guid? CustomerId = null,
    Guid? SupplierId = null);

public sealed class CounterpartyTaxResolverService : ICounterpartyTaxResolver
{
    public const string NihaiTuketiciTckn = "11111111111";
    public const string NihaiTuketiciName = "Nihai Tüketici";

    private readonly AppDbContext _db;
    private readonly ITaxpayerLookupService? _taxpayerLookup;

    public CounterpartyTaxResolverService(AppDbContext db, ITaxpayerLookupService? taxpayerLookup = null)
    {
        _db = db;
        _taxpayerLookup = taxpayerLookup;
    }

    public async Task<CounterpartyResolveResult> ResolveAsync(CounterpartyResolveInput input, CancellationToken ct)
    {
        var name = CoalesceText(input.CounterpartyName, BankMovementParser.ExtractCounterpartyName(input.Description));
        var normalizedName = BankMovementParser.NormalizeName(name);
        var normalizedIban = NormalizeIban(input.CounterpartyIban);

        // 1) Açıklama / Vomsis TCKN-VKN (checksum doğrulamalı)
        var fromDescription = BankMovementParser.ExtractTaxNoFromDescription(input.Description);
        var fromFields = NormalizeTaxNo(CoalesceText(input.CounterpartyTaxNo, fromDescription));
        if (IsAcceptableTaxNo(fromFields))
        {
            var edm = await TryEdmVerifyAsync(input.TenantId, input.BranchId, fromFields, ct);
            var display = edm?.Title ?? name;
            return CounterpartyResolveResult.Ok(
                fromFields, display, null, null,
                edm is not null ? CounterpartyIdentitySources.Edm : CounterpartyIdentitySources.Description,
                message: edm?.Title is not null ? "TCKN/VKN açıklamadan alındı, EDM doğrulandı." : "TCKN/VKN açıklamadan alındı.");
        }

        // 2) IBAN → tedarikçi
        if (!string.IsNullOrWhiteSpace(normalizedIban))
        {
            var supplier = await _db.Suppliers
                .AsNoTracking()
                .Where(x => x.TenantId == input.TenantId && !x.IsDeleted && x.IsActive)
                .FirstOrDefaultAsync(x => x.IBAN != null && NormalizeIban(x.IBAN) == normalizedIban, ct);
            if (supplier is not null)
            {
                var tax = NormalizeTaxNo(supplier.TaxNumber);
                if (IsAcceptableTaxNo(tax))
                    return CounterpartyResolveResult.Ok(tax, supplier.CompanyName, null, supplier.Id, CounterpartyIdentitySources.Supplier);
            }
        }

        // 3) IBAN → global kimlik önbelleği
        if (!string.IsNullOrWhiteSpace(normalizedIban))
        {
            var cached = await FindCacheByIbanAsync(normalizedIban, ct);
            if (cached is not null)
                return CounterpartyResolveResult.Ok(
                    cached.TaxNo, cached.DisplayName ?? name,
                    cached.LinkedCustomerId, cached.LinkedSupplierId,
                    CounterpartyIdentitySources.IdentityCache);
        }

        // 4) IBAN → daha önce eşleşmiş banka hareketi
        if (!string.IsNullOrWhiteSpace(normalizedIban))
        {
            var prev = await _db.BankImportTransactions
                .AsNoTracking()
                .Where(x =>
                    x.TenantId == input.TenantId &&
                    x.BranchId == input.BranchId &&
                    x.CounterpartyIban == normalizedIban &&
                    x.MatchedCustomerId != null &&
                    !x.IsDeleted)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.MatchedCustomerId, x.CounterpartyTaxNo })
                .FirstOrDefaultAsync(ct);
            if (prev?.MatchedCustomerId is Guid cid)
            {
                var customer = await _db.Customers.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == cid && !x.IsDeleted, ct);
                var tax = NormalizeTaxNo(CoalesceText(prev.CounterpartyTaxNo, customer?.NationalId));
                if (IsAcceptableTaxNo(tax))
                    return CounterpartyResolveResult.Ok(tax, customer?.FullName ?? name, cid, null, CounterpartyIdentitySources.BankImport);
            }
        }

        // 5) İsim → müşteri (cari)
        var customerMatch = await ResolveCustomerByNameAsync(input.TenantId, input.BranchId, name, ct);
        if (customerMatch is not null)
        {
            var tax = NormalizeTaxNo(customerMatch.NationalId);
            if (IsAcceptableTaxNo(tax))
                return CounterpartyResolveResult.Ok(tax, customerMatch.FullName, customerMatch.Id, null, CounterpartyIdentitySources.Customer);
        }

        // 6) İsim → tedarikçi
        var supplierMatch = await ResolveSupplierByNameAsync(input.TenantId, name, ct);
        if (supplierMatch is not null)
        {
            var tax = NormalizeTaxNo(supplierMatch.TaxNumber);
            if (IsAcceptableTaxNo(tax))
                return CounterpartyResolveResult.Ok(tax, supplierMatch.CompanyName, null, supplierMatch.Id, CounterpartyIdentitySources.Supplier);
        }

        // 7) Global önbellek → isim
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            var cachedByName = await _db.CounterpartyIdentityCaches
                .AsNoTracking()
                .Where(x => x.NormalizedName == normalizedName && !x.IsDeleted)
                .OrderByDescending(x => x.LastSeenAtUtc)
                .FirstOrDefaultAsync(ct);
            if (cachedByName is not null && IsAcceptableTaxNo(cachedByName.TaxNo))
                return CounterpartyResolveResult.Ok(
                    cachedByName.TaxNo, cachedByName.DisplayName ?? name,
                    cachedByName.LinkedCustomerId, cachedByName.LinkedSupplierId,
                    CounterpartyIdentitySources.IdentityCache);
        }

        // 8) Geçerli TCKN/VKN varsa EDM doğrulama (tax biliniyor ama cari yok)
        if (IsAcceptableTaxNo(fromFields))
        {
            var edm = await TryEdmVerifyAsync(input.TenantId, input.BranchId, fromFields, ct);
            return CounterpartyResolveResult.Ok(
                fromFields, edm?.Title ?? name, null, null,
                edm is not null ? CounterpartyIdentitySources.Edm : CounterpartyIdentitySources.Description);
        }

        // 9) Nihai tüketici (yalnızca gelen, bireysel görünen)
        if (input.AllowNihaiTuketici && input.IsIncomingTransfer && !IsLikelyCorporate(name))
        {
            return CounterpartyResolveResult.Ok(
                NihaiTuketiciTckn,
                string.IsNullOrWhiteSpace(name) ? NihaiTuketiciName : name,
                customerMatch?.Id,
                null,
                CounterpartyIdentitySources.NihaiTuketici,
                isNihai: true,
                message: "TCKN/VKN bulunamadı; e-Arşiv nihai tüketici (11111111111) uygulandı.");
        }

        return CounterpartyResolveResult.Fail(
            string.IsNullOrWhiteSpace(name)
                ? "Karşı taraf adı ve TCKN/VKN bulunamadı."
                : $"'{name}' için TCKN/VKN bulunamadı. Cari kaydı güncelleyin veya manuel eşleştirin.");
    }

    public async Task LearnAsync(CounterpartyLearnInput input, CancellationToken ct)
    {
        if (!IsAcceptableTaxNo(input.TaxNo)) return;

        var normalizedIban = NormalizeIban(input.CounterpartyIban);
        var normalizedName = BankMovementParser.NormalizeName(input.CounterpartyName);
        if (string.IsNullOrWhiteSpace(normalizedIban) && string.IsNullOrWhiteSpace(normalizedName))
            return;

        CounterpartyIdentityCache? existing = null;
        if (!string.IsNullOrWhiteSpace(normalizedIban))
            existing = await _db.CounterpartyIdentityCaches
                .FirstOrDefaultAsync(x => x.NormalizedIban == normalizedIban && !x.IsDeleted, ct);

        existing ??= string.IsNullOrWhiteSpace(normalizedName)
            ? null
            : await _db.CounterpartyIdentityCaches
                .FirstOrDefaultAsync(x => x.NormalizedName == normalizedName && x.TaxNo == input.TaxNo && !x.IsDeleted, ct);

        if (existing is null)
        {
            _db.CounterpartyIdentityCaches.Add(new CounterpartyIdentityCache
            {
                NormalizedIban = normalizedIban,
                NormalizedName = normalizedName ?? "",
                TaxNo = input.TaxNo,
                DisplayName = input.CounterpartyName?.Trim(),
                Source = input.Source,
                LinkedCustomerId = input.CustomerId,
                LinkedSupplierId = input.SupplierId,
                LearnedByTenantId = input.TenantId,
                LastSeenAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            existing.TaxNo = input.TaxNo;
            existing.DisplayName = input.CounterpartyName?.Trim() ?? existing.DisplayName;
            existing.Source = input.Source;
            existing.LinkedCustomerId = input.CustomerId ?? existing.LinkedCustomerId;
            existing.LinkedSupplierId = input.SupplierId ?? existing.LinkedSupplierId;
            existing.LastSeenAtUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(existing.NormalizedIban) && !string.IsNullOrWhiteSpace(normalizedIban))
                existing.NormalizedIban = normalizedIban;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<CounterpartyIdentityCache?> FindCacheByIbanAsync(string normalizedIban, CancellationToken ct)
        => await _db.CounterpartyIdentityCaches
            .AsNoTracking()
            .Where(x => x.NormalizedIban == normalizedIban && !x.IsDeleted)
            .OrderByDescending(x => x.LastSeenAtUtc)
            .FirstOrDefaultAsync(ct);

    private async Task<TaxpayerLookupResult?> TryEdmVerifyAsync(
        Guid tenantId, Guid branchId, string taxNo, CancellationToken ct)
    {
        if (_taxpayerLookup is null) return null;
        try
        {
            return await _taxpayerLookup.VerifyTaxNoAsync(tenantId, branchId, taxNo, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<Customer?> ResolveCustomerByNameAsync(
        Guid tenantId, Guid branchId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var customers = await _db.Customers
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted && x.CariTip == 0)
            .ToListAsync(ct);

        var normalizedTarget = BankMovementParser.NormalizeName(name);
        var exact = customers.FirstOrDefault(c =>
            string.Equals(BankMovementParser.NormalizeName(c.FullName), normalizedTarget, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var contains = customers
            .Where(c =>
            {
                var n = BankMovementParser.NormalizeName(c.FullName);
                return n.Contains(normalizedTarget, StringComparison.Ordinal) ||
                       normalizedTarget.Contains(n, StringComparison.Ordinal);
            })
            .ToList();
        return contains.Count == 1 ? contains[0] : null;
    }

    private async Task<Supplier?> ResolveSupplierByNameAsync(Guid tenantId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var suppliers = await _db.Suppliers
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && !x.IsDeleted && x.IsActive)
            .ToListAsync(ct);

        var normalizedTarget = BankMovementParser.NormalizeName(name);
        var exact = suppliers.FirstOrDefault(s =>
            string.Equals(BankMovementParser.NormalizeName(s.CompanyName), normalizedTarget, StringComparison.Ordinal));
        if (exact is not null) return exact;

        var contains = suppliers
            .Where(s =>
            {
                var n = BankMovementParser.NormalizeName(s.CompanyName);
                return n.Contains(normalizedTarget, StringComparison.Ordinal) ||
                       normalizedTarget.Contains(n, StringComparison.Ordinal);
            })
            .ToList();
        return contains.Count == 1 ? contains[0] : null;
    }

    public static bool IsAcceptableTaxNo(string? taxNo)
    {
        if (string.IsNullOrWhiteSpace(taxNo)) return false;
        if (string.Equals(taxNo, NihaiTuketiciTckn, StringComparison.Ordinal)) return true;
        return TurkishTaxIdValidator.IsValid(taxNo);
    }

    public static bool IsLikelyCorporate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var upper = name.ToUpperInvariant();
        string[] markers =
        [
            " LTD", " LİMİTED", " A.S", " A.Ş", " AS ", " STI", " ŞTI", " SAN.", " TIC.", " TİC.",
            " KOOP", " DERNEK", " VAKIF", " VAKFI", " HOLDING", " GRUP", " GMBH", " INC"
        ];
        return markers.Any(m => upper.Contains(m, StringComparison.Ordinal));
    }

    private static string? CoalesceText(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    private static string NormalizeTaxNo(string? value)
        => TurkishTaxIdValidator.NormalizeDigits(value);

    private static string? NormalizeIban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }
}

public sealed class EdmTaxpayerLookupService : ITaxpayerLookupService
{
    private readonly AppDbContext _db;
    private readonly IEInvoiceProviderResolver _providerResolver;

    public EdmTaxpayerLookupService(AppDbContext db, IEInvoiceProviderResolver providerResolver)
    {
        _db = db;
        _providerResolver = providerResolver;
    }

    public async Task<TaxpayerLookupResult?> VerifyTaxNoAsync(
        Guid tenantId, Guid branchId, string taxNo, CancellationToken ct)
    {
        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.BranchId == branchId && x.IsActive, ct);
        if (profile is null) return null;

        var adapter = _providerResolver.Resolve(string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode);
        if (adapter is not kuyumcu_infrastructure.Services.EdmSoapEInvoiceProviderAdapter edm)
            return null;

        var result = await edm.QueryTaxpayerAsync(profile.IntegratorUsername, profile.IntegratorSecretRef, taxNo, ct);
        if (!result.IsSuccess) return null;

        return new TaxpayerLookupResult(
            taxNo,
            result.Title,
            result.IsEInvoiceTaxpayer == true,
            CounterpartyIdentitySources.Edm);
    }
}
