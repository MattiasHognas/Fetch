using System.Text.Json;

namespace Fetch.Core;

public enum ApprovalMode
{
    Auto, Ask, Deny
}

public interface ITool
{
    string Name
    {
        get;
    }
    string Description
    {
        get;
    }
    ApprovalMode Approval
    {
        get;
    }
    Task<string> RunAsync(string input);
}

public interface IPreviewableTool
{
    Task<string> PreviewAsync(string input);
}

public interface INativeTool
{
    object GetParametersSchema();
    string ConvertArguments(JsonElement arguments);
}

public sealed record ToolFunctionDefinition(string Name, string Description, object Parameters);
public sealed record NativeToolDefinition(string Type, ToolFunctionDefinition Function);
public sealed record LlmToolCall(string Name, string ArgumentsJson);
public sealed record LlmChatMessage(string Role, string? Content = null, string? Thinking = null, string? ToolName = null, IReadOnlyList<LlmToolCall>? ToolCalls = null);
public sealed record LlmPromptResponse(string Content, string? Reasoning = null);
public sealed record LlmChatResponse(string Content, string? Reasoning, IReadOnlyList<LlmToolCall> ToolCalls);
