using System.ComponentModel.DataAnnotations;
using KUYUMCU.Price_Service.Services;
using kuyumcu_infrastructure.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/bank-sync")]
public sealed class BankSyncController : ControllerBase
{
    private readonly IBankSyncService _bankSync;
    private readonly IBankSyncProfileService _profile;
    private readonly ITenantContext _tenant;

    public BankSyncController(IBankSyncService bankSync, IBankSyncProfileService profile, ITenantContext tenant)
    {
        _bankSync = bankSync;
        _profile = profile;
        _tenant = tenant;
    }

    [HttpPost("vomsis/sync-now")]
    [Authorize]
    public async Task<IActionResult> SyncFromVomsis([FromQuery] Guid? branchId, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        try
        {
            var result = await _bankSync.PullFromVomsisAsync(tid, bid, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("vomsis/import")]
    [AllowAnonymous]
    public async Task<IActionResult> ImportVomsis([FromBody] VomsisImportReq req, CancellationToken ct)
    {
        if (req is null || req.Transactions is null || req.Transactions.Count == 0)
            return BadRequest(new { error = "Transactions listesi boş olamaz." });

        var tid = _tenant.TenantId;
        var bid = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (tid == Guid.Empty || bid == Guid.Empty)
            return BadRequest(new { error = "TenantId ve BranchId zorunludur (header veya body)." });

        var result = await _bankSync.ImportVomsisTransactionsAsync(tid, bid, req.Transactions, ct);
        return Ok(result);
    }

    /// <summary>VM worker: manuel sync talebini tamamlandı olarak işaretler.</summary>
    [HttpPost("vomsis/sync-complete")]
    [AllowAnonymous]
    public async Task<IActionResult> CompleteManualSync([FromBody] VomsisSyncCompleteReq req, CancellationToken ct)
    {
        if (req is null || req.BranchId == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var tid = _tenant.TenantId;
        var bid = req.BranchId;
        if (tid == Guid.Empty || bid == Guid.Empty)
            return BadRequest(new { error = "TenantId ve BranchId zorunludur." });

        await _profile.CompleteManualSyncAsync(tid, bid, new BankSyncPullResult
        {
            FetchedFromVomsis = req.FetchedFromVomsis,
            Imported = req.Imported,
            SummaryMessage = req.SummaryMessage
        }, ct);
        return Ok(new { success = true });
    }

    [HttpGet("transactions")]
    [Authorize]
    public async Task<IActionResult> List(
        [FromQuery] Guid? branchId,
        [FromQuery] string? status,
        [FromQuery, Range(1, 1000)] int page = 1,
        [FromQuery, Range(1, 200)] int pageSize = 50,
        CancellationToken ct = default)
    {
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var result = await _bankSync.ListAsync(tid, bid, status, page, pageSize, ct);
        return Ok(result);
    }

    [HttpPost("transactions/{id:guid}/match")]
    [Authorize]
    public async Task<IActionResult> MatchAndDraft(Guid id, [FromBody] MatchBankImportReq req, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = req?.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var options = new CreateBankImportDraftOptions
        {
            CustomerId = req?.CustomerId is Guid cid && cid != Guid.Empty ? cid : null,
            SupplierId = req?.SupplierId is Guid sid && sid != Guid.Empty ? sid : null,
            ManualTaxNo = req?.ManualTaxNo,
            ManualBuyerName = req?.ManualBuyerName,
            UseNihaiTuketici = req?.UseNihaiTuketici == true
        };

        var result = await _bankSync.CreateDraftAsync(tid, bid, id, options, ct);
        if (!result.Success)
            return BadRequest(new { error = result.Message, status = result.Status });
        return Ok(result);
    }

    [HttpPost("transactions/{id:guid}/create-draft")]
    [Authorize]
    public async Task<IActionResult> CreateDraft(Guid id, [FromBody] CreateBankImportDraftReq? req, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = req?.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var options = new CreateBankImportDraftOptions
        {
            CustomerId = req?.CustomerId,
            SupplierId = req?.SupplierId,
            ManualTaxNo = req?.ManualTaxNo,
            ManualBuyerName = req?.ManualBuyerName,
            UseNihaiTuketici = req?.UseNihaiTuketici == true
        };

        var result = await _bankSync.CreateDraftAsync(tid, bid, id, options, ct);
        if (!result.Success)
            return BadRequest(new { error = result.Message, status = result.Status });
        return Ok(result);
    }

    [HttpPost("transactions/{id:guid}/refresh-vomsis-tax")]
    [Authorize]
    public async Task<IActionResult> RefreshVomsisTax(Guid id, [FromBody] RefreshVomsisTaxReq? req, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = req?.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var result = await _bankSync.RefreshVomsisTaxAsync(tid, bid, id, ct);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("transactions/{id:guid}/reject")]
    [Authorize]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectBankImportReq? req, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = req?.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var result = await _bankSync.RejectAsync(tid, bid, id, req?.Reason, ct);
        if (!result.Success)
            return BadRequest(new { error = result.Message });
        return Ok(result);
    }

    [HttpGet("profile")]
    [Authorize]
    public async Task<IActionResult> GetProfile([FromQuery] Guid? branchId, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var dto = await _profile.GetProfileAsync(tid, bid, ct);
        return Ok(dto);
    }

    [HttpPut("auto-instruction")]
    [Authorize]
    public async Task<IActionResult> SaveAutoInstruction([FromBody] SaveBankAutoInstructionReq req, CancellationToken ct)
    {
        if (req is null) return BadRequest(new { error = "İstek boş olamaz." });
        var tid = _tenant.TenantId;
        var bid = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && bid != _tenant.BranchId.Value)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });

