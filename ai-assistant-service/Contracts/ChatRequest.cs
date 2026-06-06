namespace ai_assistant_service.Contracts;

public sealed class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public List<ChatHistoryMessage>? History { get; set; }
}

public sealed class ChatHistoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
