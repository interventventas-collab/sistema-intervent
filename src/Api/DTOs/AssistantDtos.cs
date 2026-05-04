namespace Api.DTOs;

public class AssistantChatMessage
{
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public string Content { get; set; } = "";
}

public class AssistantChatRequest
{
    public List<AssistantChatMessage> Messages { get; set; } = new();
}

public class AssistantToolCallTrace
{
    public string Tool { get; set; } = "";
    public string Args { get; set; } = "";
    public string Result { get; set; } = "";
}

public class AssistantChatResponse
{
    public string Reply { get; set; } = "";
    public List<AssistantToolCallTrace> ToolCalls { get; set; } = new();
    public bool Configured { get; set; } = true;
    public string? Error { get; set; }
}
