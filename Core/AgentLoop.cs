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
        PlanResult plan = await _planner.CreatePlanAsync(task, _agentMd);
        _state.CurrentTaskKind = plan.Kind;
        _state.CurrentPlaybook = plan.Playbook;
        _state.GroundingEvidenceBytes = 0;
        await _logger.LogAsync("plan", new
        {
            task,
            kind = plan.Kind.ToString(),
            playbook = plan.Playbook,
            llmPlan = plan.LlmPlanJson
        });
        await PlannerTodoSeeder.SeedAsync(plan, _todoStore);
        var transcript = BuildInitialPrompt(task, plan);
        var failedRuns = 0;
        var hasGroundingEvidence = false;
        var repeatedBlockedToolCalls = 0;
        var successfulDiscoveryCalls = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        for (var step = 0; step < _config.MaxAgentSteps; step++)
        {
            transcript = await CompactTranscriptIfNeededAsync(task, transcript);
            transcript += $"\n\nStep {step + 1} of {_config.MaxAgentSteps}.\n";
            var (currentTodo, completedTodos) = await GetTodoRoutingStateAsync();
            var route = await _router.ChooseAsync(task, transcript, _tools.Values, _state.SemanticSearchReady, plan, currentTodo, completedTodos);
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
                    ? "\nThat was prose or draft diagram content, not an executable step. Do not draft the architecture yet. Your next response must be exactly one JSON tool call, preferably code_map, read_ranges, or another concrete read/search step."
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

                if (TryEnforceRequiredFirstTool(step, toolName, plan, out var firstToolNudge))
                {
                    transcript += $"\n{firstToolNudge}";
                    await _logger.LogAsync("error", new
                    {
                        step,
                        kind = "required_first_tool",
                        expected = plan.Playbook.RequiredFirstTool,
                        got = toolName
                    });
                    continue;
                }

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
                var normalizedInput = NormalizeToolInput(toolName, input);
                var result = TryBlockRepeatedDiscoveryToolCall(toolName, normalizedInput, successfulDiscoveryCalls, out var repeatedDiscoveryFailure)
                    ? Record(tool, input, repeatedDiscoveryFailure, true)
                    : TryBlockRepeatedFailedToolCall(toolName, normalizedInput, failedToolCalls, out var repeatedFailure)
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
                if (IsGroundingEvidence(toolName, result))
                {
                    hasGroundingEvidence = true;
                    _state.GroundingEvidenceBytes += result.Length;
                }
                TrackToolCall(toolName, normalizedInput, result, successfulDiscoveryCalls, failedToolCalls);
                if (_state.LastTool is { IsError: false })
                {
                    await AdvanceTodoProgressAsync(toolName);
                }
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
        await SynthesizeFinalAsync(task, plan, transcript);
    }

    private bool TryEnforceRequiredFirstTool(int step, string toolName, PlanResult plan, out string nudge)
    {
        nudge = "";
        if (step != 0)
        {
            return false;
        }
        var required = plan.Playbook.RequiredFirstTool;
        if (string.IsNullOrEmpty(required) || string.Equals(toolName, required, StringComparison.Ordinal))
        {
            return false;
        }
        if (!_tools.ContainsKey(required))
        {
            return false;
        }
        nudge = $"For task kind {plan.Kind}, the first tool call MUST be {required} (you chose {toolName}). Re-issue the call as {{\"tool\":\"{required}\",\"input\":\"\"}} (empty input maps the whole repo).";
        return true;
    }

    private async Task SynthesizeFinalAsync(string task, PlanResult plan, string transcript)
    {
        await _logger.LogAsync("max_steps_reached", new
        {
            steps = _config.MaxAgentSteps
        });
        try
        {
            var summary = await _compactor.CompactAsync(transcript);
            var prompt = _prompts.Render(PromptId.FinalSynthesis, new()
            {
                ["task"] = task,
                ["task_kind"] = plan.Kind.ToString(),
                ["transcript"] = summary
            });
            var synthesized = await _llm.ChatAsync(prompt);
            var text = string.IsNullOrWhiteSpace(synthesized)
                ? $"Stopped after {_config.MaxAgentSteps} steps without producing a final answer."
                : synthesized.Trim();
            if (ShouldSuppressDraftFinal(plan.Kind, transcript, text))
            {
                text = "Stopped after the step budget. The agent gathered repo context but never completed the write: apply_diff was repeatedly called with incorrectly wrapped input instead of a raw patch. The next run should retry apply_diff with a real *** Begin Patch payload and then request approval.";
            }
            _events.Add(AgentEventType.Final, "Final", text);
            await _logger.LogAsync("final", new
            {
                text,
                synthesized = true
            });
            Console.WriteLine(text);
        }
        catch (Exception ex)
        {
            var text = $"Stopped after {_config.MaxAgentSteps} steps. Final synthesis failed: {ex.Message}";
            _events.Add(AgentEventType.Final, "Final", text);
            await _logger.LogAsync("final", new
            {
                text,
                synthesized = false
            });
            Console.WriteLine(text);
        }
    }

    private static bool ShouldSuppressDraftFinal(TaskKind kind, string transcript, string synthesized)
    {
        if (kind is not (TaskKind.ArchitectureDocs or TaskKind.Documentation))
        {
            return false;
        }

        if (!transcript.Contains("Patch parse failed:", StringComparison.OrdinalIgnoreCase)
            && !transcript.Contains("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (transcript.Contains("Patch applied successfully.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return synthesized.Contains("# Architecture", StringComparison.OrdinalIgnoreCase)
            || synthesized.Contains("## ", StringComparison.Ordinal)
            || synthesized.Contains("```mermaid", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildRecoveryGuidance(string toolName, string result)
    {
        return toolName switch
        {
            "code_map" when !result.StartsWith("code_map: no source files", StringComparison.OrdinalIgnoreCase)
                => "code_map gave you the repo skeleton. Next, pick 3-6 files from the map and call read_ranges on them to get concrete code before drafting any document.",
            "semantic_search" when !result.StartsWith("Semantic index missing.", StringComparison.OrdinalIgnoreCase)
                => "Use these semantic_search results to narrow to 3-8 concrete files with refine_context or read_ranges. Do not jump back to repo_tree with the same broad input.",
            "repo_tree" when !result.StartsWith("Repeated", StringComparison.OrdinalIgnoreCase)
                => "Repo tree only gives broad structure. For architecture/overview tasks call code_map next; otherwise call a more specific search or read tool.",
            "semantic_search" when result.StartsWith("Semantic index missing.", StringComparison.OrdinalIgnoreCase)
                => "Semantic search is unavailable until the index is built. Do not call semantic_search again in this run. Use repo_tree, search_files, search_content, and refine_context to gather evidence instead.",
            "repo_tree" when result.StartsWith("Repeated discovery tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not call repo_tree again with the same input. Choose a different tool such as semantic_search, search_content, refine_context, or read_ranges.",
            "repo_tree" when result.StartsWith("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not call repo_tree again with the same input. Choose a different tool such as semantic_search, search_content, refine_context, or read_ranges.",
            _ when result.StartsWith("Repeated discovery tool call blocked.", StringComparison.OrdinalIgnoreCase)
                => "Do not repeat the same discovery tool call with the same input. Refine the query, choose a more specific read/search tool, or move on to the next evidence-gathering step.",
            "read_ranges" when result.StartsWith("Invalid range JSON:", StringComparison.OrdinalIgnoreCase)
                => "Retry read_ranges with JSON shaped like [{\"file\":\"path/to/file.cs\",\"start\":1,\"end\":80}] or {\"file\":\"path/to/file.cs\",\"start\":1,\"end\":80} using a real file from code_map or search results.",
            "read_ranges" when result.StartsWith("Invalid input. Requested range", StringComparison.OrdinalIgnoreCase)
                => "That read_ranges request was invalid or past EOF. Do not keep incrementing the same file blindly. Choose a valid 1-based range within that file, or switch to a different anchor file from code_map.",
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
                "read_ranges" => result.StartsWith("Invalid range JSON:", StringComparison.OrdinalIgnoreCase)
                    || result.StartsWith("Invalid input.", StringComparison.OrdinalIgnoreCase),
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

    private bool TryBlockUngroundedWrite(ITool tool, string input, bool hasGroundingEvidence, out string failure)
    {
        failure = "";
        if (!RequiresGroundingEvidence(tool.Name, input))
        {
            return false;
        }
        const int minEvidenceBytes = 2048;
        if (hasGroundingEvidence && _state.GroundingEvidenceBytes >= minEvidenceBytes)
        {
            return false;
        }
        failure = "Grounding required before writing documentation. Call code_map and at least one read tool (read_ranges, read_file, context_pack) on real repo files first; accumulated evidence must be at least "
            + minEvidenceBytes
            + " bytes. Do not retry the write yet.";
        return true;
    }

    private static void TrackToolCall(string toolName, string normalizedInput, string result, HashSet<string> successfulDiscoveryCalls, HashSet<string> failedToolCalls)
    {
        var key = MakeToolCallKey(toolName, normalizedInput);
        if (IsDiscoveryTool(toolName) && !IsDiscoveryFailure(result) && !result.StartsWith("Repeated ", StringComparison.OrdinalIgnoreCase))
        {
            _ = successfulDiscoveryCalls.Add(key);
        }

        if (result.StartsWith("Repeated ", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Invalid ", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Patch ", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Denied by approval policy", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Approval denied.", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Grounding required before writing documentation.", StringComparison.OrdinalIgnoreCase))
        {
            _ = failedToolCalls.Add(key);
        }
    }

    private static string NormalizeToolInput(string toolName, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "";
        }

        if (toolName is "code_map" or "read_ranges" or "symbol_search" or "references_search" or "todo_write")
        {
            try
            {
                using var doc = JsonDocument.Parse(input);
                return JsonSerializer.Serialize(doc.RootElement);
            }
            catch
            {
                return input.Trim();
            }
        }

        return input.Trim();
    }

    private static string MakeToolCallKey(string toolName, string normalizedInput) => toolName + "\n" + normalizedInput;

    private async Task AdvanceTodoProgressAsync(string toolName)
    {
        if (_state.CurrentPlaybook is null)
        {
            return;
        }

        List<TodoItem> todos = await _todoStore.ReadAsync();
        if (todos.Count == 0)
        {
            return;
        }

        var marker = $"({toolName})";
        var currentIndex = todos.FindIndex(t => string.Equals(t.Status, "in_progress", StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            return;
        }

        if (!todos[currentIndex].Text.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        todos[currentIndex] = todos[currentIndex] with
        {
            Status = "done"
        };
        var nextIndex = todos.FindIndex(currentIndex + 1, t => string.Equals(t.Status, "pending", StringComparison.Ordinal));
        if (nextIndex >= 0)
        {
            todos[nextIndex] = todos[nextIndex] with
            {
                Status = "in_progress"
            };

            while (nextIndex >= 0 && todos[nextIndex].Text.StartsWith("Optionally ", StringComparison.OrdinalIgnoreCase))
            {
                todos[nextIndex] = todos[nextIndex] with
                {
                    Status = "done"
                };
                nextIndex = todos.FindIndex(nextIndex + 1, t => string.Equals(t.Status, "pending", StringComparison.Ordinal));
                if (nextIndex >= 0)
                {
                    todos[nextIndex] = todos[nextIndex] with
                    {
                        Status = "in_progress"
                    };
                }
            }
        }

        await _todoStore.WriteAsync(todos);
    }

    private async Task<(string CurrentTodo, string CompletedTodos)> GetTodoRoutingStateAsync()
    {
        List<TodoItem> todos = await _todoStore.ReadAsync();
        var currentTodo = todos.FirstOrDefault(t => string.Equals(t.Status, "in_progress", StringComparison.Ordinal))?.Text ?? "(none)";
        var completedTodos = string.Join(" | ", todos.Where(t => string.Equals(t.Status, "done", StringComparison.Ordinal)).Select(t => t.Text));
        return (currentTodo, string.IsNullOrWhiteSpace(completedTodos) ? "(none)" : completedTodos);
    }

    private static bool TryBlockRepeatedFailedToolCall(string toolName, string normalizedInput, HashSet<string> failedToolCalls, out string failure)
    {
        failure = "";
        if (!failedToolCalls.Contains(MakeToolCallKey(toolName, normalizedInput)))
        {
            return false;
        }

        failure = $"Repeated failing tool call blocked. A previous attempt with {toolName} and the same input already failed. Read the last error and choose a different tool or different input instead of retrying the same call.";
        return true;
    }

    private static bool TryBlockRepeatedDiscoveryToolCall(string toolName, string normalizedInput, HashSet<string> successfulDiscoveryCalls, out string failure)
    {
        failure = "";
        if (!IsDiscoveryTool(toolName))
        {
            return false;
        }

        if (!successfulDiscoveryCalls.Contains(MakeToolCallKey(toolName, normalizedInput)))
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

        // repo_tree alone is NOT sufficient grounding for docs writes - it lists files without their content.
        // code_map, read_*, search_content with hits, semantic_search, context_pack all qualify.
        if (toolName is not ("code_map" or "search_content" or "semantic_search" or "symbol_search" or "references_search" or "read_file" or "read_head" or "read_ranges" or "context_pack"))
        {
            return false;
        }

        var trimmed = result.TrimStart();
        return !IsDiscoveryFailure(trimmed);
    }

    private static bool IsDiscoveryTool(string toolName) => toolName is "repo_tree" or "search_files" or "search_content" or "semantic_search" or "symbol_search" or "references_search" or "read_file" or "read_head" or "read_ranges" or "context_pack" or "list_files" or "code_map";

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

    private string BuildInitialPrompt(string task, PlanResult plan) => _prompts.Render(PromptId.AgentInitial, new()
    {
        ["agent_md"] = _agentMd ?? "",
        ["previous_summary"] = _session.ReadSummary(),
        ["recent_log"] = _session.RecentLogTail(),
        ["plan"] = plan.LlmPlanJson,
        ["task_kind"] = plan.Kind.ToString(),
        ["playbook_hint"] = plan.Playbook.Hint,
        ["playbook_steps"] = string.Join("\n", plan.Playbook.Steps.Select((s, i) => $"{i + 1}. {s}")),
        ["required_first_tool"] = plan.Playbook.RequiredFirstTool ?? "(none)",
        ["tools"] = RenderTools(),
        ["task"] = task,
        ["semantic_search_status"] = _state.SemanticSearchReady ? "ready" : "missing"
    });
    private string BuildCompactedPrompt(string task, string summary) => _prompts.Render(PromptId.AgentCompacted, new()
    {
        ["agent_md"] = _agentMd ?? "",
        ["task"] = task,
        ["summary"] = summary,
        ["tools"] = RenderTools(),
        ["semantic_search_status"] = _state.SemanticSearchReady ? "ready" : "missing",
        ["task_kind"] = _state.CurrentTaskKind.ToString(),
        ["playbook_hint"] = _state.CurrentPlaybook?.Hint ?? "",
        ["playbook_steps"] = _state.CurrentPlaybook is null
            ? ""
            : string.Join("\n", _state.CurrentPlaybook.Steps.Select((s, i) => $"{i + 1}. {s}"))
    });
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
