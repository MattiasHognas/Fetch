using System.Text.Json;

namespace Fetch.Core;

public sealed partial class AgentLoop
{
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

            transcript = await CompactTranscriptIfNeededAsync(task, transcript, phase);
            (var currentTodo, var completedTodos) = await GetTodoRoutingStateAsync();
            ITool[] phaseTools = [.. PhaseToolPolicy.Filter(_tools.Values, phase)];
            IReadOnlyList<NativeToolDefinition> toolDefinitions = ToolSchemaRegistry.BuildDefinitions(phaseTools);
            var messages = new List<LlmChatMessage>
            {
                new("system", BuildNativePhasePrompt(task, triage, phase, transcript, currentTodo, completedTodos)),
                new("user", $"Continue the {phase} phase for the current task. Use tools when needed. Reply with PHASE_DONE only when this phase is complete.")
            };

            var repeatedBlockedToolCalls = 0;
            var phaseDone = false;
            var phaseHadSuccessfulMutation = false;

            for (var phaseStep = 0; phaseStep < _config.MaxStepsPerPhase && globalStep < _config.MaxAgentSteps; phaseStep++, globalStep++)
            {
                LlmChatResponse response = await _llm.ChatWithToolsAsync(messages, toolDefinitions);
                if (!string.IsNullOrWhiteSpace(response.Reasoning))
                {
                    _events.Add(AgentEventType.Reasoning, $"Reasoning: {phase}", response.Reasoning);
                    await _logger.LogAsync("llm_reasoning", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        reasoning = response.Reasoning
                    });
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

                if (response.ToolCalls.Count == 0)
                {
                    var content = response.Content.Trim();
                    if (string.Equals(content, "PHASE_DONE", StringComparison.Ordinal))
                    {
                        if (phase is AgentPhase.Discovery && !hasGroundingEvidence)
                        {
                            messages.Add(new LlmChatMessage("user", "PHASE_DONE rejected: gather grounding evidence with a read/search tool before advancing."));
                            continue;
                        }

                        if (phase is AgentPhase.Editing && !phaseHadSuccessfulMutation)
                        {
                            messages.Add(new LlmChatMessage("user", "PHASE_DONE rejected: Editing requires at least one successful file mutation before advancing."));
                            continue;
                        }

                        await _logger.LogAsync("phase_done_signal", new
                        {
                            phase = phase.ToString(),
                            step = phaseStep,
                            mode = "native_tools"
                        });
                        phaseDone = true;
                        break;
                    }

                    var canFinal = isLastPhase || phase is AgentPhase.Answering || phase is AgentPhase.Verification;
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
                        : "If work remains in this phase, call a tool. Reply with PHASE_DONE only when the phase is actually complete."));
                    continue;
                }

                foreach (LlmToolCall toolCall in response.ToolCalls)
                {
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
                    else if (TryEnforceCurrentTodoTool(currentTodo, toolName, out var currentTodoNudge))
                    {
                        result = currentTodoNudge;
                    }
                    else if (!NativeToolRegistry.TryGetTool(toolName, out ITool? tool) || tool is null)
                    {
                        result = $"Unknown tool: {toolName}\nAvailable tools (this phase):\n{RenderToolsForPhase(phase)}";
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
                        else if (toolName is "apply_diff" or "apply_patch" && IsJsonObjectInput(input))
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

                    _events.Add(AgentEventType.ToolResult, $"Tool result: {toolName}", result, toolName, string.IsNullOrWhiteSpace(input) ? null : input);
                    await _logger.LogAsync("tool_result", new
                    {
                        phase = phase.ToString(),
                        step = phaseStep,
                        tool = toolName,
                        result = TrimToolResult(result),
                        mode = "native_tools",
                        executedTool
                    });
                    transcript += string.IsNullOrWhiteSpace(input)
                        ? $"\n\nAssistant tool call:\n{toolName}\n\nTool result:\n{TrimToolResult(result)}\n"
                        : $"\n\nAssistant tool call:\n{{\"tool\":\"{toolName}\",\"input\":{JsonSerializer.Serialize(input)}}}\n\nTool result:\n{TrimToolResult(result)}\n"
                            + (analysis is null ? "" : $"\nCommand analysis:\n{analysis}\n")
                            + (BuildRecoveryGuidance(toolName, result) is { } recovery ? $"\nRecovery guidance:\n{recovery}\n" : "");
                    messages.Add(new LlmChatMessage("tool", result, ToolName: toolName));
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

            await _logger.LogAsync("phase_exit", new
            {
                phase = phase.ToString(),
                phaseDone,
                globalStep,
                mode = "native_tools"
            });
            await AdvancePhaseTodoAsync(phase);
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

    private string BuildNativePhasePrompt(string task, TriageResult triage, AgentPhase phase, string priorTranscript, string currentTodo, string completedTodos)
    {
        var recentState = string.IsNullOrWhiteSpace(priorTranscript)
            ? (string.IsNullOrWhiteSpace(_session.ReadSummary()) ? "(none)" : Trim(_session.ReadSummary(), _config.MaxRecentStateChars))
            : Trim(priorTranscript, _config.MaxRecentStateChars);
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
}
