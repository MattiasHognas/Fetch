using System.Text.Json;

namespace Fetch.Planning;

public sealed record PlanResult(TaskKind Kind, Playbook Playbook, string LlmPlanJson);

public sealed class TaskPlanner(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public async Task<PlanResult> CreatePlanAsync(string task, string? agentMd)
    {
        TaskKind kind = TaskClassifier.Classify(task);
        Playbook playbook = TaskClassifier.GetPlaybook(kind);
        var stepsText = string.Join("\n", playbook.Steps.Select((s, i) => $"{i + 1}. {s}"));
        var p = _prompts.Render(PromptId.PlannerCreate, new()
        {
            ["task"] = task,
            ["agent_md"] = agentMd ?? "",
            ["task_kind"] = kind.ToString(),
            ["playbook_hint"] = playbook.Hint,
            ["playbook_steps"] = stepsText,
            ["required_first_tool"] = playbook.RequiredFirstTool ?? "(none)"
        });
        var llmPlan = await _llm.ChatAsync(p);
        return new PlanResult(kind, playbook, llmPlan);
    }
}

public static class PlannerTodoSeeder
{
    /// <summary>
    /// Seeds todos directly from the deterministic playbook; the LLM-generated plan is recorded for context but
    /// the actual ordered steps come from the playbook so weak local models can't derail the loop with bad JSON.
    /// </summary>
    public static async Task SeedAsync(PlanResult plan, TodoStore store)
    {
        var todos = new List<TodoItem>();
        var id = 1;
        foreach (var step in plan.Playbook.Steps)
        {
            todos.Add(new TodoItem(id, step, id == 1 ? "in_progress" : "pending"));
            id++;
        }
        if (todos.Count > 0)
        {
            await store.WriteAsync(todos);
        }
        await Task.CompletedTask;
    }
}

public sealed class ToolRouter(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public Task<string> ChooseAsync(string task, string transcript, IEnumerable<ITool> tools, bool semanticSearchReady, PlanResult plan, string currentTodo, string completedTodos)
    {
        var availableTools = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (TryGetPinnedTodoTool(currentTodo, out var pinnedTool) && availableTools.Contains(pinnedTool))
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                tool = pinnedTool,
                reason = "The current todo explicitly targets this tool, so routing should stay on the active playbook step instead of revisiting completed steps.",
                inputHint = BuildPinnedToolInputHint(pinnedTool)
            }));
        }

        var p = _prompts.Render(PromptId.ToolRouter, new()
        {
            ["tools"] = string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}")),
            ["task"] = task,
            ["recent_state"] = Trim(transcript),
            ["semantic_search_status"] = semanticSearchReady ? "ready" : "missing",
            ["task_kind"] = plan.Kind.ToString(),
            ["playbook_hint"] = plan.Playbook.Hint,
            ["required_first_tool"] = plan.Playbook.RequiredFirstTool ?? "(none)",
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

    private static string BuildPinnedToolInputHint(string toolName)
    {
        return toolName switch
        {
            "apply_diff" => "Provide a real *** Begin Patch payload for the target file. Do not return prose or a router suggestion.",
            "read_file" => "Read the concrete file produced or needed by the current step.",
            "relationship_map" => "Provide JSON like {\"files\":[\"Program.cs\",\"Core/AgentLoop.cs\",\"Planning/Planning.cs\"]} using files already discovered from code_map/read_ranges.",
            "run_command" => "Run the narrowest relevant build, test, or verification command for the changed slice.",
            "references_search" => "Provide a concrete symbol and file context from the files already read.",
            _ => "Provide the concrete input required by the current todo step."
        };
    }

    private static string Trim(string t) => t.Length <= 12000 ? t : t[^12000..];
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
