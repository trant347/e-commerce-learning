using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Contracts;
using Microsoft.AspNetCore.Mvc;
using ai_assistant_service.Auth;

namespace ai_assistant_service.Controllers;

[ApiController]
[Route("api/ai-assistant")]
public sealed class AiAssistantController : ControllerBase
{
    private readonly IAiAssistantService _assistantService;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public AiAssistantController(
        IAiAssistantService assistantService,
        ICurrentUserAccessor currentUserAccessor)
    {
        _assistantService = assistantService;
        _currentUserAccessor = currentUserAccessor;
    }

    [HttpPost("chat")]
    [CustomAuthorize]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Message is required." });
        }

        var currentUser = _currentUserAccessor.GetRequiredUser();
        var response = await _assistantService.ChatAsync(request, currentUser, cancellationToken);
        return Ok(response);
    }
}
