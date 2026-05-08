using System.Text.Json;

namespace Fetch.Core;

public sealed class AgentLoop
{
    private readonly LlmClient _llm; private readonly Dictionary<string, ITool> _tools; private readonly SessionLogger _logger; private readonly TodoStore _todoStore; private readonly AgentConfig _config; private readonly PromptCatalog _prompts; private readonly AgentSession _session; private readonly AgentRuntimeState _state; private readonly AgentEventStore _events; private readonly SemanticIndex _semanticIndex; private readonly TriageRunner _triage; private readonly ToolRouter _router; private readonly CommandResultAnalyzer _analyzer; private readonly TranscriptCompactor _compactor; private readonly ApprovalPolicy _approvalPolicy; private readonly string? _agentMd;
    private static readonly HashSet<string> MutationToolNames = new(StringComparer.Ordinal) { "apply_diff", "apply_patch", "create_file", "delete_file", "rename_file" };
    private static int _reindexInFlight;

    public AgentLoop(LlmClient llm, IEnumerable<ITool> tools, SessionLogger logger, TodoStore todoStore, AgentConfig config, PromptCatalog prompts, AgentSession session, AgentRuntimeState state, AgentEventStore events, SemanticIndex semanticIndex, TriageRunner triage)
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
        _semanticIndex = semanticIndex;
        _triage = triage;
        _router = new ToolRouter(llm, prompts);
        _analyzer = new CommandResultAnalyzer(llm, prompts);
        _compactor = new TranscriptCompactor(llm, prompts);
        _approvalPolicy = new ApprovalPolicy(config);
        _agentMd = LoadAgentContext();
    }

    public async Task RunAsync(string task)
    {
        var mutated = false;
        try
        {
            await RunInnerAsync(task, m => mutated = mutated || m);
        }
        finally
        {
            if (mutated && _config.AutoReindex)
            {
                TriggerBackgroundReindex(_semanticIndex, _state, _logger);
            }
        }
    }

    public static void TriggerBackgroundReindex(SemanticIndex semanticIndex, AgentRuntimeState state, SessionLogger? logger = null)
    {
        if (Interlocked.CompareExchange(ref _reindexInFlight, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                _ = await semanticIndex.BuildAsync();
                state.SemanticSearchReady = semanticIndex.Exists;
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    try
                    {
                        await logger.LogAsync("reindex_error", new
                        {
                            message = ex.Message
                        });
                    }
                    catch
                    {
                    }
                }
                Console.Error.WriteLine($"Background semantic reindex failed: {ex.Message}");
            }
            finally
            {
                _ = Interlocked.Exchange(ref _reindexInFlight, 0);
            }
        });
    }

    private async Task RunInnerAsync(string task, Action<bool> reportMutation)
    {
        _events.Add(AgentEventType.UserInput, "User", task);
        await _logger.LogAsync("user_task", new
        {
            task
        });
        TriageResult triage = await _triage.RunAsync(task, _agentMd);
        _state.CurrentTaskKind = triage.Kind;
        _state.CurrentPhasePlan = triage.Plan;
        _state.GroundingEvidenceBytes = 0;
        await _logger.LogAsync("triage", new
        {
            task,
            kind = triage.Kind.ToString(),
            phases = triage.Plan.Phases.Select(p => p.ToString()).ToArray(),
            isGreenfield = triage.Plan.IsGreenfield,
            goal = triage.Goal,
            llmRaw = triage.LlmRaw
        });

        await SeedPhaseTodosAsync(triage.Plan);

        var hasGroundingEvidence = false;
        var failedRuns = 0;
        var globalStep = 0;
        var transcript = "";
        var successfulDiscoveryCalls = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var phases = triage.Plan.Phases;

        for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
        {
            AgentPhase phase = phases[phaseIndex];
            var isLastPhase = phaseIndex == phases.Count - 1;
            _state.CurrentPhase = phase;
            await _logger.LogAsync("phase_enter", new
            {
                phase = phase.ToString(),
                index = phaseIndex,
                of = phases.Count
            });
            transcript = BuildPhasePrompt(task, triage, phase, transcript);

            var repeatedBlockedToolCalls = 0;
            var phaseDone = false;
            for (var phaseStep = 0; phaseStep < _config.MaxStepsPerPhase && globalStep < _config.MaxAgentSteps; phaseStep++, globalStep++)
            {
                transcript = await CompactTranscriptIfNeededAsync(task, transcript, phase);
                transcript += $"\n\nPhase {phase} step {phaseStep + 1} of {_config.MaxStepsPerPhase} (global {globalStep + 1}/{_config.MaxAgentSteps}).\n";
                (var currentTodo, var completedTodos) = await GetTodoRoutingStateAsync();
                IEnumerable<ITool> phaseTools = PhaseToolPolicy.Filter(_tools.Values, phase);
                var route = await _router.ChooseAsync(task, transcript, phaseTools, _state.SemanticSearchReady, phase, currentTodo, completedTodos);
                await _logger.LogAsync("tool_route", new
                {
                    phase = phase.ToString(),
                    step = phaseStep,
                    route
                });
                transcript += $"\n\nTool routing hint:\n{route}\n";
                transcript = await CompactTranscriptIfNeededAsync(task, transcript, phase);
                var response = await _llm.ChatAsync(transcript);
                _events.Add(AgentEventType.LlmResponse, "LLM response", response);
                await _logger.LogAsync("llm_response", new
                {
                    phase = phase.ToString(),
                    step = phaseStep,
                    response
                });
                if (!JsonHelper.TryParseObject(response, out JsonDocument? doc, out var cleaned) || doc is null)
                {
                    transcript += LooksLikeDraftContent(response)
                        ? "\nThat was prose or draft content, not an executable step. Your next response must be exactly one JSON tool call."
                        : "\nReturn ONLY one valid JSON object: {\"tool\":\"name\",\"input\":\"value\"} OR {\"phaseDone\":true} OR {\"final\":\"answer\"}.";
                    await _logger.LogAsync("error", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        kind = "invalid_json",
                        response
                    });
                    continue;
                }
                using (doc)
                {
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("phaseDone", out JsonElement pd) && pd.ValueKind == JsonValueKind.True)
                    {
                        if (phase is AgentPhase.Discovery && !hasGroundingEvidence)
                        {
                            transcript += "\nphaseDone rejected: Discovery requires at least one grounding tool result (code_map, read_ranges, search_content, symbol_search, references_search, or semantic_search) before advancing. Call one of those tools now.";
                            await _logger.LogAsync("phase_done_rejected", new
                            {
                                phase = phase.ToString(),
                                step = phaseStep,
                                reason = "no_grounding_evidence"
                            });
                            continue;
                        }
                        await _logger.LogAsync("phase_done_signal", new
                        {
                            phase = phase.ToString(),
                            step = phaseStep
                        });
                        phaseDone = true;
                        break;
                    }
                    if (root.TryGetProperty("final", out JsonElement final))
                    {
                        var text = ReadJsonValue(final);
                        var canFinal = isLastPhase || phase is AgentPhase.Answering;
                        if (!canFinal || ShouldRejectPrematureFinal(text, phaseStep))
                        {
                            transcript += canFinal
                                ? "\nDo not return final for a plan or partial progress. If work remains, return a tool call. To advance to the next phase, return {\"phaseDone\":true}."
                                : $"\nFinal answers are only allowed in the last phase or the Answering phase. The current phase is {phase}. Continue with a tool call or return {{\"phaseDone\":true}} to advance.";
                            await _logger.LogAsync("error", new
                            {
                                phase = phase.ToString(),
                                step = phaseStep,
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
                            : "\nReturn either {\"tool\":\"name\",\"input\":\"value\"}, {\"phaseDone\":true}, or {\"final\":\"answer\"}.";
                        continue;
                    }
                    var toolName = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    var input = ReadJsonValue(inp);

                    if (!PhaseToolPolicy.IsAllowed(toolName, phase))
                    {
                        var allowed = string.Join(", ", PhaseToolPolicy.AllowedToolNames(phase));
                        transcript += $"\nTool '{toolName}' is not available in the {phase} phase. Allowed tools: {allowed}. Either choose an allowed tool or return {{\"phaseDone\":true}} to advance.";
                        await _logger.LogAsync("phase_tool_blocked", new
                        {
                            phase = phase.ToString(),
                            step = phaseStep,
                            tool = toolName
                        });
                        continue;
                    }

                    if (TryEnforceCurrentTodoTool(currentTodo, toolName, out var currentTodoNudge))
                    {
                        transcript += $"\n{currentTodoNudge}";
                        await _logger.LogAsync("error", new
                        {
                            phase = phase.ToString(),
                            step = phaseStep,
                            kind = "current_todo_tool",
                            currentTodo,
                            got = toolName
                        });
                        continue;
                    }

                    if (!_tools.TryGetValue(toolName, out ITool? tool))
                    {
                        transcript += $"\nUnknown tool: {toolName}\nAvailable tools (this phase):\n{RenderToolsForPhase(phase)}";
                        await _logger.LogAsync("error", new
                        {
                            phase = phase.ToString(),
                            step = phaseStep,
                            kind = "unknown_tool",
                            tool = toolName
                        });
                        continue;
                    }
                    _events.Add(AgentEventType.ToolCall, $"Tool call: {toolName}", input, toolName, input);
                    await _logger.LogAsync("tool_call", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        tool = toolName,
                        input
                    });
                    var normalizedInput = NormalizeToolInput(toolName, input);
                    var result = TryBlockRepeatedDiscoveryToolCall(toolName, normalizedInput, successfulDiscoveryCalls, out var repeatedDiscoveryFailure)
                        ? Record(tool, input, repeatedDiscoveryFailure, true)
                        : TryBlockRepeatedFailedToolCall(toolName, normalizedInput, failedToolCalls, out var repeatedFailure)
                        ? Record(tool, input, repeatedFailure, true)
                        : TryBlockWrongDocumentationWrite(toolName, input, _state.CurrentTaskKind, out var wrongDocsWriteFailure)
                            ? Record(tool, input, wrongDocsWriteFailure, true)
                        : TryBlockUngroundedWrite(tool, input, hasGroundingEvidence, out var groundingFailure)
                            ? Record(tool, input, groundingFailure, true)
                            : await ExecuteToolAsync(tool, input);
                    _events.Add(AgentEventType.ToolResult, $"Tool result: {toolName}", result, toolName, input);
                    await _logger.LogAsync("tool_result", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
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
                        if (MutationToolNames.Contains(toolName))
                        {
                            reportMutation(true);
                        }
                        // todos advance at phase boundary, not per tool call.
                        if (isLastPhase && await TryCompleteRunAsync(task, toolName, input, result))
                        {
                            return;
                        }
                    }
                    var recoveryGuidance = BuildRecoveryGuidance(toolName, result);
                    string? analysis = null;
                    if (toolName == "run_command")
                    {
                        analysis = await _analyzer.AnalyzeAsync(result);
                        await _logger.LogAsync("command_analysis", new
                        {
                            phase = phase.ToString(),
                            step = phaseStep,
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

            await _logger.LogAsync("phase_exit", new
            {
                phase = phase.ToString(),
                phaseDone,
                globalStep
            });
            await AdvancePhaseTodoAsync(phase);
            if (globalStep >= _config.MaxAgentSteps)
            {
                break;
            }
        }
        await SynthesizeFinalAsync(task, triage, transcript);
    }

    private async Task SeedPhaseTodosAsync(PhasePlan plan)
    {
        var todos = new List<TodoItem>();
        var id = 1;
        foreach (AgentPhase phase in plan.Phases)
        {
            todos.Add(new TodoItem(id, $"{phase} phase", id == 1 ? "in_progress" : "pending"));
            id++;
        }
        if (todos.Count > 0)
        {
            await _todoStore.WriteAsync(todos);
        }
    }

    private async Task<bool> TryCompleteRunAsync(string task, string toolName, string input, string result)
    {
        List<TodoItem> todos = await _todoStore.ReadAsync();
        if (todos.Count == 0 || todos.Any(t => !string.Equals(t.Status, "done", StringComparison.Ordinal)))
        {
            return false;
        }

        var text = BuildCompletionMessage(task, toolName, input, result);
        _events.Add(AgentEventType.Final, "Final", text);
        await _logger.LogAsync("final", new
        {
            text,
            completedByTodos = true
        });
        Console.WriteLine(text);
        return true;
    }

    private static string BuildCompletionMessage(string task, string toolName, string input, string result)
    {
        if (toolName == "read_file")
        {
            var verifiedPath = TryExtractPathInput(input) ?? input.Trim();
            return $"Completed: {task}. Verified {verifiedPath} after writing it.";
        }

        return toolName == "run_command" && result.Contains("\"ExitCode\": 0", StringComparison.Ordinal)
            ? $"Completed: {task}. The final verification command succeeded."
            : $"Completed: {task}.";
    }

    private static string? TryExtractPathInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(input);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("path", out JsonElement path)
                && path.ValueKind == JsonValueKind.String)
            {
                return path.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryEnforceRequiredFirstTool(int step, string toolName, TriageResult triage, out string nudge)
    {
        nudge = "";
        // Phase gating now handles this; kept as a no-op stub to avoid breaking callers.
        _ = step;
        _ = toolName;
        _ = triage;
        return false;
    }

    private static bool TryEnforceCurrentTodoTool(string currentTodo, string toolName, out string nudge)
    {
        nudge = "";
        if (!TryGetPinnedTodoTool(currentTodo, out var requiredTool)
            || string.Equals(requiredTool, toolName, StringComparison.Ordinal))
        {
            return false;
        }

        nudge = $"The active todo is '{currentTodo}', so the next tool MUST be {requiredTool} (you chose {toolName}). Do not go back to an earlier completed-step tool. Return exactly one JSON tool call for {requiredTool}.";
        return true;
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

    private async Task SynthesizeFinalAsync(string task, TriageResult triage, string transcript)
    {
        await _logger.LogAsync("synthesize_final", new
        {
            steps = _config.MaxAgentSteps,
            reason = "phases_completed_without_final"
        });
        try
        {
            var summary = await _compactor.CompactAsync(transcript);
            var prompt = _prompts.Render(PromptId.FinalSynthesis, new()
            {
                ["task"] = task,
                ["task_kind"] = triage.Kind.ToString(),
                ["transcript"] = summary
            });
            var synthesized = await _llm.ChatAsync(prompt);
            var text = string.IsNullOrWhiteSpace(synthesized)
                ? $"Stopped after {_config.MaxAgentSteps} steps without producing a final answer."
                : synthesized.Trim();
            if (ShouldSuppressDraftFinal(triage.Kind, transcript, text))
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
        return kind is TaskKind.ArchitectureDocs or TaskKind.Documentation && (transcript.Contains("Patch parse failed:", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase)) && !transcript.Contains("Patch applied successfully.", StringComparison.OrdinalIgnoreCase) && (synthesized.Contains("# Architecture", StringComparison.OrdinalIgnoreCase)
            || synthesized.Contains("## ", StringComparison.Ordinal)
            || synthesized.Contains("```mermaid", StringComparison.OrdinalIgnoreCase));
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
            "apply_diff" when result.StartsWith("Architecture/documentation tasks must target a docs markdown file", StringComparison.OrdinalIgnoreCase)
                => "For architecture/documentation tasks, write only docs/ARCHITECTURE.md or another docs/*.md file using a real *** Begin Patch payload. Do not modify .cs files.",
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

    private async Task<string> CompactTranscriptIfNeededAsync(string task, string transcript, AgentPhase phase)
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
            phase = phase.ToString(),
            promptTokens,
            maxPromptTokens,
            contextWindowTokens = _config.ContextWindowTokens,
            contextWindowReserveTokens = _config.ContextWindowReserveTokens,
            summary
        });
        return BuildCompactedPrompt(task, summary, phase);
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

    private static bool TryBlockWrongDocumentationWrite(string toolName, string input, TaskKind taskKind, out string failure)
    {
        failure = "";
        return taskKind is TaskKind.ArchitectureDocs or TaskKind.Documentation && toolName switch
        {
            "create_file" when TryGetCreateFilePath(input, out var createPath) && !IsDocumentationPath(createPath)
                => FailDocumentationWrite("Architecture/documentation tasks must write a markdown file under docs/ such as docs/ARCHITECTURE.md. Do not modify source files for this step.", out failure),
            "apply_patch" when TryGetDelimitedPath(input, out var patchPath) && !IsDocumentationPath(patchPath)
                => FailDocumentationWrite("Architecture/documentation tasks must write a markdown file under docs/ such as docs/ARCHITECTURE.md. Do not modify source files for this step.", out failure),
            "apply_diff" when !LooksLikeDocumentationWrite(input)
                => FailDocumentationWrite("Architecture/documentation tasks must target a docs markdown file such as docs/ARCHITECTURE.md. Provide a real *** Begin Patch payload with *** Add File or *** Update File, and do not modify .cs files.", out failure),
            _ => false
        };
    }

    private static bool FailDocumentationWrite(string message, out string failure)
    {
        failure = message;
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

        if (toolName is "code_map" or "read_ranges" or "symbol_search" or "references_search" or "relationship_map" or "todo_write")
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

    private async Task AdvancePhaseTodoAsync(AgentPhase justFinished)
    {
        List<TodoItem> todos = await _todoStore.ReadAsync();
        if (todos.Count == 0)
        {
            return;
        }
        var name = justFinished.ToString();
        var idx = todos.FindIndex(t => t.Text.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t.Status, "done", StringComparison.Ordinal));
        if (idx >= 0)
        {
            todos[idx] = todos[idx] with
            {
                Status = "done"
            };
        }
        var next = todos.FindIndex(t => string.Equals(t.Status, "pending", StringComparison.Ordinal));
        if (next >= 0)
        {
            todos[next] = todos[next] with
            {
                Status = "in_progress"
            };
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
        if (toolName is not ("code_map" or "search_content" or "semantic_search" or "symbol_search" or "references_search" or "relationship_map" or "read_file" or "read_head" or "read_ranges" or "context_pack"))
        {
            return false;
        }

        var trimmed = result.TrimStart();
        return !IsDiscoveryFailure(trimmed);
    }

    private static bool IsDiscoveryTool(string toolName) => toolName is "repo_tree" or "search_files" or "search_content" or "semantic_search" or "symbol_search" or "references_search" or "relationship_map" or "read_file" or "read_head" or "read_ranges" or "context_pack" or "list_files" or "code_map";

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

    private string BuildPhasePrompt(string task, TriageResult triage, AgentPhase phase, string priorTranscript)
    {
        var recentState = string.IsNullOrWhiteSpace(priorTranscript)
            ? (string.IsNullOrWhiteSpace(_session.ReadSummary()) ? "(none)" : Trim(_session.ReadSummary(), 4000))
            : Trim(priorTranscript, 4000);
        var thinkingHint = _config.EnableThinking && LlmClient.IsThinkingModel(_config.ModelName)
            ? "\nUse step-by-step thinking (<think>...</think>) before emitting the JSON. The <think> block will be stripped before parsing.\n"
            : "";
        return _prompts.Render(PromptId.PhaseAgent, new()
        {
            ["phase"] = phase.ToString(),
            ["phase_hint"] = PhaseToolPolicy.PhaseHint(phase) + thinkingHint,
            ["task"] = task,
            ["goal"] = triage.Goal,
            ["current_todo"] = "(seed)",
            ["completed_todos"] = "(none)",
            ["tools"] = RenderToolsForPhase(phase),
            ["recent_state"] = recentState
        });
    }

    private string BuildCompactedPrompt(string task, string summary, AgentPhase phase) => _prompts.Render(PromptId.PhaseAgent, new()
    {
        ["phase"] = phase.ToString(),
        ["phase_hint"] = PhaseToolPolicy.PhaseHint(phase),
        ["task"] = task,
        ["goal"] = _state.CurrentPhasePlan?.Goal ?? "",
        ["current_todo"] = "(see summary)",
        ["completed_todos"] = "(see summary)",
        ["tools"] = RenderToolsForPhase(phase),
        ["recent_state"] = "Compacted summary:\n" + summary
    });

    private string RenderToolsForPhase(AgentPhase phase) =>
        string.Join("\n", PhaseToolPolicy.Filter(_tools.Values, phase).Select(t => $"- {t.Name}: {ShortDescription(t.Description)}"));

    private static string ShortDescription(string description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return description;
        }
        var dot = description.IndexOf('.');
        var newline = description.IndexOf('\n');
        var cut = dot < 0 ? newline : (newline < 0 ? dot : Math.Min(dot, newline));
        return cut < 0 ? description : description[..(cut + 1)].Trim();
    }

    private static string Trim(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[^maxChars..];
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
