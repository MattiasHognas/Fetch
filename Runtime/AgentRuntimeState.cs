namespace Fetch.Runtime;

public sealed record ApprovalRequest(string Tool, string Input, string? Preview);

public sealed class AgentRuntimeState
{
    public Func<ApprovalRequest, Task<bool>>? ApprovalPromptAsync
    {
        get; set;
    }
    public ToolExecution? LastTool
    {
        get; set;
    }
    public ToolExecution? LastError
    {
        get; set;
    }
    public ToolExecution? LastCommand
    {
        get; set;
    }
}

public sealed record ToolExecution(string Tool, string Input, string Result, bool IsError);
