using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fetch.Core;

public sealed partial class AgentLoop
{
    private readonly LlmClient _llm; private readonly Dictionary<string, ITool> _tools; private readonly SessionLogger _logger; private readonly TodoStore _todoStore; private readonly AgentConfig _config; private readonly PromptCatalog _prompts; private readonly AgentSession _session; private readonly AgentRuntimeState _state; private readonly AgentEventStore _events; private readonly SemanticIndex _semanticIndex; private readonly TriageRunner _triage; private readonly CommandResultAnalyzer _analyzer; private readonly TranscriptCompactor _compactor; private readonly ApprovalPolicy _approvalPolicy; private readonly string? _agentMd;
    private const int MinDocumentationEvidenceBytes = 2048;
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
            await RunNativeAsync(task, m => mutated = mutated || m);
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

    private ToolSchemaRegistry NativeToolRegistry => field ??= new ToolSchemaRegistry(_tools.Values);

    private async Task RunNativeAsync(string task, Action<bool> reportMutation)
    {
        _events.Add(AgentEventType.UserInput, "User", task);
        await _logger.LogAsync("user_task", new
        {
            task,
            mode = "native_tools"
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
            llmRaw = triage.LlmRaw,
            mode = "native_tools"
        });

        await SeedPhaseTodosAsync(triage.Plan);

        var hasGroundingEvidence = false;
        var failedRuns = 0;
        var globalStep = 0;
        var transcript = "";
        var successfulDiscoveryCalls = new HashSet<string>(StringComparer.Ordinal);
        var failedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        var mutationLog = new List<string>();
        IReadOnlyList<AgentPhase> phases = triage.Plan.Phases;

        for (var phaseIndex = 0; phaseIndex < phases.Count; phaseIndex++)
        {
            AgentPhase phase = phases[phaseIndex];
            var isLastPhase = phaseIndex == phases.Count - 1;
            _state.CurrentPhase = phase;
            await _logger.LogAsync("phase_enter", new
            {
                phase = phase.ToString(),
                index = phaseIndex,
                of = phases.Count,
                mode = "native_tools"
            });

            transcript = await CompactTranscriptIfNeededAsync(transcript, phase);
            (var currentTodo, var completedTodos) = await GetTodoRoutingStateAsync();
            ITool[] phaseTools = [.. PhaseToolPolicy.Filter(_tools.Values, phase)];
            IReadOnlyList<NativeToolDefinition> toolDefinitions = ToolSchemaRegistry.BuildDefinitions(phaseTools);
            var messages = new List<LlmChatMessage>
            {
                new("system", await BuildNativePhasePromptAsync(task, triage, phase, transcript, currentTodo, completedTodos)),
                new("user", $"Continue the {phase} phase for the current task. Use tools when needed. When this phase is complete, call the phase_complete tool instead of replying with PHASE_DONE text.")
            };

            var repeatedBlockedToolCalls = 0;
            var phaseDone = false;
            var phaseAttemptedMutation = false;
            var phaseHadSuccessfulMutation = false;

            for (var phaseStep = 0; phaseStep < _config.MaxStepsPerPhase && globalStep < _config.MaxAgentSteps; phaseStep++, globalStep++)
            {
                LlmChatResponse response = await _llm.ChatWithToolsAsync(messages, toolDefinitions);
                if (!string.IsNullOrWhiteSpace(response.Warning))
                {
                    _events.Add(AgentEventType.LlmResponse, "Model retry", response.Warning);
                    await _logger.LogAsync("llm_warning", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        warning = response.Warning,
                        mode = "native_tools"
                    });
                    if (_state.ApprovalPromptAsync is null)
                    {
                        Console.WriteLine(response.Warning);
                    }
                }

                if (!string.IsNullOrWhiteSpace(response.Reasoning))
                {
                    _events.Add(AgentEventType.Reasoning, $"Reasoning: {phase}", response.Reasoning);
                    await _logger.LogAsync("llm_reasoning", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        reasoning = response.Reasoning
                    });
                    if (_state.ApprovalPromptAsync is null)
                    {
                        Console.WriteLine($"\n[Reasoning: {phase}]\n{response.Reasoning}");
                    }
                }

                _events.Add(AgentEventType.LlmResponse, "LLM response", string.IsNullOrWhiteSpace(response.Content) ? "(tool call)" : response.Content);
                await _logger.LogAsync("llm_response", new
                {
                    phase = phase.ToString(),
                    step = phaseStep,
                    content = response.Content,
                    reasoning = response.Reasoning,
                    toolCalls = response.ToolCalls.Select(t => new { t.Name, t.ArgumentsJson }).ToArray()
                });

                messages.Add(new LlmChatMessage("assistant", response.Content, response.Reasoning, ToolCalls: response.ToolCalls));

                if (ShouldForceDiscoveryAdvance(phase, _state.CurrentTaskKind, response, _state.GroundingEvidenceBytes))
                {
                    const string autoAdvanceMessage = "Discovery gathered enough evidence and the model signaled readiness to write. Advancing to the Editing phase.";
                    _events.Add(AgentEventType.LlmResponse, "Phase auto-advance", autoAdvanceMessage);
                    await _logger.LogAsync("phase_force_advance", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        reason = "discovery_ready_to_write",
                        mode = "native_tools"
                    });
                    phaseDone = true;
                    break;
                }

