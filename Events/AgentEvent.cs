namespace Fetch.Events;

public enum AgentEventType
{
    UserInput, LlmResponse, Reasoning, ToolCall, ToolResult, Error, Final
}

public sealed record AgentEvent(Guid Id, DateTimeOffset Timestamp, AgentEventType Type, string Title, string Body, string? Tool = null, string? Input = null);