        req.BranchId = bid;
        try
        {
            var dto = await _profile.SaveAutoInstructionAsync(tid, req, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("profile")]
    [Authorize]
    public async Task<IActionResult> SaveProfile([FromBody] SaveBankSyncProfileReq req, CancellationToken ct)
    {
        if (req is null) return BadRequest(new { error = "İstek boş olamaz." });
        var tid = _tenant.TenantId;
        var bid = req.BranchId != Guid.Empty ? req.BranchId : (_tenant.BranchId ?? Guid.Empty);
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });
        if (_tenant.BranchId.HasValue && _tenant.BranchId.Value != Guid.Empty && bid != _tenant.BranchId.Value)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });

        req.BranchId = bid;
        try
        {
            var dto = await _profile.SaveProfileAsync(tid, req, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>VM worker: şube sync ayarlarını çeker (x-app-key + X-Tenant-Id + X-Branch-Id).</summary>
    [HttpGet("profile/worker")]
    [AllowAnonymous]
    public async Task<IActionResult> GetWorkerProfile([FromQuery] Guid? branchId, CancellationToken ct)
    {
        var tid = _tenant.TenantId;
        var bid = branchId ?? _tenant.BranchId ?? Guid.Empty;
        if (tid == Guid.Empty || bid == Guid.Empty)
            return BadRequest(new { error = "TenantId ve BranchId zorunludur." });

        var cfg = await _profile.GetWorkerConfigAsync(tid, bid, ct);
        if (cfg is null)
            return NotFound(new { error = "Banka sync profili bulunamadı veya devre dışı." });
        return Ok(cfg);
    }

    public sealed class VomsisImportReq
    {
        public Guid BranchId { get; set; }
        public List<VomsisTransactionImportDto> Transactions { get; set; } = new();
    }

    public sealed class VomsisSyncCompleteReq
    {
        public Guid BranchId { get; set; }
        public int FetchedFromVomsis { get; set; }
        public int Imported { get; set; }
        public string? SummaryMessage { get; set; }
    }

    public sealed class MatchBankImportReq
    {
        public Guid? BranchId { get; set; }
        public Guid? CustomerId { get; set; }
        public Guid? SupplierId { get; set; }
        public string? ManualTaxNo { get; set; }
        public string? ManualBuyerName { get; set; }
        public bool UseNihaiTuketici { get; set; }
    }

    public sealed class CreateBankImportDraftReq
    {
        public Guid? BranchId { get; set; }
        public Guid? CustomerId { get; set; }
        public Guid? SupplierId { get; set; }
        public string? ManualTaxNo { get; set; }
        public string? ManualBuyerName { get; set; }
        public bool UseNihaiTuketici { get; set; }
    }

    public sealed class RejectBankImportReq
    {
        public Guid? BranchId { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class RefreshVomsisTaxReq
    {
        public Guid? BranchId { get; set; }
    }
}
