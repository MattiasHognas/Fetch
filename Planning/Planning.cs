using System.Text.Json;

namespace Fetch.Planning;

public sealed class TaskPlanner(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public Task<string> CreatePlanAsync(string task, string? agentMd)
    {
        var p = _prompts.Render(PromptId.PlannerCreate, new()
        {
            ["task"] = task,
            ["agent_md"] = agentMd ?? ""
        });
        return _llm.ChatAsync(p);
    }
}

public static class PlannerTodoSeeder
{
    public static async Task SeedAsync(string planJson, TodoStore store)
    {
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            if (!doc.RootElement.TryGetProperty("steps", out JsonElement steps))
            {
                return;
            }

            var todos = new List<TodoItem>();
            var id = 1;
            foreach (JsonElement s in steps.EnumerateArray())
            {
                var text = s.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                todos.Add(new TodoItem(id++, text, id == 2 ? "in_progress" : "pending"));
            }
            if (todos.Count > 0)
            {
                await store.WriteAsync(todos);
            }
        }
        catch { }
    }
}

public sealed class ToolRouter(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public Task<string> ChooseAsync(string task, string transcript, IEnumerable<ITool> tools)
    {
        var p = _prompts.Render(PromptId.ToolRouter, new()
        {
            ["tools"] = string.Join("\n", tools.Select(t => $"- {t.Name}: {t.Description}")),
            ["task"] = task,
            ["recent_state"] = Trim(transcript)
        });
        return _llm.ChatAsync(p);
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
