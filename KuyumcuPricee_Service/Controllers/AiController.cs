using KUYUMCU.Price_Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KUYUMCU.Price_Service.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public sealed class AiController : ControllerBase
{
    private readonly IAiService _aiService;
    private readonly IBranchLogoService _branchLogoService;

    public AiController(IAiService aiService, IBranchLogoService branchLogoService)
    {
        _aiService = aiService;
        _branchLogoService = branchLogoService;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] AiChatRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message alani zorunludur." });

        AiReplyResult reply;
        try
        {
            reply = await _aiService.GetReplyAsync(
                request.Message.Trim(),
                request.TenantId,
                request.BranchId,
                request.CurrentScreen,
                ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }

        return Ok(new AiChatResponse { Reply = reply.Reply, Action = reply.Action });
    }

    [HttpPost("generate-branch-logo")]
    public async Task<IActionResult> GenerateBranchLogo([FromBody] GenerateBranchLogoRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BranchName))
            return BadRequest(new { error = "branchName alanı zorunludur." });

        var result = await _branchLogoService.GenerateAsync(request.BranchName.Trim(), ct);
        if (!string.IsNullOrWhiteSpace(result.Error))
            return BadRequest(new { error = result.Error });

        return Ok(new GenerateBranchLogoResponse
        {
            LogoBase64 = result.LogoBase64,
            ContentType = result.ContentType
        });
    }
}

public sealed class AiChatRequest
{
    public string Message { get; set; } = "";
    public Guid? TenantId { get; set; }
    public Guid? BranchId { get; set; }
    public string? CurrentScreen { get; set; }
}

public sealed class AiChatResponse
{
    public string Reply { get; set; } = "";
    public KUYUMCU.Price_Service.Services.AiActionResponse? Action { get; set; }
}

public sealed class GenerateBranchLogoRequest
{
    public string BranchName { get; set; } = "";
}

public sealed class GenerateBranchLogoResponse
{
    public string LogoBase64 { get; set; } = "";
    public string ContentType { get; set; } = "image/png";
}
