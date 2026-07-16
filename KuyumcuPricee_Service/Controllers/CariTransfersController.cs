using System.Security.Claims;
using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class CariTransfersController : ControllerBase
{
    private readonly CariTransferService _transfer;

    public CariTransfersController(CariTransferService transfer) => _transfer = transfer;

    [HttpPost("process")]
    public async Task<IActionResult> Process([FromBody] CariTransferProcessReq req, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        var branchId = GetBranchId();
        if (req.BranchId.HasValue && req.BranchId.Value != Guid.Empty && req.BranchId.Value != branchId)
            return BadRequest(new { error = "İşlem şubesi, oturum şubesi ile aynı olmalıdır." });

        var (userId, userName) = ResolveUser();
        var result = await _transfer.ProcessAsync(req, tenantId, branchId, userId, userName, ct);
        if (!result.Ok)
            return BadRequest(new { error = result.Error });
        return Ok(new { ok = true, transferId = result.TransferId, sourceBatchId = result.SourceBatchId, targetBatchId = result.TargetBatchId });
    }

    private Guid GetTenantId()
    {
        var claim = User.FindFirstValue("tenant_id") ?? User.FindFirstValue("TenantId");
        if (!Guid.TryParse(claim, out var id)) throw new UnauthorizedAccessException("TenantId missing.");
        return id;
    }

    private Guid GetBranchId()
    {
        var claim = User.FindFirstValue("branch_id") ?? User.FindFirstValue("BranchId");
        if (!Guid.TryParse(claim, out var id)) throw new UnauthorizedAccessException("BranchId missing.");
        return id;
    }

    private (Guid? UserId, string? UserName) ResolveUser()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        Guid? userId = Guid.TryParse(idClaim, out var uid) ? uid : null;
        var name = User.FindFirstValue("name") ?? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name;
        return (userId, name);
    }
}
