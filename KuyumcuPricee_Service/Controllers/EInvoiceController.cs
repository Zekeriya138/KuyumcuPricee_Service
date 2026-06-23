using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using KUYUMCU.Price_Service.Services;
using kuyumcu_application.Abstractions;
using kuyumcu_domain.Entities;
using kuyumcu_domain.Enums;
using kuyumcu_infrastructure.Services;
using kuyumcu_infrastructure.Persistence;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/einvoice")]
[Authorize]
public sealed class EInvoiceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEInvoiceWorkflowService _workflow;
    private readonly IEInvoiceProviderResolver _providerResolver;

    public EInvoiceController(AppDbContext db, ITenantContext tenant, IEInvoiceWorkflowService workflow, IEInvoiceProviderResolver providerResolver)
    {
        _db = db;
        _tenant = tenant;
        _workflow = workflow;
        _providerResolver = providerResolver;
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile([FromQuery] Guid? branchId, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId;
        if (!bid.HasValue || bid.Value == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var profile = await _db.EInvoiceProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == bid.Value, ct);

        if (profile is null)
        {
            return Ok(new EInvoiceProfileDto
            {
                BranchId = bid.Value,
                ProviderCode = "edm",
                IsActive = false,
                DefaultInvoicePrefix = "AUR",
                DefaultArchivePrefix = "ARS",
                SpecialMatrahCraftedVatRatePercent = 20m,
                SpecialMatrahZiynetVatRatePercent = 20m,
                SalesInvoiceVatRatePercent = 20m,
                AutoDraftEnabled = true,
                AutoDraftMatchMode = "ANY"
            });
        }

        return Ok(ToDto(profile));
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> UpsertProfile([FromBody] SaveEInvoiceProfileReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        try
        {
            await _db.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('EInvoiceProfiles', 'IntegratorCompanyCode') IS NOT NULL
BEGIN
    ALTER TABLE [dbo].[EInvoiceProfiles] ALTER COLUMN [IntegratorCompanyCode] nvarchar(max) NULL;
END", ct);
        }
        catch
        {
            // Kolon zaten uygunsa veya SQL yetkisi yoksa kaydı engelleme.
        }
        if (req.BranchId == Guid.Empty) return BadRequest(new { error = "BranchId zorunludur." });
        if (string.IsNullOrWhiteSpace(req.TaxNumber) || req.TaxNumber.Trim().Length < 10)
            return BadRequest(new { error = "Vergi numarası en az 10 hane olmalıdır." });
        if (string.IsNullOrWhiteSpace(req.TaxOffice))
            return BadRequest(new { error = "Vergi dairesi zorunludur." });
        if (string.IsNullOrWhiteSpace(req.CompanyAddress))
            return BadRequest(new { error = "Firma adresi zorunludur." });
        if (string.IsNullOrWhiteSpace(req.IntegratorUsername))
            return BadRequest(new { error = "Entegratör kullanıcı adı zorunludur." });
        if (req.SpecialMatrahCraftedVatRatePercent < 0m || req.SpecialMatrahCraftedVatRatePercent > 100m)
            return BadRequest(new { error = "İşçilikli ürün KDV oranı 0-100 arasında olmalıdır." });
        if (req.SpecialMatrahZiynetVatRatePercent < 0m || req.SpecialMatrahZiynetVatRatePercent > 100m)
            return BadRequest(new { error = "Ziynet ürün KDV oranı 0-100 arasında olmalıdır." });
        if (req.SalesInvoiceVatRatePercent < 0m || req.SalesInvoiceVatRatePercent > 100m)
            return BadRequest(new { error = "Satış faturası KDV oranı 0-100 arasında olmalıdır." });
        if (req.AutoDraftMinTotal.HasValue && req.AutoDraftMaxTotal.HasValue &&
            req.AutoDraftMaxTotal.Value > 0m && req.AutoDraftMinTotal.Value > req.AutoDraftMaxTotal.Value)
            return BadRequest(new { error = "Otomatik taslak alt tutarı üst tutardan büyük olamaz." });
        var normalizedWorkmanshipRules = (req.WorkmanshipRules ?? [])
            .Select(x => new WorkmanshipRuleSetting
            {
                ProductType = x.ProductType,
                Karat = x.Karat,
                MinTotal = x.MinTotal,
                MaxTotal = x.MaxTotal,
                Percentage = x.Percentage
            })
            .ToList();

        foreach (var rule in normalizedWorkmanshipRules)
        {
            var productType = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(rule.ProductType);
            if (productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted &&
                productType != EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet)
                return BadRequest(new { error = "İşçilik kuralı ürün tipi geçersiz." });
            if (rule.MinTotal < 0m)
                return BadRequest(new { error = "İşçilik kuralı alt limit 0'dan küçük olamaz." });
            if (rule.MaxTotal <= 0m)
                return BadRequest(new { error = "İşçilik kuralı üst limit 0'dan büyük olmalıdır." });
            if (rule.MinTotal > rule.MaxTotal)
                return BadRequest(new { error = "İşçilik kuralı alt limit üst limitten büyük olamaz." });
            if (rule.Percentage < 0m || rule.Percentage > 100m)
                return BadRequest(new { error = "İşçilik yüzdesi 0-100 arasında olmalıdır." });
            var normalizedSelector = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipSelector(productType, rule.Karat);
            if (string.IsNullOrWhiteSpace(normalizedSelector))
            {
                var msg = string.Equals(productType, EInvoiceProfileSettingsCodec.WorkmanshipProductTypeZiynet, StringComparison.OrdinalIgnoreCase)
                    ? "Ziynet ürün seçimi geçersiz."
                    : "Ayar seçimi geçersiz. Sadece 24K, 22K, 18K, 14K, 8K desteklenir.";
                return BadRequest(new { error = msg });
            }
        }
        var overlapGroup = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipRules(normalizedWorkmanshipRules)
            .GroupBy(x => $"{EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(x.ProductType)}|{EInvoiceProfileSettingsCodec.NormalizeWorkmanshipKarat(x.Karat)}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g =>
            {
                var ordered = g.OrderBy(x => x.MinTotal).ThenBy(x => x.MaxTotal).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    if (ordered[i].MinTotal <= ordered[i - 1].MaxTotal)
                        return true;
                }
                return false;
            });
        if (overlapGroup is not null)
            return BadRequest(new { error = "İşçilik kurallarında çakışan tutar aralıkları var. Aynı ürün tipi ve ayar için aralıklar üst üste gelemez." });

        var profile = await _db.EInvoiceProfiles.FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == req.BranchId, ct);
        if (profile is null)
        {
            profile = new kuyumcu_domain.Entities.EInvoiceProfile
            {
                TenantId = tid,
                BranchId = req.BranchId
            };
            _db.EInvoiceProfiles.Add(profile);
        }

        profile.ProviderCode = string.IsNullOrWhiteSpace(req.ProviderCode) ? "edm" : req.ProviderCode.Trim().ToLowerInvariant();
        profile.CompanyName = string.IsNullOrWhiteSpace(req.CompanyName) ? "Firma" : req.CompanyName.Trim();
        profile.CompanyAddress = req.CompanyAddress.Trim();
        profile.TaxNumber = req.TaxNumber.Trim();
        profile.TaxOffice = req.TaxOffice.Trim();
        profile.SenderLabel = req.SenderLabel?.Trim();
        profile.DefaultInvoicePrefix = string.IsNullOrWhiteSpace(req.DefaultInvoicePrefix) ? "AUR" : req.DefaultInvoicePrefix.Trim().ToUpperInvariant();
        profile.DefaultArchivePrefix = string.IsNullOrWhiteSpace(req.DefaultArchivePrefix) ? "ARS" : req.DefaultArchivePrefix.Trim().ToUpperInvariant();
        profile.IntegratorUsername = req.IntegratorUsername?.Trim();
        if (!string.IsNullOrWhiteSpace(req.IntegratorPassword))
            profile.IntegratorSecretRef = req.IntegratorPassword.Trim();
        profile.IsActive = req.IsActive;
        profile.IntegratorCompanyCode = EInvoiceProfileSettingsCodec.Encode(new EInvoiceProfileSettings
        {
            SpecialMatrahCraftedVatRatePercent = req.SpecialMatrahCraftedVatRatePercent,
            SpecialMatrahZiynetVatRatePercent = req.SpecialMatrahZiynetVatRatePercent,
            SalesInvoiceVatRatePercent = req.SalesInvoiceVatRatePercent,
            AutoDraftEnabled = req.AutoDraftEnabled,
            AutoDraftMatchMode = req.AutoDraftMatchMode,
            AutoDraftAllowedPaymentMethods = req.AutoDraftAllowedPaymentMethods ?? [],
            AutoDraftMinTotal = req.AutoDraftMinTotal,
            AutoDraftMaxTotal = req.AutoDraftMaxTotal,
            WorkmanshipRules = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipRules(normalizedWorkmanshipRules)
        });

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(profile));
    }

    [HttpPost("profile/test-connection")]
    [Authorize]
    public async Task<IActionResult> TestConnection([FromBody] TestEInvoiceConnectionReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var providerCode = string.IsNullOrWhiteSpace(req.ProviderCode) ? "edm" : req.ProviderCode.Trim().ToLowerInvariant();
        var adapter = _providerResolver.Resolve(providerCode);
        var res = await adapter.TestConnectionAsync(new kuyumcu_application.Abstractions.EInvoiceConnectionTestRequest(
            _tenant.TenantId,
            req.BranchId,
            providerCode,
            req.IntegratorUsername?.Trim(),
            req.IntegratorPassword?.Trim(),
            req.TaxNumber?.Trim() ?? "",
            req.TaxOffice?.Trim() ?? "",
            req.CompanyAddress?.Trim() ?? ""), ct);
        // WPF tarafında başarısız testin detay mesajını gösterebilmek için her durumda 200 döndürüyoruz.
        return Ok(res);
    }

    [HttpGet("outgoing")]
    public async Task<IActionResult> ListOutgoing(
        [FromQuery] Guid? branchId,
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery, Range(1, 1000)] int page = 1,
        [FromQuery, Range(1, 500)] int pageSize = 50,
        CancellationToken ct = default)
    {
        var tid = _tenant.TenantId;
        var q = _db.EInvoiceDocuments
            .AsNoTracking()
            .Where(x => x.TenantId == tid && x.Direction == "Outgoing");

        if (branchId.HasValue && branchId.Value != Guid.Empty)
            q = q.Where(x => x.BranchId == branchId.Value);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status);
        if (from.HasValue)
            q = q.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue)
            q = q.Where(x => x.CreatedAt <= to.Value);

        var total = await q.CountAsync(ct);
        var docs = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.InvoiceId,
                x.BranchId,
                BranchName = x.Branch != null ? (x.Branch.Name ?? "") : "",
                x.DocumentType,
                x.InvoiceNumber,
                x.Status,
                x.GrandTotal,
                x.Currency,
                x.RetryCount,
                x.LastError,
                x.IntegratorDocumentId,
                x.Uuid,
                x.Ettn,
                x.SubmittedAt,
                x.DeliveredAt,
                x.CancelledAt,
                x.CreatedAt
            })
            .ToListAsync(ct);

        return Ok(new
        {
            total,
            page,
            pageSize,
            items = docs
        });
    }

    [HttpGet("outgoing/{id:guid}")]
    public async Task<IActionResult> GetOutgoing(Guid id, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (doc is null) return NotFound();

        var outboxes = await _db.EInvoiceOutboxes.AsNoTracking()
            .Where(x => x.TenantId == tid && x.DocumentId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        return Ok(new
        {
            document = doc,
            outbox = outboxes
        });
    }

    [HttpGet("outgoing/{id:guid}/ubl-preview")]
    [Authorize]
    public async Task<IActionResult> GetOutgoingUblPreview(Guid id, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });

        var payload = await _db.EInvoiceOutboxes.AsNoTracking()
            .Where(x => x.TenantId == tid && x.DocumentId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.PayloadJson)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload))
            return BadRequest(new { error = "UBL payload bulunamadı. Belge için henüz gönderim kuyruğu oluşturulmamış olabilir." });

        var ublXml = ExtractUblXml(payload);
        if (string.IsNullOrWhiteSpace(ublXml))
            return BadRequest(new { error = "Payload içinde UBL bulunamadı (ublXml/ublBase64)." });

        var validation = ValidateUblRequiredProperties(ublXml);
        return Ok(new
        {
            documentId = doc.Id,
            doc.InvoiceNumber,
            doc.DocumentType,
            doc.Status,
            doc.IntegratorDocumentId,
            doc.Uuid,
            doc.Ettn,
            validation,
            ublXml
        });
    }

    [HttpPost("outgoing/{invoiceId:guid}/send")]
    [Authorize]
    public async Task<IActionResult> SendOutgoing(Guid invoiceId, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.InvoiceId == invoiceId, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });
        if (string.Equals(doc.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Bu fatura zaten gönderildi ve onaylandı." });
        if (string.Equals(doc.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "İptal edilmiş fatura tekrar gönderilemez." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && doc.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Seçili şube ile belge şubesi farklı. Lütfen belgeye ait şubeye geçin." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId && x.IsActive, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için aktif e-fatura entegrasyonu bulunamadı. Önce ayarları kaydedin." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername))
            return BadRequest(new { error = "EDM kullanıcı adı zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return BadRequest(new { error = "EDM şifresi zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.SenderLabel))
            return BadRequest(new { error = "EDM gönderici etiketi (SenderLabel) zorunludur. E-Fatura ayarlarında SenderLabel girin." });

        var queued = await _workflow.QueueManualSendAsync(tid, invoiceId, null, ct);
        if (queued is not null)
            queued = await _workflow.TryProcessPendingImmediatelyAsync(tid, invoiceId, ct);
        if (queued is null) return NotFound(new { error = "Belge bulunamadı." });
        return Ok(new { queued.Id, queued.InvoiceId, queued.Status, queued.RetryCount });
    }

    [HttpGet("outgoing/{invoiceId:guid}/send-preview")]
    [Authorize]
    public async Task<IActionResult> GetSendPreview(Guid invoiceId, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.InvoiceId == invoiceId, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && doc.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Seçili şube ile belge şubesi farklı. Lütfen belgeye ait şubeye geçin." });

        var draft = await _workflow.BuildManualDraftAsync(tid, invoiceId, ct);
        if (draft is null) return NotFound(new { error = "Önizleme verisi oluşturulamadı." });

        var branch = await _db.Branches.AsNoTracking()
            .Where(x => x.TenantId == tid && x.Id == doc.BranchId)
            .Select(x => new { x.Name, x.Address })
            .FirstOrDefaultAsync(ct);
        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId && x.IsActive, ct);

        return Ok(new
        {
            documentId = doc.Id,
            doc.InvoiceId,
            doc.InvoiceNumber,
            doc.Status,
            sender = new
            {
                branchName = branch?.Name,
                profile?.CompanyName,
                profile?.CompanyAddress,
                profile?.TaxNumber,
                profile?.TaxOffice,
                profile?.SenderLabel
            },
            draft
        });
    }

    [HttpPost("outgoing/{invoiceId:guid}/send-preview")]
    [Authorize]
    public async Task<IActionResult> SendOutgoingPreview(Guid invoiceId, [FromBody] SendPreviewReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        if (req?.Draft is null)
            return BadRequest(new { error = "Önizleme bilgisi zorunludur." });
        var draft = req.Draft;
        if (string.IsNullOrWhiteSpace(draft.BuyerName))
            return BadRequest(new { error = "Alıcı adı/soyadı zorunludur." });
        var buyerTaxNo = new string((draft.BuyerTaxNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (buyerTaxNo.Length != 10 && buyerTaxNo.Length != 11)
            return BadRequest(new { error = "Alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (draft.Lines is null || draft.Lines.Count == 0)
            return BadRequest(new { error = "Fatura satırı bulunamadı." });
        if (draft.Lines.Any(x => string.IsNullOrWhiteSpace(x.ProductName)))
            return BadRequest(new { error = "Satırlarda ürün adı zorunludur." });

        var tid = _tenant.TenantId;
        var doc = await _db.EInvoiceDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tid && x.InvoiceId == invoiceId, ct);
        if (doc is null) return NotFound(new { error = "Belge bulunamadı." });
        if (string.Equals(doc.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Bu fatura zaten gönderildi ve onaylandı." });
        if (string.Equals(doc.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "İptal edilmiş fatura tekrar gönderilemez." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && doc.BranchId != _tenant.BranchId.Value)
            return BadRequest(new { error = "Seçili şube ile belge şubesi farklı. Lütfen belgeye ait şubeye geçin." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tid && x.BranchId == doc.BranchId && x.IsActive, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için aktif e-fatura entegrasyonu bulunamadı. Önce ayarları kaydedin." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername))
            return BadRequest(new { error = "EDM kullanıcı adı zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return BadRequest(new { error = "EDM şifresi zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.SenderLabel))
            return BadRequest(new { error = "EDM gönderici etiketi (SenderLabel) zorunludur. E-Fatura ayarlarında SenderLabel girin." });

        var normalizedDraft = draft with { BuyerTaxNumber = buyerTaxNo };
        var isEArchive = string.Equals(normalizedDraft.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase);
        if (isEArchive && !CanUseEArchive())
            return Forbid();
        if (!isEArchive && buyerTaxNo.Length != 10)
            return BadRequest(new { error = "e-Fatura için alıcı vergi kimliği VKN (10 hane) olmalıdır. TCKN/diğer durumlarda belge tipini E-Arşiv seçin." });
        if (!isEArchive && string.IsNullOrWhiteSpace(normalizedDraft.BuyerEmail))
            return BadRequest(new { error = "e-Fatura için alıcı etiketi zorunludur. Alıcı E-Posta alanına EDM alıcı etiketi girin veya belge tipini E-Arşiv seçin." });

        var queued = await _workflow.QueueManualSendAsync(tid, invoiceId, normalizedDraft, ct);
        if (queued is not null)
            queued = await _workflow.TryProcessPendingImmediatelyAsync(tid, invoiceId, ct);
        if (queued is null) return NotFound(new { error = "Belge bulunamadı." });
        return Ok(new { queued.Id, queued.InvoiceId, queued.Status, queued.RetryCount });
    }

    [HttpPost("outgoing/{id:guid}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelOutgoing(Guid id, [FromBody] CancelReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var ok = await _workflow.CancelDocumentAsync(tid, id, req.Reason ?? "Kullanıcı iptal talebi", ct);
        if (!ok) return BadRequest(new { error = "Belge iptal isteği gönderilemedi." });
        return Ok(new { success = true });
    }

    [HttpPost("outgoing/delete-selected")]
    [Authorize]
    public async Task<IActionResult> DeleteSelectedOutgoing([FromBody] DeleteSelectedReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var ids = (req.DocumentIds ?? [])
            .Where(x => x != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return BadRequest(new { error = "Silinecek belge seçilmedi." });

        var selectedBranchId = _tenant.BranchId ?? Guid.Empty;
        var docs = new List<EInvoiceDocument>();
        var skippedByBranch = 0;
        foreach (var id in ids)
        {
            var doc = await _db.EInvoiceDocuments
                .FirstOrDefaultAsync(x => x.TenantId == tid && x.Id == id, ct);
            if (doc is null || doc.IsDeleted)
                continue;
            if (selectedBranchId != Guid.Empty && doc.BranchId != selectedBranchId)
            {
                skippedByBranch++;
                continue;
            }
            docs.Add(doc);
        }
        if (docs.Count == 0)
            return Ok(new { deletedDocuments = 0, deletedOutboxes = 0, skippedByBranch });

        var deletedOutboxes = 0;
        foreach (var doc in docs)
        {
            doc.IsDeleted = true;
            var outboxes = await _db.EInvoiceOutboxes
                .Where(x => x.TenantId == tid && x.DocumentId == doc.Id && !x.IsDeleted)
                .ToListAsync(ct);
            foreach (var outbox in outboxes)
            {
                outbox.IsDeleted = true;
                deletedOutboxes++;
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new
        {
            deletedDocuments = docs.Count,
            deletedOutboxes,
            skippedByBranch
        });
    }

    [HttpPost("manual/taxpayer-query")]
    [Authorize]
    public async Task<IActionResult> QueryManualTaxpayer([FromBody] ManualTaxpayerQueryReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var taxNo = NormalizeDigits(req.TaxNumber);
        if (taxNo.Length != 10 && taxNo.Length != 11)
            return BadRequest(new { error = "TCKN/VKN 10 veya 11 hane olmalıdır." });

        var branchId = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "Şube seçimi bulunamadı." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.BranchId == branchId && x.IsActive, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için aktif e-fatura entegrasyonu bulunamadı." });

        var title = (req.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            var branchCustomers = await _db.Customers
                .AsNoTracking()
                .Where(x => x.TenantId == _tenant.TenantId &&
                            x.BranchId == branchId &&
                            !x.IsDeleted &&
                            x.NationalId != null)
                .Select(x => new { x.FullName, x.NationalId })
                .ToListAsync(ct);

            title = branchCustomers
                .Where(x => NormalizeDigits(x.NationalId) == taxNo)
                .Select(x => x.FullName)
                .FirstOrDefault() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title))
                title = taxNo.Length == 11 ? "NİHAİ TÜKETİCİ" : $"VKN {taxNo}";
        }
        var documentType = taxNo.Length == 10 ? "EFatura" : "EArsiv";
        var source = "rule";
        var message = "Mükellefiyet, TCKN/VKN kuralına göre belirlendi.";

        try
        {
            var adapter = _providerResolver.Resolve(string.IsNullOrWhiteSpace(profile.ProviderCode) ? "edm" : profile.ProviderCode);
            if (adapter is EdmSoapEInvoiceProviderAdapter edmAdapter)
            {
                var edmResult = await edmAdapter.QueryTaxpayerAsync(profile.IntegratorUsername, profile.IntegratorSecretRef, taxNo, ct);
                if (edmResult.IsSuccess && edmResult.IsEInvoiceTaxpayer.HasValue)
                {
                    documentType = edmResult.IsEInvoiceTaxpayer.Value ? "EFatura" : "EArsiv";
                    source = "edm";
                    message = string.IsNullOrWhiteSpace(edmResult.Message)
                        ? "Mükellefiyet EDM üzerinden sorgulandı."
                        : edmResult.Message!;
                }
                else if (!string.IsNullOrWhiteSpace(edmResult.Message))
                {
                    source = "rule-fallback";
                    message = $"EDM sorgusu tamamlanamadı, kural tabanlı sonuç kullanıldı: {edmResult.Message}";
                }
            }
        }
        catch
        {
            // fallback: rule based result is still returned
        }

        return Ok(new ManualTaxpayerQueryResponse
        {
            TaxNumber = taxNo,
            Title = title,
            DocumentType = documentType,
            LiabilityType = documentType == "EFatura" ? "E-Fatura Mükellefi" : "E-Arşiv",
            IsEInvoiceTaxpayer = documentType == "EFatura",
            Source = source,
            Message = message
        });
    }

    [HttpPost("manual/send")]
    [Authorize]
    public async Task<IActionResult> CreateManualAndSend([FromBody] ManualCreateAndSendReq req, CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        if (req?.Draft is null)
            return BadRequest(new { error = "Taslak bilgisi zorunludur." });

        var branchId = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (branchId == Guid.Empty)
            return BadRequest(new { error = "Şube seçimi bulunamadı." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && _tenant.BranchId.Value != branchId)
            return BadRequest(new { error = "Seçili profil şubesi ile belge şubesi farklı." });

        var buyerTaxNo = NormalizeDigits(req.Draft.BuyerTaxNumber);
        if (buyerTaxNo.Length != 10 && buyerTaxNo.Length != 11)
            return BadRequest(new { error = "Alıcı TCKN/VKN 10 veya 11 hane olmalıdır." });
        if (string.IsNullOrWhiteSpace(req.Draft.BuyerName))
            return BadRequest(new { error = "Alıcı ünvanı/adı zorunludur." });
        if (req.Draft.Lines is null || req.Draft.Lines.Count == 0)
            return BadRequest(new { error = "En az bir fatura satırı zorunludur." });

        var profile = await _db.EInvoiceProfiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == _tenant.TenantId && x.BranchId == branchId && x.IsActive, ct);
        if (profile is null)
            return BadRequest(new { error = "Bu şube için aktif e-fatura entegrasyonu bulunamadı. Önce ayarları kaydedin." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorUsername))
            return BadRequest(new { error = "EDM kullanıcı adı zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.IntegratorSecretRef))
            return BadRequest(new { error = "EDM şifresi zorunludur." });
        if (string.IsNullOrWhiteSpace(profile.SenderLabel))
            return BadRequest(new { error = "EDM gönderici etiketi (SenderLabel) zorunludur." });

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { error = "Kullanıcı kimliği alınamadı." });

        Customer? customer = null;
        if (req.CustomerId.HasValue && req.CustomerId.Value != Guid.Empty)
        {
            customer = await _db.Customers.FirstOrDefaultAsync(
                x => x.TenantId == _tenant.TenantId && x.Id == req.CustomerId.Value, ct);
            if (customer is null)
                return BadRequest(new { error = "Seçilen müşteri/tedarikçi bulunamadı." });
        }

        var normalizedDocType = string.Equals(req.Draft.DocumentType, "EArsiv", StringComparison.OrdinalIgnoreCase)
            ? "EArsiv"
            : "EFatura";
        if (normalizedDocType == "EArsiv" && !CanUseEArchive())
            return Forbid();
        if (normalizedDocType == "EFatura" && buyerTaxNo.Length != 10)
            return BadRequest(new { error = "e-Fatura için alıcı vergi kimliği VKN (10 hane) olmalıdır." });

        var normalizedLines = req.Draft.Lines
            .Select((x, idx) => new ManualEInvoiceLineDraft(
                idx + 1,
                string.IsNullOrWhiteSpace(x.ProductName) ? "Ürün" : x.ProductName.Trim(),
                x.Barcode,
                x.ProductCode,
                x.Quantity <= 0 ? 1m : x.Quantity,
                string.IsNullOrWhiteSpace(x.UnitCode) ? "NIU" : x.UnitCode,
                x.UnitPrice < 0 ? 0m : x.UnitPrice,
                x.KdvRate < 0 ? 0m : x.KdvRate,
                x.KdvAmount,
                x.TotalAmount,
                x.Gram,
                x.Karat,
                x.Workmanship,
                x.ProductCategory,
                x.HasGoldEquivalent,
                x.StoneInfo,
                x.SerialNumber))
            .ToList();
        var normalizedDraft = new ManualEInvoiceDraft(
            normalizedDocType,
            req.Draft.BuyerName.Trim(),
            buyerTaxNo,
            req.Draft.BuyerAddress?.Trim(),
            req.Draft.BuyerCity?.Trim(),
            req.Draft.BuyerDistrict?.Trim(),
            req.Draft.BuyerPostalCode?.Trim(),
            req.Draft.IssueDateText?.Trim(),
            req.Draft.IssueTimeText?.Trim(),
            req.Draft.BuyerEmail?.Trim(),
            string.IsNullOrWhiteSpace(req.Draft.Currency) ? "TRY" : req.Draft.Currency,
            normalizedLines);

        var invoiceDateUtc = ResolveIssueDateUtc(req.Draft.IssueDateText, req.Draft.IssueTimeText);

        var grandTotal = normalizedLines.Sum(x =>
            x.TotalAmount ?? (Math.Round((x.Quantity <= 0 ? 1m : x.Quantity) * Math.Max(0m, x.UnitPrice), 2, MidpointRounding.AwayFromZero) +
                              Math.Round((Math.Round((x.Quantity <= 0 ? 1m : x.Quantity) * Math.Max(0m, x.UnitPrice), 2, MidpointRounding.AwayFromZero)) * ((x.KdvRate > 1 ? x.KdvRate : x.KdvRate * 100m) / 100m), 2, MidpointRounding.AwayFromZero)));

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var sale = new Sale
        {
            TenantId = _tenant.TenantId,
            BranchId = branchId,
            UserId = userId,
            CustomerId = customer?.Id,
            PaymentType = "ManualEInvoice",
            Items = new List<SaleItem>()
        };
        _db.Sales.Add(sale);

        var lineNo = 1;
        foreach (var line in normalizedLines)
        {
            var qty = line.Quantity <= 0 ? 1m : line.Quantity;
            var net = Math.Round(qty * line.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var kdvRatePercent = line.KdvRate > 1 ? line.KdvRate : line.KdvRate * 100m;
            var kdvAmount = line.KdvAmount ?? Math.Round(net * (kdvRatePercent / 100m), 2, MidpointRounding.AwayFromZero);
            var total = line.TotalAmount ?? (net + kdvAmount);

            sale.Items.Add(new SaleItem
            {
                TenantId = _tenant.TenantId,
                LineNo = lineNo++,
                Kind = ResolveManualLineItemKind(line.Karat, line.ProductCategory, kdvRatePercent),
                ProductCode = string.IsNullOrWhiteSpace(line.ProductCode) ? $"MANUAL-{(req.ProductType ?? "ALTIN").Trim().ToUpperInvariant()}" : line.ProductCode.Trim(),
                ProductName = line.ProductName,
                Karat = line.Karat ?? req.ProductType ?? "",
                Category = line.ProductCategory,
                Quantity = qty,
                UnitPrice = line.UnitPrice,
                Discount = 0m,
                TaxRate = kdvRatePercent / 100m,
                LineTotal = total
            });
        }

        var invoice = new Invoice
        {
            TenantId = _tenant.TenantId,
            SaleId = sale.Id,
            BranchId = branchId,
            CustomerId = customer?.Id,
            InvoiceDate = invoiceDateUtc,
            GrandTotal = Math.Round(grandTotal, 2, MidpointRounding.AwayFromZero),
            PaymentType = "ManualEInvoice",
            IsExported = false
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);

        var queuedDoc = await _workflow.QueueInvoiceAsync(invoice, customer, ct);
        queuedDoc = await _workflow.QueueManualSendAsync(_tenant.TenantId, invoice.Id, normalizedDraft, ct) ?? queuedDoc;
        queuedDoc = await _workflow.TryProcessPendingImmediatelyAsync(_tenant.TenantId, invoice.Id, ct) ?? queuedDoc;
        await tx.CommitAsync(ct);

        return Ok(new
        {
            queuedDoc.Id,
            queuedDoc.InvoiceId,
            queuedDoc.Status,
            queuedDoc.DocumentType,
            queuedDoc.InvoiceNumber,
            queuedDoc.RetryCount,
            message = "Manuel fatura oluşturuldu ve gönderim kuyruğuna alındı."
        });
    }

    [HttpGet("ops/health")]
    [Authorize]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        if (!CanUseEInvoice())
            return Forbid();
        var tid = _tenant.TenantId;
        var now = DateTime.UtcNow;
        var pending = await _db.EInvoiceOutboxes.CountAsync(x => x.TenantId == tid && x.Status == "Pending", ct);
        var deadLetter = await _db.EInvoiceOutboxes.CountAsync(x => x.TenantId == tid && x.Status == "DeadLetter", ct);
        var failedDocs = await _db.EInvoiceDocuments.CountAsync(x => x.TenantId == tid && x.Status == "Failed", ct);
        var verifiedWebhooks = await _db.EInvoiceWebhookLogs.CountAsync(x => x.TenantId == tid && x.IsVerified, ct);
        var invalidWebhooks = await _db.EInvoiceWebhookLogs.CountAsync(x => x.TenantId == tid && !x.IsVerified, ct);
        var delayed = await _db.EInvoiceOutboxes.CountAsync(x => x.TenantId == tid && x.Status == "Pending" && x.NextAttemptAt < now.AddMinutes(-5), ct);
        return Ok(new
        {
            pendingOutbox = pending,
            delayedOutbox = delayed,
            deadLetterOutbox = deadLetter,
            failedDocuments = failedDocs,
            verifiedWebhooks,
            invalidWebhooks,
            checkedAtUtc = now
        });
    }

    [HttpPost("webhook/{providerCode}")]
    [AllowAnonymous]
    public async Task<IActionResult> ReceiveWebhook(string providerCode, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (_tenant.TenantId == Guid.Empty)
            return BadRequest(new { error = "Tenant gereklidir." });

        var payload = body.ValueKind == JsonValueKind.Undefined ? "{}" : body.GetRawText();
        var signature = Request.Headers.TryGetValue("X-Webhook-Signature", out var sig) ? sig.ToString() : "";
        var headers = Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var result = await _workflow.ProcessWebhookAsync(
            _tenant.TenantId,
            _tenant.BranchId ?? Guid.Empty,
            providerCode,
            signature,
            payload,
            headers,
            ct);

        if (!result.IsSuccess)
            return BadRequest(new { error = result.Message, logId = result.LogId });

        return Ok(new { success = true, result.LogId });
    }

    public sealed class CancelReq
    {
        public string? Reason { get; set; }
    }

    public sealed class DeleteSelectedReq
    {
        public List<Guid> DocumentIds { get; set; } = new();
    }

    public sealed class SaveEInvoiceProfileReq
    {
        public Guid BranchId { get; set; }
        public string ProviderCode { get; set; } = "edm";
        public bool IsActive { get; set; }
        public string CompanyName { get; set; } = "";
        public string CompanyAddress { get; set; } = "";
        public string TaxNumber { get; set; } = "";
        public string TaxOffice { get; set; } = "";
        public string? SenderLabel { get; set; }
        public string? IntegratorUsername { get; set; }
        public string? IntegratorPassword { get; set; }
        public string DefaultInvoicePrefix { get; set; } = "AUR";
        public string DefaultArchivePrefix { get; set; } = "ARS";
        public decimal SpecialMatrahCraftedVatRatePercent { get; set; } = 20m;
        public decimal SpecialMatrahZiynetVatRatePercent { get; set; } = 20m;
        public decimal SalesInvoiceVatRatePercent { get; set; } = 20m;
        public bool AutoDraftEnabled { get; set; } = true;
        public string AutoDraftMatchMode { get; set; } = "ANY";
        public List<string>? AutoDraftAllowedPaymentMethods { get; set; }
        public decimal? AutoDraftMinTotal { get; set; }
        public decimal? AutoDraftMaxTotal { get; set; }
        public List<WorkmanshipRuleDto>? WorkmanshipRules { get; set; }
    }

    public sealed class TestEInvoiceConnectionReq
    {
        public Guid BranchId { get; set; }
        public string ProviderCode { get; set; } = "edm";
        public string TaxNumber { get; set; } = "";
        public string TaxOffice { get; set; } = "";
        public string CompanyAddress { get; set; } = "";
        public string? IntegratorUsername { get; set; }
        public string? IntegratorPassword { get; set; }
    }

    public sealed class EInvoiceProfileDto
    {
        public Guid BranchId { get; set; }
        public string ProviderCode { get; set; } = "edm";
        public bool IsActive { get; set; }
        public string CompanyName { get; set; } = "";
        public string CompanyAddress { get; set; } = "";
        public string TaxNumber { get; set; } = "";
        public string TaxOffice { get; set; } = "";
        public string? SenderLabel { get; set; }
        public string? IntegratorUsername { get; set; }
        public string DefaultInvoicePrefix { get; set; } = "AUR";
        public string DefaultArchivePrefix { get; set; } = "ARS";
        public decimal SpecialMatrahCraftedVatRatePercent { get; set; } = 20m;
        public decimal SpecialMatrahZiynetVatRatePercent { get; set; } = 20m;
        public decimal SalesInvoiceVatRatePercent { get; set; } = 20m;
        public bool AutoDraftEnabled { get; set; } = true;
        public string AutoDraftMatchMode { get; set; } = "ANY";
        public List<string> AutoDraftAllowedPaymentMethods { get; set; } = new();
        public decimal? AutoDraftMinTotal { get; set; }
        public decimal? AutoDraftMaxTotal { get; set; }
        public List<WorkmanshipRuleDto> WorkmanshipRules { get; set; } = new();
    }

    public sealed class WorkmanshipRuleDto
    {
        public string ProductType { get; set; } = EInvoiceProfileSettingsCodec.WorkmanshipProductTypeCrafted;
        public string? Karat { get; set; }
        public decimal MinTotal { get; set; }
        public decimal MaxTotal { get; set; }
        public decimal Percentage { get; set; }
    }

    public sealed class SendPreviewReq
    {
        public ManualEInvoiceDraft? Draft { get; set; }
    }

    public sealed class ManualTaxpayerQueryReq
    {
        public Guid BranchId { get; set; }
        public string TaxNumber { get; set; } = "";
        public string? Title { get; set; }
    }

    public sealed class ManualTaxpayerQueryResponse
    {
        public string TaxNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string DocumentType { get; set; } = "EArsiv";
        public string LiabilityType { get; set; } = "E-Arşiv";
        public bool IsEInvoiceTaxpayer { get; set; }
        public string Source { get; set; } = "rule";
        public string Message { get; set; } = "";
    }

    public sealed class ManualCreateAndSendReq
    {
        public Guid BranchId { get; set; }
        public Guid? CustomerId { get; set; }
        public string? ProductType { get; set; }
        public string? CalculationMode { get; set; }
        public decimal? MarketUnitPrice { get; set; }
        public decimal? HasGoldPrice { get; set; }
        public decimal? Workmanship { get; set; }
        public decimal? Gram { get; set; }
        public decimal? TotalAmount { get; set; }
        public ManualEInvoiceDraft? Draft { get; set; }
    }

    private static EInvoiceProfileDto ToDto(kuyumcu_domain.Entities.EInvoiceProfile p)
    {
        var settings = EInvoiceProfileSettingsCodec.Decode(p.IntegratorCompanyCode);
        return new EInvoiceProfileDto
        {
            BranchId = p.BranchId,
            ProviderCode = p.ProviderCode,
            IsActive = p.IsActive,
            CompanyName = p.CompanyName,
            CompanyAddress = p.CompanyAddress,
            TaxNumber = p.TaxNumber,
            TaxOffice = p.TaxOffice,
            SenderLabel = p.SenderLabel,
            IntegratorUsername = p.IntegratorUsername,
            DefaultInvoicePrefix = p.DefaultInvoicePrefix,
            DefaultArchivePrefix = p.DefaultArchivePrefix,
            SpecialMatrahCraftedVatRatePercent = settings.SpecialMatrahCraftedVatRatePercent,
            SpecialMatrahZiynetVatRatePercent = settings.SpecialMatrahZiynetVatRatePercent,
            SalesInvoiceVatRatePercent = settings.SalesInvoiceVatRatePercent,
            AutoDraftEnabled = settings.AutoDraftEnabled,
            AutoDraftMatchMode = settings.AutoDraftMatchMode,
            AutoDraftAllowedPaymentMethods = settings.AutoDraftAllowedPaymentMethods,
            AutoDraftMinTotal = settings.AutoDraftMinTotal,
            AutoDraftMaxTotal = settings.AutoDraftMaxTotal,
            WorkmanshipRules = settings.WorkmanshipRules
                .Select(x => new WorkmanshipRuleDto
                {
                    ProductType = EInvoiceProfileSettingsCodec.NormalizeWorkmanshipProductType(x.ProductType),
                    Karat = EInvoiceProfileSettingsCodec.ToWorkmanshipSelectorDisplay(x.ProductType, x.Karat),
                    MinTotal = x.MinTotal,
                    MaxTotal = x.MaxTotal,
                    Percentage = x.Percentage
                })
                .ToList()
        };
    }

    private static string? ExtractUblXml(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (TryFindJsonString(doc.RootElement, "ublXml", out var xml) && !string.IsNullOrWhiteSpace(xml))
                return xml;
            if (TryFindJsonString(doc.RootElement, "ublBase64", out var base64) && !string.IsNullOrWhiteSpace(base64))
            {
                var bytes = Convert.FromBase64String(base64);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool TryFindJsonString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    return !string.IsNullOrWhiteSpace(value);
                }
                if (TryFindJsonString(p.Value, name, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (TryFindJsonString(child, name, out value))
                    return true;
        }
        return false;
    }

    private static object ValidateUblRequiredProperties(string ublXml)
    {
        var required = new[]
        {
            "ÜRÜN ADI",
            "BARKOD",
            "ÜRÜN KODU",
            "GRAM",
            "AYAR",
            "İŞÇİLİK",
            "MİKTAR",
            "BİRİM FİYAT",
            "KDV ORANI",
            "KDV TUTARI",
            "TOPLAM TUTAR",
            "DÖVİZ TİPİ",
            "HAS ALTIN KARŞILIĞI",
            "ÜRÜN KATEGORİSİ",
            "TAŞ BİLGİSİ",
            "SERİ NUMARASI"
        };

        var x = XDocument.Parse(ublXml);
        var lines = x.Descendants().Where(e => e.Name.LocalName == "InvoiceLine").ToList();
        var missingByLine = new List<object>();

        foreach (var line in lines)
        {
            var lineId = line.Descendants().FirstOrDefault(e => e.Name.LocalName == "ID")?.Value ?? "?";
            var names = line.Descendants()
                .Where(e => e.Name.LocalName == "AdditionalItemProperty")
                .Select(p => p.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim() ?? "")
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var missing = required.Where(r => !names.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                missingByLine.Add(new { lineId, missing });
        }

        return new
        {
            lineCount = lines.Count,
            requiredProperties = required,
            missingLineCount = missingByLine.Count,
            missingByLine
        };
    }

    private static string NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        var chars = value.Where(char.IsDigit).ToArray();
        return new string(chars);
    }

    private static DateTime ResolveIssueDateUtc(string? issueDateText, string? issueTimeText)
    {
        var dateFormats = new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        if (!DateTime.TryParseExact(issueDateText ?? string.Empty, dateFormats, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var datePart) &&
            !DateTime.TryParse(issueDateText ?? string.Empty, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out datePart))
        {
            return DateTime.UtcNow;
        }

        if (!TimeSpan.TryParseExact(issueTimeText ?? string.Empty, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var timePart) &&
            !TimeSpan.TryParseExact(issueTimeText ?? string.Empty, @"hh\:mm", CultureInfo.InvariantCulture, out timePart))
        {
            timePart = TimeSpan.Zero;
        }

        var localDateTime = DateTime.SpecifyKind(datePart.Date.Add(timePart), DateTimeKind.Local);
        return localDateTime.ToUniversalTime();
    }

    private static ItemKind ResolveManualLineItemKind(string? karat, string? category, decimal kdvRatePercent)
    {
        var text = $"{karat} {category}".ToUpperInvariant();
        if (text.Contains("GUMUS") || text.Contains("GÜMÜŞ"))
            return ItemKind.Silver;
        if (Math.Abs(kdvRatePercent) < 0.001m)
            return ItemKind.CraftedGold;
        if (text.Contains("ZIYNET") || text.Contains("ZİYNET"))
            return ItemKind.Ziynet;
        return ItemKind.Product;
    }

    private bool CanUseEInvoice()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_einvoice");
    }

    private bool CanUseEArchive()
    {
        var role = User.FindFirstValue(ClaimTypes.Role) ?? "";
        if (string.Equals(role, "Owner", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasPermissionClaim("perm_earchive");
    }

    private bool HasPermissionClaim(string claimType)
    {
        var raw = User.FindFirstValue(claimType);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase);
    }
}
