namespace ai_assistant_service.Contracts;

public sealed class ChatResponse
{
    public string Answer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public List<string> Sources { get; set; } = new();
    /// <summary>Task masters mentioned in the answer, extracted from tool results.</summary>
    public List<TaskMasterMention> Mentions { get; set; } = new();
}

public sealed class TaskMasterMention
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
