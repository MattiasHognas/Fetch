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
        var hasGroundingEvidence = false;
        var repeatedBlockedToolCalls = 0;
        for (var step = 0; step < _config.MaxAgentSteps; step++)
        {
            transcript = await CompactTranscriptIfNeededAsync(task, transcript);
            var route = await _router.ChooseAsync(task, transcript, _tools.Values, _state.SemanticSearchReady);
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
                transcript += LooksLikeDraftContent(response)
                    ? "\nThat was prose or draft diagram content, not an executable step. Do not draft the architecture yet. Your next response must be exactly one JSON tool call, preferably refine_context, read_ranges, or another concrete read/search step. Do not repeat repo_tree with the same input."
                    : "\nReturn ONLY one valid JSON object with no prose, no fenced code block, and no tool-call markup.";
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
                    var text = ReadJsonValue(final);
                    if (ShouldRejectPrematureFinal(text, step))
                    {
                        transcript += "\nDo not return final for a plan, a next step, or partial progress. If work remains, return a tool call as {\"tool\":\"name\",\"input\":\"value\"}.";
                        await _logger.LogAsync("error", new
                        {
                            step,
                            kind = "premature_final",
                            text
                        });
                        continue;
                    }
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
                    transcript += root.TryGetProperty("tool", out _) && (root.TryGetProperty("reason", out _) || root.TryGetProperty("inputHint", out _))
                        ? "\nThat was a router-style suggestion, not an executable tool call. Execute exactly one tool now and include a real input value as {\"tool\":\"name\",\"input\":\"value\"}."
                        : "\nReturn either {\"tool\":\"name\",\"input\":\"value\"} or {\"final\":\"answer\"}.";
                    continue;
                }
                var toolName = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var input = ReadJsonValue(inp);
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
                var result = TryBlockRepeatedDiscoveryToolCall(toolName, input, out var repeatedDiscoveryFailure)
                    ? Record(tool, input, repeatedDiscoveryFailure, true)
                    : TryBlockRepeatedFailedToolCall(toolName, input, out var repeatedFailure)
                    ? Record(tool, input, repeatedFailure, true)
                    : TryBlockUngroundedWrite(tool, input, hasGroundingEvidence, out var groundingFailure)
                        ? Record(tool, input, groundingFailure, true)
                        : await ExecuteToolAsync(tool, input);
                _events.Add(AgentEventType.ToolResult, $"Tool result: {toolName}", result, toolName, input);
                await _logger.LogAsync("tool_result", new
                {
                    step,
                    tool = toolName,
                    result = TrimToolResult(result)
                });
                repeatedBlockedToolCalls = result.StartsWith("Repeated discovery tool call blocked.", StringComparison.OrdinalIgnoreCase)
                    || result.StartsWith("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase)
                    ? repeatedBlockedToolCalls + 1
                    : 0;
                if (repeatedBlockedToolCalls >= 3)
                {
                    var blocker = $"Blocked: the agent is repeatedly retrying {toolName} with the same input after explicit loop-prevention errors. It must switch to a different tool or query instead of continuing this run.";
                    _events.Add(AgentEventType.Final, "Final", blocker);
                    await _logger.LogAsync("final", new
                    {
                        text = blocker
                    });
                    Console.WriteLine(blocker);
                    return;
                }
                hasGroundingEvidence = hasGroundingEvidence || IsGroundingEvidence(toolName, result);
                var recoveryGuidance = BuildRecoveryGuidance(toolName, result);
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
                transcript += $"\n\nAssistant tool call:\n{cleaned}\n\nTool result:\n{TrimToolResult(result)}\n"
                    + (analysis is null ? "" : $"\nCommand analysis:\n{analysis}\n")
                    + (recoveryGuidance is null ? "" : $"\nRecovery guidance:\n{recoveryGuidance}\n");
            }
        }
        Console.WriteLine("Stopped after max steps.");
    }

    private static string? BuildRecoveryGuidance(string toolName, string result)
    {
        return toolName switch
        {
            "semantic_search" when !result.StartsWith("Semantic index missing.", StringComparison.OrdinalIgnoreCase)
                => "Use these semantic_search results to narrow to 3-8 concrete files with refine_context or read_ranges. Do not jump back to repo_tree with the same broad input.",
            "repo_tree" when !result.StartsWith("Repeated", StringComparison.OrdinalIgnoreCase)
                => "Repo tree only gives broad structure. Your next step should be a more specific search or read tool, or a different repo_tree depth if you truly need different structure evidence.",
            "semantic_search" when result.StartsWith("Semantic index missing.", StringComparison.OrdinalIgnoreCase)
                => "Semantic search is unavailable until the index is built. Do not call semantic_search again in this run. Use repo_tree, search_files, search_content, and refine_context to gather evidence instead.",
            "repo_tree" when result.StartsWith("Repeated discovery tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not call repo_tree again with the same input. Choose a different tool such as semantic_search, search_content, refine_context, or read_ranges.",
            "repo_tree" when result.StartsWith("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not call repo_tree again with the same input. Choose a different tool such as semantic_search, search_content, refine_context, or read_ranges.",
            _ when result.StartsWith("Repeated discovery tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not repeat the same discovery tool call with the same input. Refine the query, choose a more specific read/search tool, or move on to the next evidence-gathering step.",
            "read_ranges" when result.StartsWith("Invalid range JSON:", StringComparison.OrdinalIgnoreCase)
                => "Retry read_ranges with JSON shaped like [{\"file\":\"Program.cs\",\"start\":1,\"end\":50}] or {\"file\":\"Program.cs\",\"start\":1,\"end\":50}.",
            "apply_diff" when result.StartsWith("Patch validation failed:\nFile already exists:", StringComparison.OrdinalIgnoreCase)
                => "The target file already exists. Read its current contents, then retry apply_diff with *** Update File instead of *** Add File. Do not repeat the same add-file patch.",
            "apply_diff" when result.StartsWith("Patch failed.", StringComparison.OrdinalIgnoreCase)
                => "Do not return final yet. Read the relevant file or confirm it does not exist, then retry apply_diff with a full patch beginning with *** Begin Patch.",
            "apply_diff" when result.StartsWith("Patch parse failed:", StringComparison.OrdinalIgnoreCase)
                => "Retry apply_diff with a real patch, for example: *** Begin Patch\n*** Add File: docs/ARCHITECTURE.md\n+# Architecture\n*** End Patch. Do not use path|||content or +path|||... pseudo-patches.",
            "apply_patch" when result.StartsWith("Invalid input.", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("File not found:", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("Old text not found.", StringComparison.OrdinalIgnoreCase)
                => "Do not return final yet. Read the current file contents again and retry apply_patch with exact current text.",
            "read_file" when result.StartsWith("File not found:", StringComparison.OrdinalIgnoreCase)
                => "If the file does not exist yet, create it with apply_diff using a full add-file patch instead of returning final.",
            "create_file" when result.StartsWith("Grounding required before writing documentation.", StringComparison.OrdinalIgnoreCase)
                => "Do not invent documentation content. Your next response must be a single read/search tool call such as repo_tree, search_files, search_content, read_file, read_ranges, or context_pack. Do not retry the write yet.",
            "apply_diff" when result.StartsWith("Grounding required before writing documentation.", StringComparison.OrdinalIgnoreCase)
                => "Do not invent documentation content. Your next response must be a single read/search tool call such as repo_tree, search_files, search_content, read_file, read_ranges, or context_pack. Do not retry apply_diff yet.",
            "apply_patch" when result.StartsWith("Grounding required before writing documentation.", StringComparison.OrdinalIgnoreCase)
                => "Do not invent documentation content. Your next response must be a single read/search tool call such as repo_tree, search_files, search_content, read_file, read_ranges, or context_pack. Do not retry the docs edit yet.",
            _ when result.StartsWith("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not repeat the same failing tool call. Choose a different tool or different input that addresses the error. If the failure was on a write, switch back to a search/read tool next.",
            _ => null
        };
    }

    private static string ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Undefined => "",
            JsonValueKind.Object => value.GetRawText(),
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => value.GetRawText(),
            JsonValueKind.False => value.GetRawText(),
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };
    }

    private static bool ShouldRejectPrematureFinal(string text, int step)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = text.Trim();
        if (step == 0)
        {
            return true;
        }

        string[] prematurePhrases =
        [
            "first step",
            "next step",
            "let's start",
            "the task is to",
            "the first step",
            "we need to",
            "involves",
            "before proceeding",
            "searching for relevant files",
            "ensure that we can identify"
        ];

        return prematurePhrases.Any(phrase => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeDraftContent(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        string[] markers =
        [
            "```mermaid",
            "graph td",
            "graph lr",
            "flowchart ",
            "sequencediagram",
            "classdiagram",
            "statediagram",
            "erdiagram",
            "gantt",
            "mindmap",
            "quadrantchart",
            "# architecture",
            "## architecture"
        ];

        return markers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase));
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
            var preview = tool is IPreviewableTool p ? await p.PreviewAsync(input) : null;
            if (IsFailedPreview(tool, preview))
            {
                return Record(tool, input, preview!, true);
            }
            var approved = await RequestApprovalAsync(tool.Name, input, preview);
            if (!approved)
            {
                return Record(tool, input, "Approval denied. Tool not executed.", true);
            }
        }
        try
        {
            Console.WriteLine($"Running: {tool.Name}");
            var result = await tool.RunAsync(input);
            var isError = IsErrorResult(tool, result);
            return Record(tool, input, result, isError);
        }
        catch (Exception ex) { return Record(tool, input, $"Tool failed: {ex.Message}", true); }
    }
    private async Task<bool> RequestApprovalAsync(string toolName, string input, string? preview)
    {
        if (_state.ApprovalPromptAsync is not null)
        {
            return await _state.ApprovalPromptAsync(new ApprovalRequest(toolName, input, preview));
        }

        if (!string.IsNullOrWhiteSpace(preview))
        {
            Console.WriteLine("\nPreview\n-------");
            Console.WriteLine(preview);
        }
        Console.WriteLine($"Approve tool {toolName}?\nInput:\n{input}\n[y/N] ");
        var answer = Console.ReadLine();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase);
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
    private static bool IsErrorResult(ITool tool, string result)
    {
        return tool.Name == "run_command"
            ? result.Contains("\"ExitCode\": 1")
                || result.Contains("\"ExitCode\": -1")
                || result.Contains("\"TimedOut\": true", StringComparison.OrdinalIgnoreCase)
                || result.Contains("Blocked by command policy.", StringComparison.OrdinalIgnoreCase)
            : result.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Patch failed.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Patch parse failed:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Patch validation failed:", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Denied by approval policy", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Approval denied.", StringComparison.OrdinalIgnoreCase) || tool.Name switch
            {
                "create_file" => result.StartsWith("Invalid input.", StringComparison.OrdinalIgnoreCase)
                    || result.StartsWith("File already exists:", StringComparison.OrdinalIgnoreCase),
                "delete_file" => result.StartsWith("File not found:", StringComparison.OrdinalIgnoreCase),
                "rename_file" => result.StartsWith("Invalid input.", StringComparison.OrdinalIgnoreCase)
                    || result.StartsWith("Not found:", StringComparison.OrdinalIgnoreCase),
                "apply_patch" => result.StartsWith("Invalid input.", StringComparison.OrdinalIgnoreCase)
                    || result.StartsWith("File not found:", StringComparison.OrdinalIgnoreCase)
                    || result.StartsWith("Old text not found.", StringComparison.OrdinalIgnoreCase),
                "apply_diff" => result.StartsWith("Patch parse failed:", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
    }

    private static bool IsFailedPreview(ITool tool, string? preview)
    {
        return !string.IsNullOrWhiteSpace(preview) && tool.Name switch
        {
            "apply_diff" => preview.StartsWith("Patch parse failed:", StringComparison.OrdinalIgnoreCase)
                || preview.StartsWith("Patch validation failed:", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool TryBlockUngroundedWrite(ITool tool, string input, bool hasGroundingEvidence, out string failure)
    {
        failure = "";
        if (hasGroundingEvidence || !RequiresGroundingEvidence(tool.Name, input))
        {
            return false;
        }

        failure = "Grounding required before writing documentation. Search or read repo files first and only write after you have concrete evidence from this codebase.";
        return true;
    }

    private bool TryBlockRepeatedFailedToolCall(string toolName, string input, out string failure)
    {
        failure = "";
        ToolExecution? lastError = _state.LastError;
        if (lastError is null || !lastError.IsError)
        {
            return false;
        }

        if (!string.Equals(lastError.Tool, toolName, StringComparison.Ordinal) || !string.Equals(lastError.Input, input, StringComparison.Ordinal))
        {
            return false;
        }

        failure = $"Repeated failing tool call blocked. A previous attempt with {toolName} and the same input already failed. Read the last error and choose a different tool or different input instead of retrying the same call.";
        return true;
    }

    private bool TryBlockRepeatedDiscoveryToolCall(string toolName, string input, out string failure)
    {
        failure = "";
        ToolExecution? lastTool = _state.LastTool;
        if (lastTool is null || lastTool.IsError || !IsDiscoveryTool(toolName))
        {
            return false;
        }

        if (!string.Equals(lastTool.Tool, toolName, StringComparison.Ordinal) || !string.Equals(lastTool.Input, input, StringComparison.Ordinal))
        {
            return false;
        }

        failure = $"Repeated discovery tool call blocked. The previous {toolName} call with the same input already succeeded and will not produce new information. Refine the query or choose a different tool.";
        return true;
    }

    private static bool RequiresGroundingEvidence(string toolName, string input)
    {
        return toolName switch
        {
            "create_file" => TryGetCreateFilePath(input, out var createPath) && IsDocumentationPath(createPath),
            "apply_patch" => TryGetDelimitedPath(input, out var patchPath) && IsDocumentationPath(patchPath),
            "apply_diff" => TryGetDiffPaths(input).Any(IsDocumentationPath) || LooksLikeDocumentationWrite(input),
            _ => false
        };
    }

    private static bool IsGroundingEvidence(string toolName, string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return false;
        }

        if (toolName is not ("repo_tree" or "search_files" or "search_content" or "semantic_search" or "symbol_search" or "references_search" or "read_file" or "read_head" or "read_ranges" or "context_pack" or "list_files"))
        {
            return false;
        }

        var trimmed = result.TrimStart();
        return !IsDiscoveryFailure(trimmed);
    }

    private static bool IsDiscoveryTool(string toolName) => toolName is "repo_tree" or "search_files" or "search_content" or "semantic_search" or "symbol_search" or "references_search" or "read_file" or "read_head" or "read_ranges" or "context_pack" or "list_files";

    private static bool IsDiscoveryFailure(string result)
    {
        return result.StartsWith("Semantic index missing.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("No configured LSP server available.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("LSP symbol search failed:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("LSP references search failed:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("LSP server", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("ripgrep (rg) is not installed.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("rg:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Empty search.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("File not found:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Not found:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Directory not found:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Invalid input.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Grounding required before writing documentation.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCreateFilePath(string input, out string path) => TryGetDelimitedPath(input, out path);

    private static bool TryGetDelimitedPath(string input, out string path)
    {
        var parts = input.Split("|||", 2);
        path = parts.Length == 0 ? "" : parts[0].Trim();
        return !string.IsNullOrWhiteSpace(path);
    }

    private static string[] TryGetDiffPaths(string input)
    {
        try
        {
            return [.. PatchParser.Parse(input).Select(op => op.Path)];
        }
        catch
        {
            return [];
        }
    }

    private static bool LooksLikeDocumentationWrite(string input)
    {
        return input.Contains("docs/", StringComparison.OrdinalIgnoreCase)
            || input.Contains(".md", StringComparison.OrdinalIgnoreCase)
            || input.Contains(".mdx", StringComparison.OrdinalIgnoreCase)
            || input.Contains(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocumentationPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildInitialPrompt(string task, string plan) => _prompts.Render(PromptId.AgentInitial, new() { ["agent_md"] = _agentMd ?? "", ["previous_summary"] = _session.ReadSummary(), ["recent_log"] = _session.RecentLogTail(), ["plan"] = plan, ["tools"] = RenderTools(), ["task"] = task, ["semantic_search_status"] = _state.SemanticSearchReady ? "ready" : "missing" });
    private string BuildCompactedPrompt(string task, string summary) => _prompts.Render(PromptId.AgentCompacted, new() { ["agent_md"] = _agentMd ?? "", ["task"] = task, ["summary"] = summary, ["tools"] = RenderTools(), ["semantic_search_status"] = _state.SemanticSearchReady ? "ready" : "missing" });
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
