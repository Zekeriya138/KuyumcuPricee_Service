using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Persistence;
using KUYUMCU.Price_Service.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SuppliersController : ControllerBase
{
    private readonly AppDbContext _db;

    public SuppliersController(AppDbContext db) => _db = db;

    private Guid GetTenantId()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("tenant_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Tenant-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("TenantId missing (JWT veya X-Tenant-Id).");
    }

    private Guid GetBranchId()
    {
        var claim = User?.Claims?.FirstOrDefault(c =>
            c.Type.Equals("branch_id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out var fromJwt))
            return fromJwt;
        if (Request.Headers.TryGetValue("X-Branch-Id", out var hdr) &&
            Guid.TryParse(hdr.ToString(), out var fromHdr))
            return fromHdr;
        throw new InvalidOperationException("BranchId missing (JWT veya X-Branch-Id).");
    }

    public record SupplierListDto(
        Guid Id,
        string SupplierCode,
        string CompanyName,
        int SupplierType,
        string? ContactName,
        string? Phone,
        string? City,
        decimal Balance,
        decimal BalanceTL,
        decimal BalanceUSD,
        decimal BalanceEUR,
        decimal BalanceGBP,
        decimal BalanceHAS,
        decimal BalanceGUMUS,
        DateTime? LastPurchaseDate,
        bool IsActive
    );

    public record SupplierDto(
        Guid Id,
        Guid TenantId,
        Guid? BranchId,
        string SupplierCode,
        string CompanyName,
        string? ContactName,
        int SupplierType,
        string? Phone,
        string? Whatsapp,
        string? Email,
        string? City,
        string? District,
        string? Address,
        string? TaxOffice,
        string? TaxNumber,
        string? Notes,
        decimal CurrentDebt,
        decimal CurrentCredit,
        decimal Balance,
        decimal BalanceTL,
        decimal BalanceUSD,
        decimal BalanceEUR,
        decimal BalanceGBP,
        decimal BalanceHAS,
        decimal BalanceGUMUS,
        int DefaultPaymentType,
        string? BankName,
        string? IBAN,
        int PaymentTermDays,
        int CurrencyType,
        decimal RiskLimit,
        string? ProductCategoriesWorkedWith,
        string? KaratTypes,
        int PricingType,
        decimal DefaultLaborCostPerGram,
        decimal FireRate,
        bool WorksOnConsignment,
        bool AllowsManufacturing,
        bool IsActive,
        DateTime CreatedAt,
        DateTime? UpdatedAt
    );

    public sealed record SupplierBalanceRowDto(
        decimal BalanceTL,
        decimal BalanceUSD,
        decimal BalanceEUR,
        decimal BalanceGBP,
        decimal BalanceHAS,
        decimal BalanceGUMUS
    );

    public sealed record SupplierRecentTxRowDto(
        DateTime IslemTarihi,
        string Grup,
        string Kalem,
        string Deger,
        string CariDurum,
        string Aciklama,
        string Kullanici
    );

    public sealed record SupplierZiynetRowDto(
        string Ad,
        string Tip,
        decimal Adet
    );

    public sealed record SupplierFinanceDto(
        SupplierBalanceRowDto Doviz,
        List<SupplierRecentTxRowDto> SonIslemler,
        List<SupplierZiynetRowDto> Ziynet
    );

    public record CreateSupplierDto(
        Guid? BranchId,
        string SupplierCode,
        string CompanyName,
        string? ContactName,
        int SupplierType,
        string? Phone,
        string? Whatsapp,
        string? Email,
        string? City,
        string? District,
        string? Address,
        string? TaxOffice,
        string? TaxNumber,
        string? Notes,
        decimal CurrentDebt,
        decimal CurrentCredit,
        decimal Balance,
        decimal BalanceTL,
        decimal BalanceUSD,
        decimal BalanceEUR,
        decimal BalanceGBP,
        decimal BalanceHAS,
        decimal BalanceGUMUS,
        int DefaultPaymentType,
        string? BankName,
        string? IBAN,
        int PaymentTermDays,
        int CurrencyType,
        decimal RiskLimit,
        string? ProductCategoriesWorkedWith,
        string? KaratTypes,
        int PricingType,
        decimal DefaultLaborCostPerGram,
        decimal FireRate,
        bool WorksOnConsignment,
        bool AllowsManufacturing,
        bool IsActive
    );

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] int? supplierType,
        [FromQuery] string? city,
        [FromQuery] bool? isActive,
        [FromQuery] decimal? balanceMin,
        [FromQuery] decimal? balanceMax,
        CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var query = _db.Suppliers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim().ToLower();
            query = query.Where(x =>
                (x.SupplierCode != null && x.SupplierCode.ToLower().Contains(t)) ||
                (x.CompanyName != null && x.CompanyName.ToLower().Contains(t)) ||
                (x.Name != null && x.Name.ToLower().Contains(t)) ||
                (x.ContactName != null && x.ContactName.ToLower().Contains(t)) ||
                (x.Phone != null && x.Phone.Contains(t)) ||
                (x.Email != null && x.Email.ToLower().Contains(t)));
        }

        if (supplierType.HasValue)
            query = query.Where(x => (int)x.SupplierType == supplierType.Value);

        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(x => x.City != null && x.City.ToLower().Contains(city.Trim().ToLower()));

        if (isActive.HasValue)
            query = query.Where(x => x.IsActive == isActive.Value);

        if (balanceMin.HasValue)
            query = query.Where(x => x.Balance >= balanceMin.Value);
        if (balanceMax.HasValue)
            query = query.Where(x => x.Balance <= balanceMax.Value);

        var list = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(500)
            .Select(x => new SupplierListDto(
                x.Id,
                x.SupplierCode ?? "",
                x.CompanyName ?? x.Name ?? "",
                (int)x.SupplierType,
                x.ContactName,
                x.Phone,
                x.City,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceTL).FirstOrDefault() ?? x.Balance,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceTL).FirstOrDefault() ?? x.Balance,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceUSD).FirstOrDefault() ?? 0m,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceEUR).FirstOrDefault() ?? 0m,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceGBP).FirstOrDefault() ?? 0m,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceHAS).FirstOrDefault() ?? 0m,
                _db.SupplierBalances.Where(sb => sb.SupplierId == x.Id && sb.TenantId == tenantId && !sb.IsDeleted).Select(sb => (decimal?)sb.BalanceGUMUS).FirstOrDefault() ?? 0m,
                _db.Purchases.Where(p => p.SupplierId == x.Id && p.TenantId == tenantId && p.BranchId == branchId).Max(p => (DateTime?)p.Date),
                x.IsActive
            ))
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var s = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (s is null) return NotFound();
        var sb = await _db.SupplierBalances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SupplierId == s.Id && x.TenantId == tenantId && !x.IsDeleted, ct);
        var dto = ToDto(s, sb);
        return Ok(dto);
    }

    [HttpGet("{id:guid}/finance")]
    public async Task<IActionResult> Finance(Guid id, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();

        var supplier = await _db.Suppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (supplier is null) return NotFound();

        var bal = await _db.SupplierBalances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SupplierId == id && x.TenantId == tenantId && !x.IsDeleted, ct);

        var doviz = new SupplierBalanceRowDto(
            bal?.BalanceTL ?? 0m,
            bal?.BalanceUSD ?? 0m,
            bal?.BalanceEUR ?? 0m,
            bal?.BalanceGBP ?? 0m,
            bal?.BalanceHAS ?? 0m,
            bal?.BalanceGUMUS ?? 0m
        );

        var tx = await _db.SupplierTransactions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SupplierId == id && x.BranchId == branchId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(300)
            .ToListAsync(ct);

        var sonIslemler = tx
            .Select(x =>
            {
                var grup = string.IsNullOrWhiteSpace(x.TxType) ? "FINANS" : x.TxType.Trim().ToUpperInvariant();
                var kalem = $"{(string.IsNullOrWhiteSpace(x.SourceUnit) ? "TL" : x.SourceUnit)} → {(string.IsNullOrWhiteSpace(x.TargetUnit) ? "TL" : x.TargetUnit)}";
                return new SupplierRecentTxRowDto(
                    x.TxDate,
                    grup,
                    kalem,
                    FormatSupplierRecentValue(x),
                    ResolveSupplierCariDurum(x),
                    x.Description ?? "",
                    x.KullaniciAdi ?? ""
                );
            })
            .ToList();

        var existingPurchaseRefs = tx
            .Where(x => !string.IsNullOrWhiteSpace(x.Description))
            .Select(x => x.Description!)
            .Where(desc => desc.Contains("PURCHASE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var purchaseDocs = await _db.Purchases.AsNoTracking()
            .Where(x =>
                x.TenantId == tenantId &&
                x.BranchId == branchId &&
                x.SupplierId == id &&
                !x.IsDeleted)
            .Select(x => new { x.Id, x.Date, x.PaymentMethod, x.GrandTotal, x.UserId })
            .OrderByDescending(x => x.Date)
            .Take(300)
            .ToListAsync(ct);

        var userNames = await UserDisplayNames.BuildUserNameMapAsync(
            _db, tenantId, purchaseDocs.Select(x => x.UserId).ToList(), ct);

        foreach (var purchase in purchaseDocs)
        {
            var marker = purchase.Id.ToString();
            var exists = existingPurchaseRefs.Any(desc => desc.Contains(marker, StringComparison.OrdinalIgnoreCase));
            if (exists) continue;

            sonIslemler.Add(new SupplierRecentTxRowDto(
                purchase.Date,
                "AUDIT",
                "Alış İşlemi",
                $"{Math.Abs(purchase.GrandTotal):N2} TL",
                "İşlem",
                $"Alış belgesi (PURCHASE {purchase.Id}, Ödeme: {purchase.PaymentMethod})",
                userNames.TryGetValue(purchase.UserId, out var pn) ? pn : ""));
        }

        sonIslemler = sonIslemler
            .OrderByDescending(x => x.IslemTarihi)
            .Take(300)
            .ToList();

        var ziynet = tx
            .Select(TryParseZiynetMove)
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => $"{(x.Ad ?? "").Trim().ToUpperInvariant()}|{(x.Tip ?? "").Trim().ToUpperInvariant()}")
            .Select(g =>
            {
                var first = g.First();
                return new SupplierZiynetRowDto(
                    first.Ad,
                    string.IsNullOrWhiteSpace(first.Tip) ? "Yeni" : first.Tip.Trim(),
                    decimal.Round(g.Sum(v => v.Adet), 3, MidpointRounding.AwayFromZero));
            })
            .Where(x => x.Adet != 0m)
            .OrderBy(x => x.Ad)
            .ThenBy(x => x.Tip)
            .ToList();

        return Ok(new SupplierFinanceDto(doviz, sonIslemler, ziynet));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSupplierDto dto, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var companyName = (dto.CompanyName ?? "").Trim();
        if (string.IsNullOrEmpty(companyName))
            return BadRequest(new { error = "Firma / Ünvan zorunludur." });
        if (dto.BranchId.HasValue && dto.BranchId.Value != Guid.Empty && dto.BranchId.Value != branchId)
            return BadRequest(new { error = "Tedarikçi sadece seçili şubede oluşturulabilir." });

        var code = (dto.SupplierCode ?? "").Trim();
        if (string.IsNullOrEmpty(code))
            code = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

        var entity = new Supplier
        {
            TenantId = tenantId,
            BranchId = branchId,
            SupplierCode = code,
            CompanyName = companyName,
            Name = companyName,
            ContactName = dto.ContactName?.Trim(),
            SupplierType = (SupplierType)dto.SupplierType,
            Phone = dto.Phone?.Trim(),
            Whatsapp = dto.Whatsapp?.Trim(),
            Email = dto.Email?.Trim(),
            City = dto.City?.Trim(),
            District = dto.District?.Trim(),
            Address = dto.Address?.Trim(),
            TaxOffice = dto.TaxOffice?.Trim(),
            TaxNumber = dto.TaxNumber?.Trim(),
            Notes = dto.Notes?.Trim(),
            CurrentDebt = dto.CurrentDebt,
            CurrentCredit = dto.CurrentCredit,
            Balance = dto.BalanceTL,
            DefaultPaymentType = (SupplierPaymentType)dto.DefaultPaymentType,
            BankName = dto.BankName?.Trim(),
            IBAN = dto.IBAN?.Trim(),
            PaymentTermDays = dto.PaymentTermDays,
            CurrencyType = (SupplierCurrencyType)dto.CurrencyType,
            RiskLimit = dto.RiskLimit,
            ProductCategoriesWorkedWith = dto.ProductCategoriesWorkedWith?.Trim(),
            KaratTypes = dto.KaratTypes?.Trim(),
            PricingType = (SupplierPricingType)dto.PricingType,
            DefaultLaborCostPerGram = dto.DefaultLaborCostPerGram,
            FireRate = dto.FireRate,
            WorksOnConsignment = dto.WorksOnConsignment,
            AllowsManufacturing = dto.AllowsManufacturing,
            IsActive = dto.IsActive
        };

        _db.Suppliers.Add(entity);
        await _db.SaveChangesAsync(ct);
        var sb = new SupplierBalance
        {
            TenantId = tenantId,
            SupplierId = entity.Id,
            BalanceTL = dto.BalanceTL,
            BalanceUSD = dto.BalanceUSD,
            BalanceEUR = dto.BalanceEUR,
            BalanceGBP = dto.BalanceGBP,
            BalanceHAS = dto.BalanceHAS,
            BalanceGUMUS = dto.BalanceGUMUS,
            UpdatedAt = DateTime.UtcNow
        };
        _db.SupplierBalances.Add(sb);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, ToDto(entity, sb));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateSupplierDto dto, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var s = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (s is null) return NotFound();

        var companyName = (dto.CompanyName ?? "").Trim();
        if (string.IsNullOrEmpty(companyName))
            return BadRequest(new { error = "Firma / Ünvan zorunludur." });

        if (dto.BranchId.HasValue && dto.BranchId.Value != Guid.Empty && dto.BranchId.Value != branchId)
            return BadRequest(new { error = "Tedarikçi farklı bir şubeye taşınamaz." });
        s.BranchId = branchId;
        if (!string.IsNullOrWhiteSpace(dto.SupplierCode)) s.SupplierCode = dto.SupplierCode.Trim();
        s.CompanyName = companyName;
        s.Name = companyName;
        s.ContactName = dto.ContactName?.Trim();
        s.SupplierType = (SupplierType)dto.SupplierType;
        s.Phone = dto.Phone?.Trim();
        s.Whatsapp = dto.Whatsapp?.Trim();
        s.Email = dto.Email?.Trim();
        s.City = dto.City?.Trim();
        s.District = dto.District?.Trim();
        s.Address = dto.Address?.Trim();
        s.TaxOffice = dto.TaxOffice?.Trim();
        s.TaxNumber = dto.TaxNumber?.Trim();
        s.Notes = dto.Notes?.Trim();
        s.CurrentDebt = dto.CurrentDebt;
        s.CurrentCredit = dto.CurrentCredit;
        s.Balance = dto.BalanceTL;
        s.DefaultPaymentType = (SupplierPaymentType)dto.DefaultPaymentType;
        s.BankName = dto.BankName?.Trim();
        s.IBAN = dto.IBAN?.Trim();
        s.PaymentTermDays = dto.PaymentTermDays;
        s.CurrencyType = (SupplierCurrencyType)dto.CurrencyType;
        s.RiskLimit = dto.RiskLimit;
        s.ProductCategoriesWorkedWith = dto.ProductCategoriesWorkedWith?.Trim();
        s.KaratTypes = dto.KaratTypes?.Trim();
        s.PricingType = (SupplierPricingType)dto.PricingType;
        s.DefaultLaborCostPerGram = dto.DefaultLaborCostPerGram;
        s.FireRate = dto.FireRate;
        s.WorksOnConsignment = dto.WorksOnConsignment;
        s.AllowsManufacturing = dto.AllowsManufacturing;
        s.IsActive = dto.IsActive;
        s.UpdatedAt = DateTime.UtcNow;

        var sb = await _db.SupplierBalances.FirstOrDefaultAsync(x => x.SupplierId == s.Id && x.TenantId == tenantId && !x.IsDeleted, ct);
        if (sb is null)
        {
            sb = new SupplierBalance
            {
                TenantId = tenantId,
                SupplierId = s.Id
            };
            _db.SupplierBalances.Add(sb);
        }
        sb.BalanceTL = dto.BalanceTL;
        sb.BalanceUSD = dto.BalanceUSD;
        sb.BalanceEUR = dto.BalanceEUR;
        sb.BalanceGBP = dto.BalanceGBP;
        sb.BalanceHAS = dto.BalanceHAS;
        sb.BalanceGUMUS = dto.BalanceGUMUS;
        sb.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(s, sb));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        var s = await _db.Suppliers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.BranchId == branchId && !x.IsDeleted, ct);
        if (s is null) return NotFound();
        s.IsDeleted = true;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static SupplierDto ToDto(Supplier s, SupplierBalance? sb) => new(
        s.Id,
        s.TenantId,
        s.BranchId,
        s.SupplierCode ?? "",
        s.CompanyName ?? s.Name ?? "",
        s.ContactName,
        (int)s.SupplierType,
        s.Phone,
        s.Whatsapp,
        s.Email,
        s.City,
        s.District,
        s.Address,
        s.TaxOffice,
        s.TaxNumber,
        s.Notes,
        s.CurrentDebt,
        s.CurrentCredit,
        sb?.BalanceTL ?? s.Balance,
        sb?.BalanceTL ?? s.Balance,
        sb?.BalanceUSD ?? 0m,
        sb?.BalanceEUR ?? 0m,
        sb?.BalanceGBP ?? 0m,
        sb?.BalanceHAS ?? 0m,
        sb?.BalanceGUMUS ?? 0m,
        (int)s.DefaultPaymentType,
        s.BankName,
        s.IBAN,
        s.PaymentTermDays,
        (int)s.CurrencyType,
        s.RiskLimit,
        s.ProductCategoriesWorkedWith,
        s.KaratTypes,
        (int)s.PricingType,
        s.DefaultLaborCostPerGram,
        s.FireRate,
        s.WorksOnConsignment,
        s.AllowsManufacturing,
        s.IsActive,
        s.CreatedAt,
        s.UpdatedAt
    );

    private static string FormatSupplierRecentValue(SupplierTransaction x)
    {
        var srcU = string.IsNullOrWhiteSpace(x.SourceUnit) ? "TL" : x.SourceUnit.Trim().ToUpperInvariant();
        var tgtU = string.IsNullOrWhiteSpace(x.TargetUnit) ? "TL" : x.TargetUnit.Trim().ToUpperInvariant();
        var txType = (x.TxType ?? "").Trim().ToUpperInvariant();
        if (txType == "OPENING_BALANCE")
        {
            var signOpen = x.TargetAmount >= 0m ? "+" : "-";
            return $"{signOpen}{Math.Abs(x.TargetAmount):N4} {tgtU}";
        }
        if (txType == "BALANCE_CONVERSION")
            return $"{x.SourceAmount:N4} {srcU} => {x.TargetAmount:N4} {tgtU}";

        if (txType == "ZIYNET")
        {
            var ziynetAmt = x.TargetAmount;
            var ziynetSign = ziynetAmt >= 0m ? "+" : "-";
            return $"{ziynetSign}{Math.Abs(ziynetAmt):N4} {tgtU}";
        }

        var sign = txType == "PAYMENT" ? "-" : "+";
        if (x.IsConverted && !string.Equals(srcU, tgtU, StringComparison.OrdinalIgnoreCase))
        {
            return $"{sign}{x.SourceAmount:N4} {srcU} => {x.TargetAmount:N4} {tgtU}";
        }
        return $"{sign}{x.TargetAmount:N4} {tgtU}";
    }

    private static string ResolveSupplierCariDurum(SupplierTransaction x)
    {
        var txType = (x.TxType ?? "").Trim().ToUpperInvariant();
        var desc = (x.Description ?? "").Trim();

        // Alış/satış veresiye kayıtlarında cari taraf (alacak/borç) gösterilsin.
        var veresiyeKaydi =
            desc.Contains("veresiye", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("cari", StringComparison.OrdinalIgnoreCase);
        if (veresiyeKaydi)
            return txType == "COLLECTION" ? "Alacaklı" : "Borçlu";

        // SupplierIslemWindow manuel akışında eylem metni gösterilsin.
        if (txType == "OPENING_BALANCE") return "Açılış Bakiye Girişi";
        if (txType == "BALANCE_CONVERSION") return "Bakiye Dönüştürme";
        if (txType == "PAYMENT") return "Ödeme";
        if (txType == "COLLECTION") return "Tahsilat";
        if (txType == "ZIYNET") return "Ziynet";
        return "Finans";
    }

    private sealed record SupplierZiynetMove(string Ad, string Tip, decimal Adet);

    private static SupplierZiynetMove? TryParseZiynetMove(SupplierTransaction tx)
    {
        var desc = (tx.Description ?? "").Trim();
        if (string.IsNullOrWhiteSpace(desc)) return null;
        if (!desc.Contains("[ZIYNET]|", StringComparison.OrdinalIgnoreCase)) return null;

        string ad = "";
        string tip = "Yeni";
        decimal adet = 0m;
        var parts = desc.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim();
            if (part.StartsWith("AD=", StringComparison.OrdinalIgnoreCase))
                ad = part.Substring(3).Trim();
            else if (part.StartsWith("TIP=", StringComparison.OrdinalIgnoreCase))
                tip = part.Substring(4).Trim();
            else if (part.StartsWith("ADET=", StringComparison.OrdinalIgnoreCase))
                decimal.TryParse(part.Substring(5).Trim().Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out adet);
        }

        if (adet == 0m)
            adet = tx.TargetAmount;

        if (string.IsNullOrWhiteSpace(ad) || adet == 0m)
            return null;
        return new SupplierZiynetMove(ad, string.IsNullOrWhiteSpace(tip) ? "Yeni" : tip, adet);
    }
}
