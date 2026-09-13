using ai_assistant_service.Contracts;
using ai_assistant_service.Services.Tools;

namespace ai_assistant_service.Services.Contracts;

public interface IAiAssistantService
{
    Task<ChatResponse> ChatAsync(
        ChatRequest request,
        ToolExecutionContext executionContext,
        CancellationToken cancellationToken);
}
