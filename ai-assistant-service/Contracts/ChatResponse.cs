namespace ai_assistant_service.Contracts;

public sealed class ChatResponse
{
    public string Answer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = new();
}
