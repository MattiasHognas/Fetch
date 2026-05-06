using System.Text.Json;

namespace Fetch.Core;

public sealed class AgentLoop
{
    private readonly LlmClient _llm; private readonly Dictionary<string, ITool> _tools; private readonly SessionLogger _logger; private readonly TodoStore _todoStore; private readonly AgentConfig _config; private readonly PromptCatalog _prompts; private readonly AgentSession _session; private readonly AgentRuntimeState _state; private readonly AgentEventStore _events; private readonly TaskPlanner _planner; private readonly ToolRouter _router; private readonly CommandResultAnalyzer _analyzer; private readonly TranscriptCompactor _compactor; private readonly ApprovalPolicy _approvalPolicy; private readonly string? _agentMd;
    public AgentLoop(LlmClient llm, IEnumerable<ITool> tools, SessionLogger logger, TodoStore todoStore, AgentConfig config, PromptCatalog prompts, AgentSession session, AgentRuntimeState state, AgentEventStore events)
    {
        _llm = llm;
        _tools = tools.ToDictionary(t => t.Name);
        _logger = logger;
        _todoStore = todoStore;
        _config = config;
        _prompts = prompts;
        _session = session;
        _state = state;
        _events = events;
        _planner = new TaskPlanner(llm, prompts);
        _router = new ToolRouter(llm, prompts);
        _analyzer = new CommandResultAnalyzer(llm, prompts);
        _compactor = new TranscriptCompactor(llm, prompts);
        _approvalPolicy = new ApprovalPolicy(config);
        _agentMd = LoadAgentContext();
    }

