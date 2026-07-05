using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Services;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/expense-slips")]
[Authorize]
public sealed class ExpenseSlipController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEInvoiceProviderResolver _providerResolver;

    public ExpenseSlipController(AppDbContext db, ITenantContext tenant, IEInvoiceProviderResolver providerResolver)
    {
        _db = db;
        _tenant = tenant;
        _providerResolver = providerResolver;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] string? status,
        [FromQuery, Range(1, 1000)] int page = 1,
        [FromQuery, Range(1, 500)] int pageSize = 50,
        CancellationToken ct = default)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var q = _db.ExpenseSlipDocuments.AsNoTracking().Where(x => x.TenantId == tid);
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status);

        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.BranchId,
                BranchName = x.Branch != null ? (x.Branch.Name ?? "") : "",
                x.SourceSaleId,
                x.DocumentNo,
                x.Status,
                x.Currency,
                x.GrandTotal,
                x.BuyerName,
                x.BuyerTaxNumber,
                x.Description,
                x.PayloadJson,
                x.IntegratorDocumentId,
                x.Uuid,
                x.RawLastResponse,
                x.LastError,
                x.RetryCount,
                x.SubmittedAt,
                x.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new { total, page, pageSize, items = rows });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var row = await _db.ExpenseSlipDocuments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound();
        return Ok(row);
    }

    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> GetAudit(Guid id, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var exists = await _db.ExpenseSlipDocuments.AsNoTracking().AnyAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (!exists) return NotFound(new { error = "Belge bulunamadı." });

        var logs = await _db.ExpenseSlipAuditLogs.AsNoTracking()
            .Where(x => x.TenantId == tid && x.DocumentId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.Action,
                x.StatusBefore,
                x.StatusAfter,
                x.IsSuccess,
                x.RequestJson,
                x.ResponseRaw,
                x.ErrorMessage,
                x.CreatedAt
            })
            .ToListAsync(ct);
        return Ok(logs);
    }

    [HttpPost("draft")]
    public async Task<IActionResult> CreateDraft([FromBody] CreateExpenseSlipDraftReq req, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        if (req.BranchId == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });
        if (string.IsNullOrWhiteSpace(req.BuyerName))
            return BadRequest(new { error = "Alıcı adı zorunludur." });
        var taxNo = NormalizeDigits(req.BuyerTaxNumber);
        if (taxNo.Length is not (10 or 11))
            return BadRequest(new { error = "Alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (req.GrandTotal <= 0m)
            return BadRequest(new { error = "Toplam tutar 0'dan büyük olmalıdır." });
        if (string.IsNullOrWhiteSpace(req.Workmanship))
            return BadRequest(new { error = "İşin mahiyeti zorunludur." });
        if (string.IsNullOrWhiteSpace(req.ProductType))
            return BadRequest(new { error = "İşin cinsi zorunludur." });
        if (req.QuantityGram is null || req.QuantityGram <= 0m)
            return BadRequest(new { error = "Adet/Gram 0'dan büyük olmalıdır." });
        if (req.UnitPrice is null || req.UnitPrice <= 0m)
            return BadRequest(new { error = "Birim fiyat 0'dan büyük olmalıdır." });

        var branch = await _db.Branches.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.Id == req.BranchId, ct);
        if (branch is null)
            return BadRequest(new { error = "Geçersiz şube." });

        var docNo = await BuildDocumentNoAsync(_tenant.TenantId, req.BranchId, ct);
        var payload = req.PayloadJson;
        if (string.IsNullOrWhiteSpace(payload))
        {
            payload = JsonSerializer.Serialize(new
            {
                req.BranchId,
                req.SourceSaleId,
                buyerName = req.BuyerName.Trim(),
                buyerTaxNumber = taxNo,
                grandTotal = req.GrandTotal,
                workmanship = req.Workmanship?.Trim(),
                productType = req.ProductType?.Trim(),
                quantityGram = req.QuantityGram,
                unitPrice = req.UnitPrice,
                lineTotal = req.LineTotal ?? req.GrandTotal,
                currency = string.IsNullOrWhiteSpace(req.Currency) ? "TRY" : req.Currency.Trim().ToUpperInvariant(),
                description = req.Description?.Trim()
            });
        }

        var row = new ExpenseSlipDocument
        {
            TenantId = _tenant.TenantId,
            BranchId = req.BranchId,
            SourceSaleId = req.SourceSaleId,
            DocumentNo = docNo,
            Status = "Draft",
            Currency = string.IsNullOrWhiteSpace(req.Currency) ? "TRY" : req.Currency.Trim().ToUpperInvariant(),
            GrandTotal = Math.Round(req.GrandTotal, 2, MidpointRounding.AwayFromZero),
            BuyerName = req.BuyerName.Trim(),
            BuyerTaxNumber = taxNo,
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            PayloadJson = payload
        };
        _db.ExpenseSlipDocuments.Add(row);
        _db.ExpenseSlipAuditLogs.Add(BuildAuditLog(
            row.TenantId,
            row.BranchId,
            row.Id,
            "CreateDraft",
            null,
            row.Status,
            true,
            payload,
            null,
            null));
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.DocumentNo, row.Status, message = "Gider pusulası taslağı oluşturuldu." });
    }

    [HttpPost("{id:guid}/queue")]
    public async Task<IActionResult> Queue(Guid id, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var row = await _db.ExpenseSlipDocuments.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound(new { error = "Belge bulunamadı." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && row.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Belge şubesi ile seçili şube farklı." });

        if (string.Equals(row.Status, "Queued", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Status, "Sent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { row.Id, row.DocumentNo, row.Status, message = "Belge zaten kuyrukta/gönderilmiş." });
        }

        var profile = await _db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == row.TenantId && x.BranchId == row.BranchId && x.IsActive, ct);
        var senderTaxNo = NormalizeDigits(profile?.TaxNumber);
        if (senderTaxNo.Length is not (10 or 11))
            return BadRequest(new { error = "Gider pusulası gönderimi için aktif şube e-fatura profilinde geçerli Vergi No (10/11 hane) zorunludur." });

        row.Status = "Queued";
        row.SubmittedAt = DateTime.UtcNow;
        row.LastError = null;
        _db.ExpenseSlipAuditLogs.Add(BuildAuditLog(
            row.TenantId,
            row.BranchId,
            row.Id,
            "Queue",
            "Draft",
            row.Status,
            true,
            null,
            null,
            null));
        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            row.Id,
            row.DocumentNo,
            row.Status,
            message = "Gider pusulası gönderim kuyruğuna alındı (Faz-1 iskelet)."
        });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelExpenseSlipReq? req, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var row = await _db.ExpenseSlipDocuments.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound(new { error = "Belge bulunamadı." });

        var profile = await _db.EInvoiceProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == row.TenantId && x.BranchId == row.BranchId && x.IsActive, ct);
        var providerCode = string.IsNullOrWhiteSpace(profile?.ProviderCode) ? "edm" : profile.ProviderCode;
        var adapter = _providerResolver.Resolve(providerCode);
        if (adapter is not EdmSoapEInvoiceProviderAdapter edm)
            return BadRequest(new { error = $"Bu sağlayıcıda gider pusulası iptali desteklenmiyor: {providerCode}" });

        var cancelResult = await edm.CancelMmAsync(new EdmMmCancelRequest(
            row.TenantId,
            row.BranchId,
            row.Id,
            row.IntegratorDocumentId,
            row.Uuid,
            string.IsNullOrWhiteSpace(req?.Reason) ? "Gider pusulası iptal işlemi" : req!.Reason!,
            profile?.IntegratorUsername,
            profile?.IntegratorSecretRef), ct);

        if (!cancelResult.IsSuccess)
        {
            _db.ExpenseSlipAuditLogs.Add(BuildAuditLog(
                row.TenantId,
                row.BranchId,
                row.Id,
                "Cancel",
                row.Status,
                row.Status,
                false,
                JsonSerializer.Serialize(new { reason = req?.Reason }),
                cancelResult.RawResponse,
                cancelResult.ErrorMessage));
            await _db.SaveChangesAsync(ct);
            return BadRequest(new { error = cancelResult.ErrorMessage ?? "EDM CancelMM başarısız." });
        }

        var before = row.Status;
        row.Status = "Cancelled";
        row.LastError = null;
        if (!string.IsNullOrWhiteSpace(cancelResult.RawResponse))
            row.RawLastResponse = cancelResult.RawResponse!;
        _db.ExpenseSlipAuditLogs.Add(BuildAuditLog(
            row.TenantId,
            row.BranchId,
            row.Id,
            "Cancel",
            before,
            row.Status,
            true,
            JsonSerializer.Serialize(new { reason = req?.Reason }),
            cancelResult.RawResponse,
            null));
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.DocumentNo, row.Status, message = "Gider pusulası iptal edildi." });
    }

    [HttpPut("{id:guid}/draft")]
    public async Task<IActionResult> UpdateDraft(Guid id, [FromBody] UpdateExpenseSlipDraftReq req, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var row = await _db.ExpenseSlipDocuments.FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (row is null) return NotFound(new { error = "Belge bulunamadı." });
        if (!string.Equals(row.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Sadece taslak durumundaki gider pusulaları düzenlenebilir." });

        if (string.IsNullOrWhiteSpace(req.BuyerName))
            return BadRequest(new { error = "Alıcı adı zorunludur." });
        var taxNo = NormalizeDigits(req.BuyerTaxNumber);
        if (taxNo.Length is not (10 or 11))
            return BadRequest(new { error = "Alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (string.IsNullOrWhiteSpace(req.Workmanship))
            return BadRequest(new { error = "İşin mahiyeti zorunludur." });
        if (string.IsNullOrWhiteSpace(req.ProductType))
            return BadRequest(new { error = "İşin cinsi zorunludur." });
        if (req.QuantityGram <= 0m)
            return BadRequest(new { error = "Adet/Gram 0'dan büyük olmalıdır." });
        if (req.UnitPrice <= 0m)
            return BadRequest(new { error = "Birim fiyat 0'dan büyük olmalıdır." });
        if (req.GrandTotal <= 0m)
            return BadRequest(new { error = "Toplam tutar 0'dan büyük olmalıdır." });

        var beforeStatus = row.Status;
        row.BuyerName = req.BuyerName.Trim();
        row.BuyerTaxNumber = taxNo;
        row.Currency = string.IsNullOrWhiteSpace(req.Currency) ? "TRY" : req.Currency.Trim().ToUpperInvariant();
        row.GrandTotal = Math.Round(req.GrandTotal, 2, MidpointRounding.AwayFromZero);
        row.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        row.PayloadJson = JsonSerializer.Serialize(new
        {
            row.BranchId,
            row.SourceSaleId,
            buyerName = row.BuyerName,
            buyerTaxNumber = row.BuyerTaxNumber,
            grandTotal = row.GrandTotal,
            workmanship = req.Workmanship?.Trim(),
            productType = req.ProductType?.Trim(),
            quantityGram = req.QuantityGram,
            unitPrice = req.UnitPrice,
            lineTotal = req.LineTotal > 0m ? req.LineTotal : row.GrandTotal,
            currency = row.Currency,
            description = row.Description
        });

        _db.ExpenseSlipAuditLogs.Add(BuildAuditLog(
            row.TenantId,
            row.BranchId,
            row.Id,
            "UpdateDraft",
            beforeStatus,
            row.Status,
            true,
            row.PayloadJson,
            null,
            null));
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Id, row.DocumentNo, row.Status, message = "Gider pusulası taslağı güncellendi." });
    }

    [HttpPost("delete-selected")]
    public async Task<IActionResult> DeleteSelected([FromBody] DeleteSelectedExpenseSlipReq req, CancellationToken ct)
    {
        var denied = RequireExpenseSlipPermission();
        if (denied != null) return denied;

        var tid = _tenant.TenantId;
        var ids = (req.DocumentIds ?? [])
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return BadRequest(new { error = "Silinecek belge seçilmedi." });

        var selectedBranchId = _tenant.BranchId ?? Guid.Empty;
        // Eski SQL Server compatibility level ortamlarda ids.Contains(...) ifadesi
        // OPENJSON üretip sözdizimi hatasına düşebiliyor; bu yüzden id'leri tek tek çekiyoruz.
        var rows = new List<ExpenseSlipDocument>();
        foreach (var id in ids)
        {
            var row = await _db.ExpenseSlipDocuments
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
            if (row is null)
                continue;
            if (selectedBranchId != Guid.Empty && row.BranchId != selectedBranchId)
                continue;
            rows.Add(row);
        }
        if (rows.Count == 0)
            return Ok(new { deletedDocuments = 0 });

        foreach (var row in rows)
            row.IsDeleted = true;

        await _db.SaveChangesAsync(ct);
        return Ok(new { deletedDocuments = rows.Count });
    }

    private async Task<string> BuildDocumentNoAsync(Guid tenantId, Guid branchId, CancellationToken ct)
    {
        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"GPS-{datePart}-";
        var countToday = await _db.ExpenseSlipDocuments.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.BranchId == branchId && x.DocumentNo.StartsWith(prefix))
            .CountAsync(ct);
        return $"{prefix}{(countToday + 1):0000}";
    }

    private IActionResult? RequireExpenseSlipPermission()
    {
        if (CanUseExpenseSlip()) return null;
        return Forbid();
    }

    private bool CanUseExpenseSlip()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_expense_slip");
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Where(char.IsDigit).ToArray();
        return new string(chars);
    }

    private static ExpenseSlipAuditLog BuildAuditLog(
        Guid tenantId,
        Guid branchId,
        Guid documentId,
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
            TenantId = tenantId,
            BranchId = branchId,
            DocumentId = documentId,
            Action = action,
            StatusBefore = statusBefore,
            StatusAfter = statusAfter,
            IsSuccess = isSuccess,
            RequestJson = requestJson,
            ResponseRaw = responseRaw,
            ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage
        };
    }

    public sealed class CreateExpenseSlipDraftReq
    {
        public Guid BranchId { get; set; }
        public Guid? SourceSaleId { get; set; }
        public string BuyerName { get; set; } = "";
        public string BuyerTaxNumber { get; set; } = "";
        public string? Workmanship { get; set; }
        public string? ProductType { get; set; }
        public decimal? QuantityGram { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? LineTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public string Currency { get; set; } = "TRY";
        public string? Description { get; set; }
        public string? PayloadJson { get; set; }
    }

    public sealed class CancelExpenseSlipReq
    {
        public string? Reason { get; set; }
    }

    public sealed class UpdateExpenseSlipDraftReq
    {
        public string BuyerName { get; set; } = "";
        public string BuyerTaxNumber { get; set; } = "";
        public string? Workmanship { get; set; }
        public string? ProductType { get; set; }
        public decimal QuantityGram { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal LineTotal { get; set; }
        public decimal GrandTotal { get; set; }
        public string? Currency { get; set; }
        public string? Description { get; set; }
    }

    public sealed class DeleteSelectedExpenseSlipReq
    {
        public List<Guid> DocumentIds { get; set; } = new();
    }
}