                if (response.ToolCalls.Count == 0)
                {
                    var content = response.Content.Trim();
                    var canFinal = isLastPhase || phase is AgentPhase.Answering || phase is AgentPhase.Verification;
                    if (content.Contains("PHASE_DONE", StringComparison.Ordinal))
                    {
                        messages.Add(new LlmChatMessage("user", canFinal
                            ? "PHASE_DONE text is not accepted. Call the phase_complete tool to advance this phase, or return the final user-facing answer if the task is complete."
                            : "PHASE_DONE text is not accepted. If this phase is complete, call the phase_complete tool. Otherwise call another tool to keep working."));
                        continue;
                    }

                    if (canFinal && !ShouldRejectPrematureFinal(content, phaseStep))
                    {
                        _events.Add(AgentEventType.Final, "Final", content);
                        await _logger.LogAsync("final", new
                        {
                            text = content,
                            mode = "native_tools"
                        });
                        Console.WriteLine(content);
                        return;
                    }

                    messages.Add(new LlmChatMessage("user", canFinal
                        ? "If the task is complete, return the final answer. Otherwise call a tool to continue working."
                        : "If work remains in this phase, call a tool. When the phase is actually complete, call the phase_complete tool."));
                    continue;
                }

                for (var toolIndex = 0; toolIndex < response.ToolCalls.Count; toolIndex++)
                {
                    LlmToolCall toolCall = response.ToolCalls[toolIndex];
                    var toolName = toolCall.Name;
                    var input = "";
                    var result = "";
                    string? analysis = null;
                    var executedTool = false;

                    if (!PhaseToolPolicy.IsAllowed(toolName, phase))
                    {
                        var allowed = string.Join(", ", PhaseToolPolicy.AllowedToolNames(phase));
                        result = $"Tool '{toolName}' is not available in the {phase} phase. Allowed tools: {allowed}.";
                    }
                    else if (TryEnforceCurrentTodoTool(currentTodo, toolName, phase, _state.CurrentTaskKind, phaseAttemptedMutation, out var currentTodoNudge))
                    {
                        result = currentTodoNudge;
                    }
                    else if (!NativeToolRegistry.TryGetTool(toolName, out ITool? tool) || tool is null)
                    {
                        result = $"Unknown tool: {toolName}\nAvailable tools (this phase):\n{RenderToolsForPhase(phase)}";
                    }
                    else if (TryBlockDocumentationEditingBeforeFirstWrite(phase, _state.CurrentTaskKind, phaseAttemptedMutation, toolName, out var docsEditingFailure))
                    {
                        result = docsEditingFailure;
                    }
                    else
                    {
                        var argumentsValid = true;
                        try
                        {
                            input = ToolSchemaRegistry.ConvertArguments(tool, toolCall.ArgumentsJson);
                        }
                        catch (Exception ex)
                        {
                            argumentsValid = false;
                            result = $"Invalid tool arguments for {toolName}: {ex.Message}";
                        }

                        if (!argumentsValid)
                        {
                        }
                        else
                        {
                            if (phase is AgentPhase.Editing && MutationToolNames.Contains(toolName))
                            {
                                phaseAttemptedMutation = true;
                            }

                            if (toolName is "apply_diff" or "apply_patch" && IsJsonObjectInput(input))
                            {
                                result = "apply_diff/apply_patch input must be a raw V4A patch string starting with '*** Begin Patch', not a JSON object.";
                            }
                            else
                            {
                                _events.Add(AgentEventType.ToolCall, $"Tool call: {toolName}", input, toolName, input);
                                await _logger.LogAsync("tool_call", new
                                {
                                    phase = phase.ToString(),
                                    step = phaseStep,
                                    tool = toolName,
                                    input,
                                    mode = "native_tools"
                                });

                                executedTool = true;
                                var normalizedInput = NormalizeToolInput(toolName, input);
                                if (toolName == "phase_complete")
                                {
                                    (result, phaseDone) = await ExecutePhaseCompleteToolAsync(tool, input, phase, phaseStep, hasGroundingEvidence, phaseHadSuccessfulMutation, toolIndex == response.ToolCalls.Count - 1);
                                    TrackToolCall(toolName, normalizedInput, result, successfulDiscoveryCalls, failedToolCalls);
                                }
                                else
                                {
                                    result = TryBlockRepeatedDiscoveryToolCall(toolName, normalizedInput, successfulDiscoveryCalls, out var repeatedDiscoveryFailure)
                                        ? Record(tool, input, repeatedDiscoveryFailure, true)
                                        : TryBlockRepeatedFailedToolCall(toolName, normalizedInput, failedToolCalls, out var repeatedFailure)
                                        ? Record(tool, input, repeatedFailure, true)
                                        : TryBlockWrongDocumentationWrite(toolName, input, _state.CurrentTaskKind, out var wrongDocsWriteFailure)
                                            ? Record(tool, input, wrongDocsWriteFailure, true)
                                        : TryBlockUngroundedWrite(tool, input, hasGroundingEvidence, out var groundingFailure)
                                            ? Record(tool, input, groundingFailure, true)
                                            : await ExecuteToolAsync(tool, input);

                                    repeatedBlockedToolCalls = result.StartsWith("Repeated discovery tool call blocked.", StringComparison.OrdinalIgnoreCase)
                                        || result.StartsWith("Repeated failing tool call blocked.", StringComparison.OrdinalIgnoreCase)
                                        ? repeatedBlockedToolCalls + 1
                                        : 0;
                                    if (repeatedBlockedToolCalls >= 3)
                                    {
                                        var canAdvancePhase = phase switch
                                        {
                                            AgentPhase.Discovery => hasGroundingEvidence,
                                            AgentPhase.Editing => phaseHadSuccessfulMutation,
                                            AgentPhase.Triage => throw new NotImplementedException(),
                                            AgentPhase.Planning => throw new NotImplementedException(),
                                            AgentPhase.Verification => throw new NotImplementedException(),
                                            AgentPhase.Answering => throw new NotImplementedException(),
                                            _ => true
                                        };
                                        if (canAdvancePhase && !isLastPhase)
                                        {
                                            await _logger.LogAsync("phase_force_advance", new
                                            {
                                                phase = phase.ToString(),
                                                step = phaseStep,
                                                reason = "repeated_blocked_tool_calls",
                                                tool = toolName,
                                                mode = "native_tools"
                                            });
                                            phaseDone = true;
                                        }

                                        if (phaseDone)
                                        {
                                            break;
                                        }

                                        var blocker = $"Blocked: the agent is repeatedly retrying {toolName} with the same input after explicit loop-prevention errors.";
                                        _events.Add(AgentEventType.Final, "Final", blocker);
                                        await _logger.LogAsync("final", new
                                        {
                                            text = blocker,
                                            mode = "native_tools"
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
                                            phaseHadSuccessfulMutation = true;
                                            foreach (var path in ExtractMutationPaths(toolName, input, result))
                                            {
                                                var entry = $"{toolName} -> {path}";
                                                if (!mutationLog.Contains(entry, StringComparer.Ordinal))
                                                {
                                                    mutationLog.Add(entry);
                                                }
                                            }
                                        }

                                        if (isLastPhase && await TryCompleteRunAsync(task, toolName, input, result))
                                        {
                                            return;
                                        }
                                    }

                                    if (toolName == "run_command")
                                    {
                                        analysis = await _analyzer.AnalyzeAsync(result);
                                        await _logger.LogAsync("command_analysis", new
                                        {
                                            phase = phase.ToString(),
                                            step = phaseStep,
                                            analysis,
                                            mode = "native_tools"
                                        });
                                        if (!result.Contains("\"ExitCode\": 0"))
                                        {
                                            failedRuns++;
                                            if (failedRuns >= _config.MaxFailedCommandAttempts)
                                            {
                                                messages.Add(new LlmChatMessage("user", "Maximum failed command attempts reached. Return the blocker or choose a different recovery tool."));
                                            }
                                        }
                                        else
                                        {
                                            failedRuns = 0;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    var trimmedResult = await TrimToolResultAsync(result);
                    _events.Add(AgentEventType.ToolResult, $"Tool result: {toolName}", result, toolName, string.IsNullOrWhiteSpace(input) ? null : input);
                    await _logger.LogAsync("tool_result", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        tool = toolName,
                        result = trimmedResult,
                        mode = "native_tools",
                        executedTool
                    });
                    transcript += string.IsNullOrWhiteSpace(input)
                        ? $"\n\nAssistant tool call:\n{toolName}\n\nTool result:\n{trimmedResult}\n"
                        : $"\n\nAssistant tool call:\n{{\"tool\":\"{toolName}\",\"input\":{JsonSerializer.Serialize(input)}}}\n\nTool result:\n{trimmedResult}\n"
                            + (analysis is null ? "" : $"\nCommand analysis:\n{analysis}\n")
                            + (BuildRecoveryGuidance(toolName, result) is { } recovery ? $"\nRecovery guidance:\n{recovery}\n" : "");
                    messages.Add(new LlmChatMessage("tool", trimmedResult, ToolName: toolName));
                    if (phaseDone)
                    {
                        break;
                    }
                }

                if (phaseDone)
                {
                    break;
                }
            }

            if (ShouldAdvanceGroundedDocumentationDiscoveryAfterBudget(phase, isLastPhase, phaseDone, _state.CurrentTaskKind, _state.GroundingEvidenceBytes, globalStep))
            {
                const string budgetAdvanceMessage = "Discovery hit its step budget after gathering enough documentation evidence. Advancing to the Editing phase.";
                _events.Add(AgentEventType.LlmResponse, "Phase auto-advance", budgetAdvanceMessage);
                await _logger.LogAsync("phase_force_advance", new
                {
                    phase = phase.ToString(),
                    step = globalStep,
                    reason = "discovery_budget_with_grounding",
                    mode = "native_tools"
                });
                phaseDone = true;
            }

            await _logger.LogAsync("phase_exit", new
            {
                phase = phase.ToString(),
                phaseDone,
                globalStep,
                mode = "native_tools"
            });
            if (!phaseDone && !isLastPhase && globalStep < _config.MaxAgentSteps)
            {
                var blocker = phase is AgentPhase.Editing
                    ? "Blocked: Editing phase exhausted its step budget before completing a required write. The agent will not advance until it makes the docs change and calls phase_complete."
                    : $"Blocked: {phase} phase exhausted its step budget before calling phase_complete. The agent will not advance to the next phase automatically.";
                _events.Add(AgentEventType.Final, "Final", blocker);
                await _logger.LogAsync("final", new
                {
                    text = blocker,
                    reason = "phase_incomplete",
                    phase = phase.ToString(),
                    mode = "native_tools"
                });
                Console.WriteLine(blocker);
                return;
            }
            if (phaseDone)
            {
                await AdvancePhaseTodoAsync(phase);
            }
            if (phase is AgentPhase.Editing && !phaseHadSuccessfulMutation)
            {
                var blocker = "Blocked: Editing phase ended without applying any file changes. The agent must call a mutation tool successfully before advancing.";
                _events.Add(AgentEventType.Final, "Final", blocker);
                await _logger.LogAsync("final", new
                {
                    text = blocker,
                    reason = "editing_no_mutation",
                    mode = "native_tools"
                });
                Console.WriteLine(blocker);
                return;
            }
            if (globalStep >= _config.MaxAgentSteps)
            {
                break;
            }
        }
        await SynthesizeFinalAsync(task, triage, transcript, mutationLog);
    }

    private async Task<string> BuildNativePhasePromptAsync(string task, TriageResult triage, AgentPhase phase, string priorTranscript, string currentTodo, string completedTodos)
    {
        var recentState = string.IsNullOrWhiteSpace(priorTranscript)
            ? (string.IsNullOrWhiteSpace(_session.ReadSummary()) ? "(none)" : await TrimRecentStateAsync(_session.ReadSummary()))
            : await TrimRecentStateAsync(priorTranscript);
        var thinkingHint = _config.EnableThinking && LlmClient.IsThinkingModel(_config.ModelName)
            ? "\nPreserve useful reasoning across tool calls and use it to decide the next action.\n"
            : "";
        return _prompts.Render(PromptId.PhaseAgentNative, new()
        {
            ["phase"] = phase.ToString(),
            ["phase_hint"] = PhaseToolPolicy.PhaseHint(phase) + thinkingHint,
            ["task"] = task,
            ["goal"] = triage.Goal,
            ["current_todo"] = currentTodo,
            ["completed_todos"] = completedTodos,
            ["tools"] = RenderToolsForPhase(phase),
            ["recent_state"] = recentState
        });
    }

    private async Task SeedPhaseTodosAsync(PhasePlan plan)
    {
        var todos = new List<TodoItem>();
        var id = 1;
        foreach (AgentPhase phase in plan.Phases)
        {
            todos.Add(new TodoItem(id, BuildPhaseTodoText(plan.Kind, phase), id == 1 ? "in_progress" : "pending"));
            id++;
        }
        if (todos.Count > 0)
        {
            await _todoStore.WriteAsync(todos);
        }
    }

    private static string BuildPhaseTodoText(TaskKind kind, AgentPhase phase)
    {
        return (kind, phase) switch
        {
            (TaskKind.ArchitectureDocs, AgentPhase.Discovery) => "Discovery phase: gather architecture evidence",
            (TaskKind.ArchitectureDocs, AgentPhase.Editing) => "Editing phase: write docs/ARCHITECTURE.md (apply_diff or create_file)",
            (TaskKind.ArchitectureDocs, AgentPhase.Verification) => "Verification phase: verify docs/ARCHITECTURE.md (read_file)",
            (TaskKind.Documentation, AgentPhase.Editing) => "Editing phase: write the docs markdown file (apply_diff or create_file)",
            (TaskKind.Documentation, AgentPhase.Verification) => "Verification phase: verify the docs markdown file (read_file)",
            _ => $"{phase} phase"
        };
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

    private static bool TryEnforceCurrentTodoTool(string currentTodo, string toolName, AgentPhase phase, TaskKind taskKind, bool phaseAttemptedMutation, out string nudge)
    {
        nudge = "";
        if (phase is AgentPhase.Editing
            && phaseAttemptedMutation
            && taskKind is TaskKind.ArchitectureDocs or TaskKind.Documentation)
        {
            return false;
        }

        if (!TryGetPinnedTodoTools(currentTodo, out IReadOnlyList<string> requiredTools)
            || requiredTools.Any(requiredTool => string.Equals(requiredTool, toolName, StringComparison.Ordinal)))
        {
            return false;
        }

        var requiredText = requiredTools.Count == 1 ? requiredTools[0] : string.Join(" or ", requiredTools);
        nudge = $"The active todo is '{currentTodo}', so the next tool MUST be {requiredText} (you chose {toolName}). Do not go back to an earlier completed-step tool. Return exactly one JSON tool call for {requiredTools[0]}.";
        return true;
    }

    private static bool TryGetPinnedTodoTools(string currentTodo, out IReadOnlyList<string> toolNames)
    {
        toolNames = [];
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
        if (string.IsNullOrWhiteSpace(marker))
        {
            return false;
        }

        var parsedTools = marker
            .Replace(" or ", ",", StringComparison.OrdinalIgnoreCase)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parsedTools.Length == 0)
        {
            return false;
        }

        toolNames = parsedTools;
        return true;
    }

    private async Task SynthesizeFinalAsync(string task, TriageResult triage, string transcript, List<string> mutationLog)
    {
        await _logger.LogAsync("synthesize_final", new
        {
            steps = _config.MaxAgentSteps,
            reason = "phases_completed_without_final",
            mutations = mutationLog
        });
        try
        {
            var summary = await _compactor.CompactAsync(transcript);
            var mutationsBlock = mutationLog.Count == 0
                ? "(none recorded)"
                : string.Join("\n", mutationLog.Select(m => $"- {m}"));
            var prompt = _prompts.Render(PromptId.FinalSynthesis, new()
            {
                ["task"] = task,
                ["task_kind"] = triage.Kind.ToString(),
                ["transcript"] = $"Confirmed successful file writes during this run:\n{mutationsBlock}\n\n{summary}"
            });
            var synthesized = await _llm.ChatAsync(prompt);
            var text = string.IsNullOrWhiteSpace(synthesized)
                ? $"Stopped after {_config.MaxAgentSteps} steps without producing a final answer."
                : synthesized.Trim();
            if (mutationLog.Count > 0 && LooksLikeDeniedFinal(text))
            {
                text = $"Completed: {task}\n\nConfirmed file writes:\n{mutationsBlock}";
            }
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

    private static bool LooksLikeDeniedFinal(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        var lower = text.ToLowerInvariant();
        return lower.Contains("i'm unable")
            || lower.Contains("i am unable")
            || lower.Contains("unable to generate")
            || lower.Contains("no data or steps mentioned")
            || lower.Contains("transcript only indicates")
            || lower.Contains("cannot generate")
            || lower.Contains("there is no data");
    }

    [GeneratedRegex(@"\*\*\* (?:Add|Update|Delete) File:\s*(\S+)")]
    private static partial Regex PatchPathRegex();

    [GeneratedRegex(@"(?:wrote|created|updated|renamed to|deleted)\s+([A-Za-z0-9_./-]+\.[A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MutationResultPathRegex();

    private static IEnumerable<string> ExtractMutationPaths(string toolName, string input, string result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(input))
        {
            var p = TryExtractPathInput(input);
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
            {
                yield return p;
            }
        }
        if (toolName is "apply_diff" or "apply_patch" && !string.IsNullOrWhiteSpace(input))
        {
            foreach (Match m in PatchPathRegex().Matches(input))
            {
                var p = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                {
                    yield return p;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(result))
        {
            foreach (Match m in MutationResultPathRegex().Matches(result))
            {
                var p = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                {
                    yield return p;
                }
            }
        }
    }

    private static string? BuildRecoveryGuidance(string toolName, string result)
    {
        return toolName switch
        {
            "phase_complete" when result.StartsWith("phase_complete rejected:", StringComparison.OrdinalIgnoreCase)
                => "Do not call phase_complete again until you satisfy the rejection reason. Gather more evidence in Discovery, write a file successfully in Editing, or finish the remaining required work for this phase first.",
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
                => "apply_diff requires a raw V4A patch string, NOT a JSON object. Do not use {\"file\":...,\"search\":...,\"replaceWith\":...}. Correct example for an in-place edit:\n*** Begin Patch\n*** Update File: Core/AgentLoop.cs\n@@\n- var phaseStep = 0;\n+ var stepInPhase = 0;\n*** End Patch\nThe '-' line must match the existing file byte-for-byte. Read the file first to get the exact line text, then retry with a real patch.",
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

    private static bool IsJsonObjectInput(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        var trimmed = input.TrimStart();
        return trimmed.Length > 0 && trimmed[0] == '{';
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
    private async Task<string> CompactTranscriptIfNeededAsync(string transcript, AgentPhase phase)
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
        return "Compacted summary:\n" + summary;
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

    private async Task<(string Result, bool PhaseDone)> ExecutePhaseCompleteToolAsync(ITool tool, string input, AgentPhase phase, int phaseStep, bool hasGroundingEvidence, bool phaseHadSuccessfulMutation, bool isLastToolCall)
    {
        string result;
        if (!isLastToolCall)
        {
            result = Record(tool, input, "phase_complete rejected: it must be the last tool call in the response. Finish all other tool calls first, then call phase_complete.", true);
            return (result, false);
        }

        if (phase is AgentPhase.Discovery && !hasGroundingEvidence)
        {
            result = Record(tool, input, "phase_complete rejected: gather grounding evidence with a read/search tool before advancing.", true);
            return (result, false);
        }

        if (phase is AgentPhase.Editing && !phaseHadSuccessfulMutation)
        {
            result = Record(tool, input, "phase_complete rejected: Editing requires at least one successful file mutation before advancing.", true);
            return (result, false);
        }

        try
        {
            Console.WriteLine($"Running: {tool.Name}");
            result = Record(tool, input, await tool.RunAsync(input), false);
        }
        catch (Exception ex)
        {
            result = Record(tool, input, $"phase_complete rejected: tool failed: {ex.Message}", true);
            return (result, false);
        }

        await _logger.LogAsync("phase_done_signal", new
        {
            phase = phase.ToString(),
            step = phaseStep,
            mode = "native_tools",
            via = "tool",
            summary = string.IsNullOrWhiteSpace(input) ? null : input
        });
        return (result, true);
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
        if (hasGroundingEvidence && _state.GroundingEvidenceBytes >= MinDocumentationEvidenceBytes)
        {
            return false;
        }
        failure = "Grounding required before writing documentation. Call code_map and at least one read tool (read_ranges, read_file, context_pack) on real repo files first; accumulated evidence must be at least "
            + MinDocumentationEvidenceBytes
            + " bytes. Do not retry the write yet.";
        return true;
    }

    private static bool TryBlockDocumentationEditingBeforeFirstWrite(AgentPhase phase, TaskKind taskKind, bool phaseAttemptedMutation, string toolName, out string failure)
    {
        failure = "";
        if (phase != AgentPhase.Editing
            || phaseAttemptedMutation
            || taskKind is not (TaskKind.ArchitectureDocs or TaskKind.Documentation))
        {
            return false;
        }

        if (toolName is "apply_diff" or "create_file")
        {
            return false;
        }

        failure = "Architecture/documentation tasks must start the Editing phase by writing the docs markdown file with apply_diff or create_file. Do not spend Editing steps on additional reads before the first write attempt.";
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
            || result.StartsWith("phase_complete rejected:", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("Tool failed:", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Denied by approval policy", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Approval denied.", StringComparison.OrdinalIgnoreCase)
            || result.Contains("Blocked by command policy.", StringComparison.Ordinal)
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

    private static bool ShouldForceDiscoveryAdvance(AgentPhase phase, TaskKind taskKind, LlmChatResponse response, int groundingEvidenceBytes)
    {
        return phase == AgentPhase.Discovery
            && taskKind is TaskKind.ArchitectureDocs or TaskKind.Documentation
            && groundingEvidenceBytes >= MinDocumentationEvidenceBytes
            && response.ToolCalls.Count != 0
            && !response.ToolCalls.Any(t => !IsDiscoveryTool(t.Name)) && SignalsReadyToWriteDocumentation(response.Content, response.Reasoning);
    }

    private bool ShouldAdvanceGroundedDocumentationDiscoveryAfterBudget(AgentPhase phase, bool isLastPhase, bool phaseDone, TaskKind taskKind, int groundingEvidenceBytes, int globalStep)
    {
        return phase == AgentPhase.Discovery
            && !isLastPhase
            && !phaseDone
            && taskKind is TaskKind.ArchitectureDocs or TaskKind.Documentation
            && groundingEvidenceBytes >= MinDocumentationEvidenceBytes
            && globalStep < _config.MaxAgentSteps;
    }

    private static bool SignalsReadyToWriteDocumentation(string? content, string? reasoning)
    {
        var text = string.Join("\n", [content ?? "", reasoning ?? ""]);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] evidenceSignals =
        [
            "have all the evidence i need",
            "have enough information",
            "all the information i need",
            "ready to create",
            "ready to write"
        ];
        string[] writeSignals =
        [
            "let me create",
            "let me now create",
            "let me write",
            "create the architecture.md file",
            "write the architecture.md file",
            "create the docs",
            "write the docs"
        ];

        return evidenceSignals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase))
            && writeSignals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));
    }

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

    private async Task<string> TrimRecentStateAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var candidate = TrimByChars(text, _config.MaxRecentStateChars, keepTail: true);
        if (_config.MaxRecentStateTokens <= 0)
        {
            return candidate;
        }

        (var Text, var _) = await TrimToTokenBudgetAsync(candidate, _config.MaxRecentStateTokens, keepTail: true);
        return Text;
    }

    private async Task<string> TrimToolResultAsync(string text)
    {
        const string marker = "\n[tool result truncated]";
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var candidate = text;
        var truncated = false;
        if (_config.MaxToolResultChars > 0 && candidate.Length > _config.MaxToolResultChars)
        {
            candidate = candidate[.._config.MaxToolResultChars];
            truncated = true;
        }

        if (_config.MaxToolResultTokens <= 0)
        {
            return truncated ? candidate + marker : candidate;
        }

        var candidateTokens = await _llm.CountPromptTokensAsync(candidate);
        if (!truncated && candidateTokens <= _config.MaxToolResultTokens)
        {
            return candidate;
        }

        var markerTokens = await _llm.CountPromptTokensAsync(marker);
        var contentBudget = Math.Max(0, _config.MaxToolResultTokens - markerTokens);

        (var Text, var _) = await TrimToTokenBudgetAsync(candidate, contentBudget, keepTail: false);
        return Text + marker;
    }

    private async Task<(string Text, bool Truncated)> TrimToTokenBudgetAsync(string text, int maxTokens, bool keepTail)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, false);
        }

        if (maxTokens <= 0)
        {
            return ("", text.Length > 0);
        }

        var tokenCount = await _llm.CountPromptTokensAsync(text);
        if (tokenCount <= maxTokens)
        {
            return (text, false);
        }

        var low = 0;
        var high = text.Length;
        var best = "";
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var candidate = Slice(text, mid, keepTail);
            var candidateTokens = await _llm.CountPromptTokensAsync(candidate);
            if (candidateTokens <= maxTokens)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return (best, true);
    }

    private static string TrimByChars(string text, int maxChars, bool keepTail) => maxChars <= 0 || text.Length <= maxChars ? text : keepTail ? text[^maxChars..] : text[..maxChars];

    private static string Slice(string text, int length, bool keepTail) => length <= 0 ? "" : length >= text.Length ? text : keepTail ? text[^length..] : text[..length];

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