    public async Task RunAsync(string task)
    {
        _events.Add(AgentEventType.UserInput, "User", task);
        await _logger.LogAsync("user_task", new
        {
            task
        });
        var plan = await _planner.CreatePlanAsync(task, _agentMd);
        await _logger.LogAsync("plan", new
        {
            task,
            plan
        });
        await PlannerTodoSeeder.SeedAsync(plan, _todoStore);
        var transcript = BuildInitialPrompt(task, plan);
        var failedRuns = 0;
        for (var step = 0; step < _config.MaxAgentSteps; step++)
        {
            transcript = await CompactTranscriptIfNeededAsync(task, transcript);
            var route = await _router.ChooseAsync(task, transcript, _tools.Values);
            await _logger.LogAsync("tool_route", new
            {
                step,
                route
            });
            transcript += $"\n\nTool routing hint:\n{route}\n";
            transcript = await CompactTranscriptIfNeededAsync(task, transcript);
            var response = await _llm.ChatAsync(transcript);
            _events.Add(AgentEventType.LlmResponse, "LLM response", response);
            await _logger.LogAsync("llm_response", new
            {
                step,
                response
            });
            if (!JsonHelper.TryParseObject(response, out JsonDocument? doc, out var cleaned) || doc is null)
            {
                transcript += "\nReturn ONLY one valid JSON object.";
                await _logger.LogAsync("error", new
                {
                    step,
                    kind = "invalid_json",
                    response
                });
                continue;
            }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("final", out JsonElement final))
                {
                    var text = final.GetString() ?? "";
                    _events.Add(AgentEventType.Final, "Final", text);
                    await _logger.LogAsync("final", new
                    {
                        text
                    });
                    Console.WriteLine(text);
                    return;
                }
                if (!root.TryGetProperty("tool", out JsonElement t) || !root.TryGetProperty("input", out JsonElement inp))
                {
                    transcript += "\nReturn either {\"tool\":\"name\",\"input\":\"value\"} or {\"final\":\"answer\"}.";
                    continue;
                }
                var toolName = t.GetString() ?? "";
                var input = inp.GetString() ?? "";
                if (!_tools.TryGetValue(toolName, out ITool? tool))
                {
                    transcript += $"\nUnknown tool: {toolName}\nAvailable tools:\n{RenderTools()}";
                    await _logger.LogAsync("error", new
                    {
                        step,
                        kind = "unknown_tool",
                        tool = toolName
                    });
                    continue;
                }
                _events.Add(AgentEventType.ToolCall, $"Tool call: {toolName}", input, toolName, input);
                await _logger.LogAsync("tool_call", new
                {
                    step,
                    tool = toolName,
                    input
                });
                var result = await ExecuteToolAsync(tool, input);
                _events.Add(AgentEventType.ToolResult, $"Tool result: {toolName}", result, toolName, input);
                await _logger.LogAsync("tool_result", new
                {
                    step,
                    tool = toolName,
                    result = TrimToolResult(result)
                });
                string? analysis = null;
                if (toolName == "run_command")
                {
                    analysis = await _analyzer.AnalyzeAsync(result);
                    await _logger.LogAsync("command_analysis", new
                    {
                        step,
                        analysis
                    });
                    if (!result.Contains("\"ExitCode\": 0"))
                    {
                        failedRuns++;
                        if (failedRuns >= _config.MaxFailedCommandAttempts)
                        {
                            transcript += "\nMaximum failed command attempts reached. Explain what is blocking progress and return final.";
                        }
                    }
                    else
                    {
                        failedRuns = 0;
                    }
                }
                transcript += $"\n\nAssistant tool call:\n{cleaned}\n\nTool result:\n{TrimToolResult(result)}\n" + (analysis is null ? "" : $"\nCommand analysis:\n{analysis}\n");
            }
        }
        Console.WriteLine("Stopped after max steps.");
    }

    private async Task<string> CompactTranscriptIfNeededAsync(string task, string transcript)
    {
        var maxPromptTokens = Math.Max(1, _config.ContextWindowTokens - _config.ContextWindowReserveTokens);
        var promptTokens = await _llm.CountPromptTokensAsync(transcript);
        if (promptTokens <= maxPromptTokens)
        {
            return transcript;
        }

        var summary = await _compactor.CompactAsync(transcript);
        await File.WriteAllTextAsync(_session.SummaryPath, summary);
        await _logger.LogAsync("compaction", new
        {
            promptTokens,
            maxPromptTokens,
            contextWindowTokens = _config.ContextWindowTokens,
            contextWindowReserveTokens = _config.ContextWindowReserveTokens,
            summary
        });
        return BuildCompactedPrompt(task, summary);
    }

    private async Task<string> ExecuteToolAsync(ITool tool, string input)
    {
        ApprovalDecision decision = _approvalPolicy.Decide(tool);
        if (decision == ApprovalDecision.Deny)
        {
            return Record(tool, input, $"Denied by approval policy: {tool.Name}", true);
        }

        if (decision == ApprovalDecision.DryRun)
        {
            return Record(tool, input, $"Dry-run: tool not executed.\n\nTool: {tool.Name}\nInput:\n{input}", false);
        }

        if (decision == ApprovalDecision.Ask)
        {
            if (tool is IPreviewableTool p)
            {
                Console.WriteLine("\nPreview\n-------");
                Console.WriteLine(await p.PreviewAsync(input));
            }
            Console.WriteLine($"Approve tool {tool.Name}?\nInput:\n{input}\n[y/N] ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
            {
                return Record(tool, input, "User denied action.", true);
            }
        }
        try
        {
            Console.WriteLine($"Running: {tool.Name}");
            var result = await tool.RunAsync(input);
            var isError = IsErrorResult(result);
            return Record(tool, input, result, isError);
        }
        catch (Exception ex) { return Record(tool, input, $"Tool failed: {ex.Message}", true); }
    }
    private string Record(ITool tool, string input, string result, bool isError)
    {
        var exec = new ToolExecution(tool.Name, input, result, isError);
        _state.LastTool = exec;
        if (tool.Name == "run_command")
        {
            _state.LastCommand = exec;
        }

        if (isError)
        {
            _state.LastError = exec;
        }

        return result;
    }
    private static bool IsErrorResult(string r) => r.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) || r.Contains("failed", StringComparison.OrdinalIgnoreCase) || r.Contains("\"ExitCode\": 1") || r.Contains("\"ExitCode\": -1") || r.Contains("Denied by approval policy");
    private string BuildInitialPrompt(string task, string plan) => _prompts.Render(PromptId.AgentInitial, new() { ["agent_md"] = _agentMd ?? "", ["previous_summary"] = _session.ReadSummary(), ["recent_log"] = _session.RecentLogTail(), ["plan"] = plan, ["tools"] = RenderTools(), ["task"] = task });
    private string BuildCompactedPrompt(string task, string summary) => _prompts.Render(PromptId.AgentCompacted, new() { ["agent_md"] = _agentMd ?? "", ["task"] = task, ["summary"] = summary, ["tools"] = RenderTools() });
    private string RenderTools() => string.Join("\n", _tools.Values.Select(t => $"- {t.Name}: {t.Description}"));
    private string TrimToolResult(string text) => text.Length <= _config.MaxToolResultChars ? text : text[.._config.MaxToolResultChars] + "\n[tool result truncated]";
    private string? LoadAgentContext()
    {
        var chunks = new List<string>();
        foreach (var file in _config.AgentInstructionFiles)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Length > 6000)
            {
                text = text[..6000];
            }

            chunks.Add($"# {file}\n{text}");
        }
        return chunks.Count == 0 ? null : string.Join("\n\n", chunks);
    }
}
