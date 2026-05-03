using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace ai_assistant_service.Controllers;

[ApiController]
[Route("api/ai-assistant")]
public sealed class AiAssistantController : ControllerBase
{
    private readonly IAiAssistantService _assistantService;

    public AiAssistantController(IAiAssistantService assistantService)
    {
        _assistantService = assistantService;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "Message is required." });
        }

        var response = await _assistantService.ChatAsync(request, cancellationToken);
        return Ok(response);
    }
}
