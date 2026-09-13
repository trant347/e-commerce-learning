using ai_assistant_service.Auth;
using ai_assistant_service.Contracts;

namespace ai_assistant_service.Services.Contracts;

public interface IAiAssistantService
{
    Task<ChatResponse> ChatAsync(
        ChatRequest request,
        CurrentUser currentUser,
        CancellationToken cancellationToken);
}
