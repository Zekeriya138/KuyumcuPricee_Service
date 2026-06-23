using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // JWT zorunlu
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _svc;
    private readonly AppDbContext _db;
    public CustomersController(ICustomerService svc, AppDbContext db)
    {
        _svc = svc;
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] int? cariTip, CancellationToken ct)
        => Ok(await _svc.ListAsync(q, cariTip, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken ct)
        => (await _svc.GetAsync(id, ct)) is { } c ? Ok(c) : NotFound();

    public record CreateCustomerDto(
        string FullName,
        int CariTip,
        string? NationalId,
        DateTime? BirthDate,
        string? Phone,
        string? Email,
        string? City,
        string? District,
        string? Address,
        string? Note,
        string? TedarikciExtJson
    );

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomerDto dto, CancellationToken ct)
    {
        var ent = new Customer
        {
            FullName = dto.FullName,
            CariTip = dto.CariTip,
            NationalId = dto.NationalId,
            BirthDate = dto.BirthDate,
            Phone = dto.Phone,
            Email = dto.Email,
            City = dto.City,
            District = dto.District,
            Address = dto.Address,
            Note = dto.Note,
            TedarikciExtJson = dto.TedarikciExtJson
        };

        var created = await _svc.CreateAsync(ent, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] CreateCustomerDto dto, CancellationToken ct)
    {
        var inp = new Customer
        {
            FullName = dto.FullName,
            CariTip = dto.CariTip,
            NationalId = dto.NationalId,
            BirthDate = dto.BirthDate,
            Phone = dto.Phone,
            Email = dto.Email,
            City = dto.City,
            District = dto.District,
            Address = dto.Address,
            Note = dto.Note,
            TedarikciExtJson = dto.TedarikciExtJson
        };
        return await _svc.UpdateAsync(id, inp, ct) ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid id, CancellationToken ct)
        => await _svc.DeleteAsync(id, ct) ? NoContent() : NotFound();

    public sealed record CustomerBalanceRowDto(
        decimal BalanceTL,
        decimal BalanceUSD,
        decimal BalanceEUR,
        decimal BalanceGBP,
        decimal BalanceHAS,
        decimal BalanceGUMUS
    );

    public sealed record CustomerZiynetRowDto(
        string Ad,
        string Tip,
        decimal Adet
    );

    public sealed record CustomerIscilikliRowDto(
        Guid Id,
        string CariDurum,
        string UrunAdi,
        decimal Gramaj,
        string Ayar,
        decimal HasKarsiligi,
        decimal SatisFiyatiTl,
        DateTime IslemTarihi
    );

    public sealed record CustomerRecentTxRowDto(
        DateTime IslemTarihi,
        string Grup,
        string Kalem,
        string Deger,
        string CariDurum,
        string Aciklama
    );

    public sealed record CustomerFinanceDto(
        CustomerBalanceRowDto Doviz,
        List<CustomerZiynetRowDto> Ziynet,
        List<CustomerIscilikliRowDto> Iscilikli,
        List<CustomerRecentTxRowDto> SonIslemler
    );

    [HttpGet("{id:guid}/finance")]
    public async Task<IActionResult> Finance([FromRoute] Guid id, CancellationToken ct)
    {
        var customer = await _svc.GetAsync(id, ct);
        if (customer is null) return NotFound();

        var bal = await _db.CustomerBalances.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CustomerId == id && x.TenantId == customer.TenantId && !x.IsDeleted, ct);
        var tx = await _db.CustomerTransactions.AsNoTracking()
            .Where(x => x.CustomerId == id && x.TenantId == customer.TenantId && x.BranchId == customer.BranchId && !x.IsDeleted)
            .OrderByDescending(x => x.TxDate)
            .ToListAsync(ct);

        var gumusFromTx = tx
            .Where(x => string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase)
                && string.Equals((x.ItemName ?? "").Trim(), "GUMUS", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);

        var doviz = new CustomerBalanceRowDto(
            bal?.BalanceTL ?? 0m,
            bal?.BalanceUSD ?? 0m,
            bal?.BalanceEUR ?? 0m,
            bal?.BalanceGBP ?? 0m,
            bal?.BalanceHAS ?? 0m,
            decimal.Round(gumusFromTx, 4, MidpointRounding.AwayFromZero)
        );

        var ziynet = tx
            .Where(x => string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => new
            {
                ItemName = NormalizeZiynetItemName(x.ItemName),
                Tip = NormalizeZiynetTipGroupingKey(NormalizeZiynetItemName(x.ItemName), x.ItemType),
                IsEmanet = IsEmanetKaydi(x)
            })
            .Select(g =>
            {
                var adet = g.Sum(x => x.Direction >= 0 ? x.Quantity : -x.Quantity);
                var tip = g.Key.IsEmanet ? $"{g.Key.Tip} (Emanet)" : g.Key.Tip;
                return new CustomerZiynetRowDto(
                    g.Key.ItemName,
                    tip,
                    adet
                );
            })
            .Where(x => x.Adet != 0)
            .OrderBy(x => x.Ad)
            .ThenBy(x => x.Tip)
            .ToList();

        // İşçilikli sekmesinde satırları işlem bazlı göster (tam veresiye satışta yeni borç satırı net görünsün).
        var iscilikli = tx
            .Where(x => x.GroupCode == "ISCILIKLI")
            .OrderByDescending(x => x.TxDate)
            .Select(x => new CustomerIscilikliRowDto(
                x.Id,
                ResolveIscilikliCariDurum(x),
                x.ItemName,
                Math.Abs(x.Gram ?? 0m),
                x.Ayar ?? "",
                Math.Abs(x.HasEquivalent ?? 0m),
                Math.Abs(x.TotalPriceTl ?? 0m),
                x.TxDate
            ))
            .ToList();

        var sonIslemKaynak = FilterDuplicateZiynetEmanetRecentRows(tx);
        var sonIslemler = sonIslemKaynak
            .OrderByDescending(x => x.TxDate)
            .ThenByDescending(x => x.CreatedAt)
            .Take(200)
            .Select(x => new CustomerRecentTxRowDto(
                x.TxDate,
                x.GroupCode ?? "",
                ResolveRecentKalem(x),
                FormatRecentValue(x),
                ResolveRecentCariDurum(x),
                x.Note ?? ""
            ))
            .ToList();

        var existingAuditSaleIds = sonIslemKaynak
            .Where(x =>
                string.Equals((x.GroupCode ?? "").Trim(), "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.ItemName ?? "").Trim(), "SALE_EVENT", StringComparison.OrdinalIgnoreCase) &&
                x.RefId.HasValue)
            .Select(x => x.RefId!.Value)
            .ToHashSet();
        var existingAuditPurchaseIds = sonIslemKaynak
            .Where(x =>
                string.Equals((x.GroupCode ?? "").Trim(), "AUDIT", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.ItemName ?? "").Trim(), "PURCHASE_EVENT", StringComparison.OrdinalIgnoreCase) &&
                x.RefId.HasValue)
            .Select(x => x.RefId!.Value)
            .ToHashSet();

        var salesDocs = await _db.Sales.AsNoTracking()
            .Where(x =>
                x.TenantId == customer.TenantId &&
                x.BranchId == customer.BranchId &&
                x.CustomerId == id &&
                !x.IsDeleted)
            .Select(x => new { x.Id, x.CreatedAt, x.PaymentType })
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var purchaseDocs = await _db.Purchases.AsNoTracking()
            .Where(x =>
                x.TenantId == customer.TenantId &&
                x.BranchId == customer.BranchId &&
                x.CustomerId == id &&
                !x.IsDeleted)
            .Select(x => new { x.Id, x.Date, x.PaymentMethod })
            .OrderByDescending(x => x.Date)
            .Take(200)
            .ToListAsync(ct);

        foreach (var sale in salesDocs)
        {
            if (existingAuditSaleIds.Contains(sale.Id)) continue;
            sonIslemler.Add(new CustomerRecentTxRowDto(
                sale.CreatedAt,
                "AUDIT",
                "Satış İşlemi",
                "İşlem kaydı",
                "İşlem",
                $"Satış belgesi (Ref: {sale.Id}, Ödeme: {sale.PaymentType ?? "-"})"));
        }
        foreach (var purchase in purchaseDocs)
        {
            if (existingAuditPurchaseIds.Contains(purchase.Id)) continue;
            sonIslemler.Add(new CustomerRecentTxRowDto(
                purchase.Date,
                "AUDIT",
                "Alış İşlemi",
                "İşlem kaydı",
                "İşlem",
                $"Alış belgesi (Ref: {purchase.Id}, Ödeme: {purchase.PaymentMethod})"));
        }

        sonIslemler = sonIslemler
            .OrderByDescending(x => x.IslemTarihi)
            .Take(200)
            .ToList();

        return Ok(new CustomerFinanceDto(doviz, ziynet, iscilikli, sonIslemler));
    }

    private static string FormatRecentValue(CustomerTransaction x)
    {
        var sign = x.Direction >= 0 ? "+" : "-";
        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase))
        {
            var tl = Math.Abs(x.TotalPriceTl ?? x.Quantity);
            return tl > 0m ? $"{tl:N2} TL" : "İşlem kaydı";
        }
        if (string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase))
            return $"{sign}{Math.Abs(x.Quantity):N4} {x.ItemName}";

        if (string.Equals(x.GroupCode, "ZIYNET", StringComparison.OrdinalIgnoreCase))
            return $"{sign}{Math.Abs(x.Quantity):N3} adet";

        if (string.Equals(x.GroupCode, "ISCILIKLI", StringComparison.OrdinalIgnoreCase))
        {
            var gram = x.Gram ?? 0m;
            var has = x.HasEquivalent ?? 0m;
            var tl = x.TotalPriceTl ?? 0m;
            return $"{sign}{Math.Abs(gram):N3} gr | {Math.Abs(has):N6} has | {Math.Abs(tl):N2} TL";
        }

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
            return $"{sign}{Math.Abs(x.Quantity):N3} adet";

        return $"{sign}{Math.Abs(x.Quantity):N4}";
    }

    private static string ResolveRecentKalem(CustomerTransaction x)
    {
        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase))
        {
            var item = (x.ItemName ?? "").Trim().ToUpperInvariant();
            if (item == "SALE_EVENT") return "Satış İşlemi";
            if (item == "PURCHASE_EVENT") return "Alış İşlemi";
        }

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
        {
            var raw = (x.ItemType ?? "").Trim();
            if (raw.Contains("|", StringComparison.Ordinal))
            {
                var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                    return $"Ziynet Düşüm: {parts[0]} ({parts[1]})";
                if (parts.Length == 1)
                    return $"Ziynet Düşüm: {parts[0]}";
            }

            var note = x.Note ?? "";
            var marker = "Ziynet düşüm:";
            var idx = note.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var tail = note[(idx + marker.Length)..].Trim();
                var pipeIdx = tail.IndexOf('|');
                var detail = pipeIdx >= 0 ? tail[..pipeIdx].Trim() : tail;
                if (!string.IsNullOrWhiteSpace(detail))
                    return $"Ziynet Düşüm: {detail}";
            }

            return "Ziynet Düşüm";
        }

        return string.IsNullOrWhiteSpace(x.ItemType) ? (x.ItemName ?? "") : $"{x.ItemName} ({x.ItemType})";
    }

    private static string ResolveRecentCariDurum(CustomerTransaction x)
    {
        var refType = (x.RefType ?? "").Trim().ToUpperInvariant();
        var mevcut = (x.CariDurum ?? "").Trim();
        var isEmanet = IsEmanetKaydi(x);

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
            return "İşlem";

        if (string.Equals(x.GroupCode, "AUDIT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((x.ItemName ?? "").Trim(), "ZIYNET_DUSUM", StringComparison.OrdinalIgnoreCase))
            return "Düşüm";

        if (isEmanet)
            return x.Direction >= 0 ? "Emanet (Alacaklı)" : "Emanet (Borçlu)";

        // MusteriIslemWindow (manuel) akışında eylem metni göster.
        if (refType == "OPENING_BALANCE")
            return "Açılış Bakiye Girişi";
        if (refType == "BALANCE_CONVERSION")
            return "Bakiye Dönüştürme";
        if (refType is "MANUAL" or "MANUAL_SETTLE")
            return x.Direction >= 0 ? "Tahsilat" : "Ödeme";

        if (string.Equals(mevcut, "Tahsilat", StringComparison.OrdinalIgnoreCase))
            return "Tahsilat";
        if (string.Equals(mevcut, "Odeme", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mevcut, "Ödeme", StringComparison.OrdinalIgnoreCase))
            return "Ödeme";
        if (string.Equals(mevcut, "Alacakli", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mevcut, "Alacaklı", StringComparison.OrdinalIgnoreCase))
            return "Alacaklı";
        if (string.Equals(mevcut, "Borclu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mevcut, "Borçlu", StringComparison.OrdinalIgnoreCase))
            return "Borçlu";

        // Satış/alış veresiye gibi finans hareketlerinde borç-alacak dili korunur.
        if (string.Equals(x.GroupCode, "DOVIZ", StringComparison.OrdinalIgnoreCase) &&
            (refType == "SALE" || refType == "PURCHASE"))
            return x.Direction >= 0 ? "Alacaklı" : "Borçlu";

        return string.IsNullOrWhiteSpace(mevcut) ? "Finans" : mevcut;
    }

    private static string ResolveIscilikliCariDurum(CustomerTransaction x)
    {
        if (IsEmanetKaydi(x))
            return x.Direction >= 0 ? "Emanet (Alacaklı)" : "Emanet (Borçlu)";

        if (string.IsNullOrWhiteSpace(x.CariDurum))
            return x.Direction >= 0 ? "Alacakli" : "Borclu";

        return x.CariDurum!;
    }

    private static string NormalizeZiynetTipDisplay(string? raw)
    {
        var txt = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt)) return "Yeni";
        if (string.Equals(txt, "yeni", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(txt, "new", StringComparison.OrdinalIgnoreCase))
            return "Yeni";
        if (string.Equals(txt, "eski", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(txt, "old", StringComparison.OrdinalIgnoreCase))
            return "Eski";
        return txt;
    }

    private static string NormalizeZiynetItemName(string? raw)
    {
        var value = (raw ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (value.Contains("KÜLÇE") || value.Contains("KULCE"))
            return "GRAM ALTIN(KÜLÇE)";
        if ((value.Contains("22 AYAR") || value.Contains("22AYAR")) &&
            (value.Contains("GR") || value.Contains("GRAM")))
            return "22 AYAR(GR)";
        if (value == "GRAM" || value.Contains("GRAM ALTIN (HAS)") || value.Contains("GRAM ALTIN(HAS)"))
            return "GRAM ALTIN(KÜLÇE)";
        return value;
    }

    private static string NormalizeZiynetTipGroupingKey(string normalizedItemName, string? rawTip)
    {
        var item = (normalizedItemName ?? "").Trim().ToUpperInvariant();
        if (item == "GRAM ALTIN(KÜLÇE)" || item == "GRAM ALTIN(KULCE)")
            return "Yeni";
        return NormalizeZiynetTipDisplay(rawTip);
    }

    private static bool IsEmanetKaydi(CustomerTransaction x)
    {
        if (string.Equals((x.CariDurum ?? "").Trim(), "Emanet", StringComparison.OrdinalIgnoreCase))
            return true;
        var note = (x.Note ?? "").Trim();
        return note.Contains("emanet", StringComparison.OrdinalIgnoreCase);
    }

    private static List<CustomerTransaction> FilterDuplicateZiynetEmanetRecentRows(List<CustomerTransaction> rows)
    {
        if (rows.Count == 0) return rows;

        static bool IsZiynetEmanet(CustomerTransaction x) =>
            string.Equals((x.GroupCode ?? "").Trim(), "ZIYNET", StringComparison.OrdinalIgnoreCase)
            && string.Equals((x.RefType ?? "").Trim(), "SALE", StringComparison.OrdinalIgnoreCase)
            && IsEmanetKaydi(x);

        static string Nz(string? value, string fallback = "")
            => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToUpperInvariant();

        var processRows = rows
            .Where(x =>
                IsZiynetEmanet(x)
                && !x.RefId.HasValue
                && (x.Note ?? "").Contains("Ziynet satış emanet kaydı", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (processRows.Count == 0) return rows;

        var filtered = new List<CustomerTransaction>(rows.Count);
        foreach (var row in rows)
        {
            if (!IsZiynetEmanet(row) || !row.RefId.HasValue)
            {
                filtered.Add(row);
                continue;
            }

            var hasProcessTwin = processRows.Any(p =>
                Nz(p.ItemName) == Nz(row.ItemName)
                && Nz(p.ItemType, "YENI") == Nz(row.ItemType, "YENI")
                && p.Direction == row.Direction
                && Math.Abs(Math.Abs(p.Quantity) - Math.Abs(row.Quantity)) <= 0.0001m
                && Math.Abs((p.TxDate - row.TxDate).TotalMinutes) <= 10d);

            if (!hasProcessTwin)
                filtered.Add(row);
        }

        return filtered;
    }
}
