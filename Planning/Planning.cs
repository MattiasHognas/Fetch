using System.Text.Json;

namespace Fetch.Planning;

public sealed class ToolRouter(LlmClient llm, PromptCatalog prompts, AgentConfig config)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts; private readonly AgentConfig _config = config;

    public Task<string> ChooseAsync(string task, string transcript, IEnumerable<ITool> tools, bool semanticSearchReady, AgentPhase phase, string currentTodo, string completedTodos)
    {
        var availableTools = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (TryGetPinnedTodoTool(currentTodo, out var pinnedTool) && availableTools.Contains(pinnedTool))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                tool = pinnedTool,
                reason = "The current todo explicitly targets this tool.",
                inputHint = BuildPinnedToolInputHint(pinnedTool, phase)
            }));
        }

        var p = _prompts.Render(PromptId.ToolRouter, new()
        {
            ["tools"] = string.Join("\n", tools.Select(t => $"- {t.Name}")),
            ["task"] = task,
            ["recent_state"] = Trim(transcript),
            ["semantic_search_status"] = semanticSearchReady ? "ready" : "missing",
            ["task_kind"] = phase.ToString(),
            ["playbook_hint"] = PhaseToolPolicy.PhaseHint(phase),
            ["required_first_tool"] = "(none)",
            ["current_todo"] = currentTodo,
            ["completed_todos"] = completedTodos
        });
        return _llm.ChatAsync(p);
    }

    private static bool TryGetPinnedTodoTool(string currentTodo, out string toolName)
    {
        toolName = "";
        if (string.IsNullOrWhiteSpace(currentTodo))
        {
            return false;
        }

        var open = currentTodo.LastIndexOf('(');
        var close = currentTodo.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        var marker = currentTodo[(open + 1)..close].Trim();
        if (string.IsNullOrWhiteSpace(marker)
            || marker.Contains(" or ", StringComparison.OrdinalIgnoreCase)
            || marker.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        toolName = marker;
        return true;
    }

    private static string BuildPinnedToolInputHint(string toolName, AgentPhase phase)
    {
        return toolName switch
        {
            "apply_diff" when phase == AgentPhase.Editing
                => "apply_diff input MUST be a raw V4A patch string (no JSON, no quotes, no fences). Example: '*** Begin Patch\\n*** Update File: path.cs\\n@@\\n-old\\n+new\\n*** End Patch'. Each '-' line must match the file byte-for-byte; include 1-3 unchanged context lines around each hunk.",
            "read_file" => "Read the concrete file produced or needed by the current step.",
            "relationship_map" => "Provide JSON like {\"files\":[\"Program.cs\",\"Core/AgentLoop.cs\"]} using files already discovered from code_map/read_ranges.",
            "run_command" => "Run the narrowest relevant build, test, or verification command for the changed slice.",
            "references_search" => "Provide a concrete symbol and file context from the files already read.",
            _ => "Provide the concrete input required by the current todo step."
        };
    }

    private string Trim(string t) => t.Length <= _config.MaxRoutingTranscriptChars ? t : t[^_config.MaxRoutingTranscriptChars..];
}

public sealed class CommandResultAnalyzer(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public Task<string> AnalyzeAsync(string result)
    {
        var p = _prompts.Render(PromptId.CommandAnalyze, new()
        {
            ["command_result"] = result
        });
        return _llm.ChatAsync(p);
    }
}

public sealed class TranscriptCompactor(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public Task<string> CompactAsync(string transcript)
    {
        var p = _prompts.Render(PromptId.TranscriptCompact, new()
        {
            ["transcript"] = transcript
        });
        return _llm.ChatAsync(p);
    }
}
