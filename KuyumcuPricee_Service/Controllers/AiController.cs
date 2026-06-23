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

    public AiController(IAiService aiService)
    {
        _aiService = aiService;
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
