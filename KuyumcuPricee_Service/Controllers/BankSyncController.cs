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

    /// <summary>VM worker: Vomsis hareketlerini ERP'ye aktarır (x-app-key + X-Tenant-Id + X-Branch-Id).</summary>
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
        if (req?.CustomerId is null || req.CustomerId == Guid.Empty)
            return BadRequest(new { error = "CustomerId zorunludur." });

        var tid = _tenant.TenantId;
        var bid = req.BranchId ?? _tenant.BranchId ?? Guid.Empty;
        if (bid == Guid.Empty)
            return BadRequest(new { error = "BranchId zorunludur." });

        var result = await _bankSync.MatchAndCreateDraftAsync(tid, bid, id, req.CustomerId.Value, ct);
        if (!result.Success)
            return BadRequest(new { error = result.Message, status = result.Status });
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

    public sealed class MatchBankImportReq
    {
        public Guid? BranchId { get; set; }
        public Guid? CustomerId { get; set; }
    }

    public sealed class RejectBankImportReq
    {
        public Guid? BranchId { get; set; }
        public string? Reason { get; set; }
    }
}
