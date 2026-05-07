namespace Fetch.Prompts;

public sealed class PromptCatalog
{
    private readonly AgentConfig _config;
    private readonly Dictionary<string, string> _defaults;

    public PromptCatalog(AgentConfig config)
    {
        _config = config;
        _defaults = new()
        {
            [PromptId.AgentInitial] = """
You are a local coding agent.

Project instructions:
{{agent_md}}

Previous session summary:
{{previous_summary}}

Recent session log:
{{recent_log}}

Task kind: {{task_kind}}
Playbook hint: {{playbook_hint}}
Playbook steps:
{{playbook_steps}}
Required first tool: {{required_first_tool}}

Execution plan (LLM-generated, advisory):
{{plan}}

Semantic search status:
{{semantic_search_status}}

Tool decision table (pick ONE per step):
- Architecture / overview / refactor / docs about the repo: code_map FIRST, then read_ranges on specific files, then apply_diff.
- Bug fix / focused change: search_content or symbol_search to locate, read_ranges to confirm, apply_diff, run_command to verify.
- Question about the codebase: search_content or symbol_search, read_ranges, then final.
- Need broad concept search and semantic_search status is "ready": semantic_search, then refine_context or read_ranges.

Hard rules:
- Return ONLY one JSON object: {"tool":"name","input":"value"} OR {"final":"answer"}. No prose, no fences, no multiple JSON objects.
- The loop continues only when you return a tool call. There is no separate "continue" output.
- Return {"final":"..."} only when the task is actually complete or you can name the exact blocker. Never use final for "next step" or "I will now" content.
- If a write tool is blocked for missing grounding evidence, your next call MUST be a read/search tool (code_map, read_ranges, read_file, search_content, semantic_search, symbol_search, references_search, context_pack). Do not retry the write.
- Never repeat the exact same tool call after it failed. Change the tool or the input.
- If semantic_search reports the index is missing, do not call it again in this run.
- read_ranges input: [{"file":"path/to/file.cs","start":1,"end":80}] or {"file":"path/to/file.cs","start":1,"end":80}.
- Example paths are illustrative only. Use actual files discovered from code_map, search results, or prior reads; do not default to Program.cs unless it is clearly relevant.
- apply_diff input MUST be a real patch starting with *** Begin Patch and ending with *** End Patch.
- For a new file, use *** Add File: path and prefix each content line with +.
- Always read concrete files before writing documentation; do not invent class names, methods, or structure.
- After meaningful code changes, run the relevant build/test via run_command.

Available tools:
{{tools}}

Task:
{{task}}
""",
            [PromptId.AgentCompacted] = """
You are a local coding agent continuing a previous run.

Project instructions:
{{agent_md}}

Original task:
{{task}}

Task kind: {{task_kind}}
Playbook hint: {{playbook_hint}}
Playbook steps:
{{playbook_steps}}

Compacted state:
{{summary}}

Semantic search status:
{{semantic_search_status}}

Hard rules:
- Continue from the compacted state. Do not redo completed work.
- Return ONLY one JSON object: {"tool":"name","input":"value"} OR {"final":"answer"}.
- Use code_map first if you have not yet established repo structure for an architecture/refactor/docs task.
- Before writing docs/markdown, ensure you have read concrete files.
- Never repeat a failed tool call with identical input.
- If semantic_search reported the index is missing, do not retry it.

Available tools:
{{tools}}
""",
            [PromptId.PlannerCreate] = """
Refine the goal statement for a coding agent. The execution steps are FIXED by the playbook below; you do not need to invent them.
Return ONLY JSON: {"goal":"short concrete goal","risk":"low|medium|high","needsTestLoop":true,"firstToolHint":"{{required_first_tool}}"}
Task kind: {{task_kind}}
Playbook hint: {{playbook_hint}}
Playbook steps:
{{playbook_steps}}
Project instructions:
{{agent_md}}
Task:
{{task}}
""",
            [PromptId.ToolRouter] = """
Choose the best next tool for this coding agent.
Return ONLY JSON: {"tool":"tool_name_or_final","reason":"short reason","inputHint":"what input should contain"}

Task kind: {{task_kind}}
Playbook hint: {{playbook_hint}}
Required first tool (if step==0): {{required_first_tool}}
Semantic search status: {{semantic_search_status}}
Current todo: {{current_todo}}
Completed todos: {{completed_todos}}

Decision table:
- task_kind in {ArchitectureDocs, Documentation, Refactor} AND step==0: return code_map.
- If current todo names a specific tool, prefer that tool over earlier completed-step tools.
- If current todo is a docs/code write step and recent state shows enough grounding evidence, prefer apply_diff.
- Need exact identifier or text: search_content.
- Need definitions/types: symbol_search.
- Need usages: references_search.
- Have search results, need precise lines: read_ranges or refine_context.
- Need multi-file overview: code_map (preferred) or context_pack.
- Concept search and semantic_search status is "ready": semantic_search.
- Have enough evidence and writing docs/code: apply_diff.
- After meaningful code edit: run_command (build/test).

Hard rules:
- Return exactly one tool choice.
- If recent state says "Semantic index missing", do not choose semantic_search.
- If recent state says grounding is required, do not choose apply_diff/apply_patch/create_file. Choose a read/search tool.
- If the previous tool call failed, do not route to the exact same tool with the exact same input.
- If a successful code_map result is in recent state, prefer read_ranges next over re-running code_map.
- Do not route back to a completed-step tool unless the new input is narrower and clearly different.
- If current todo is "Draft and write... (apply_diff)", do not route to code_map or repeat the same read_ranges call.

Important input shapes:
- read_ranges: [{"file":"path/to/file.cs","start":1,"end":80}] or {"file":"path/to/file.cs","start":1,"end":80}
- apply_diff add file: *** Begin Patch\n*** Add File: docs/ARCHITECTURE.md\n+line 1\n*** End Patch
- code_map: empty string for whole repo, or {"path":"Tools","include":"*.cs"} to scope.

Available tools:
{{tools}}
Task:
{{task}}
Recent state:
{{recent_state}}
""",
            [PromptId.CommandAnalyze] = """
Analyze this command result for a coding agent.
Return ONLY JSON:
{"passed":true,"failureKind":"none|build|test|runtime|lint|dependency|unknown","summary":"short summary","likelyFiles":["path1"],"nextAction":"what the agent should do next"}
Command result:
{{command_result}}
""",
            [PromptId.TranscriptCompact] = """
Summarize this coding-agent transcript for continuation.
Return ONLY plain text with these sections:
Current goal:
Completed work:
Files inspected:
Files changed:
Commands run:
Test/build status:
Open todos:
Important constraints:
Next best step:
Rules:
- Use only facts explicitly present in the transcript.
- Do not claim a task is completed unless the transcript shows a successful final result or successful file edits that satisfy the task.
- Preserve blockers, repeated failed tool calls, invalid JSON/prose responses, and loop-prevention errors under Important constraints or Open todos.
- Files changed must be empty or "None" unless a create/edit/delete tool actually succeeded.
- If the run is stuck, Next best step should describe the concrete recovery action, not a generic summary.
Transcript:
{{transcript}}
""",
            [PromptId.ContextRefine] = """
Select the smallest useful file line ranges for this coding task.
Return ONLY JSON:
[{"file":"path","start":1,"end":80}]
Rules:
- Prefer 3-8 ranges.
- Include tests and implementation when relevant.
- Keep ranges tight but sufficient.
- Do not invent files.
- Use only files present in search results.
Task:
{{task}}
Search results:
{{search_results}}
""",
            [PromptId.FinalSynthesis] = """
The agent loop ran out of steps before producing a final answer.
Synthesize the best possible final answer for the user using ONLY evidence already in the transcript below.
Return ONLY plain text. Be honest if work is incomplete.

Task:
{{task}}

Task kind: {{task_kind}}

Transcript:
{{transcript}}
"""
        };
    }

    public string Render(string id, Dictionary<string, string> values)
    {
        var template = LoadTemplate(id);
        foreach ((var key, var value) in values)
        {
            template = template.Replace("{{" + key + "}}", value ?? "");
        }

        return template;
    }

    private string LoadTemplate(string id)
    {
        return _config.PromptOverrides.TryGetValue(id, out var path) && File.Exists(path)
            ? File.ReadAllText(path)
            : _defaults.TryGetValue(id, out var prompt) ? prompt : throw new InvalidOperationException($"Unknown prompt id: {id}");
    }

    public void WriteDefaults()
    {
        _ = Directory.CreateDirectory(_config.PromptRoot);
        foreach ((var id, var prompt) in _defaults)
        {
            var path = Path.Combine(_config.PromptRoot, id + ".txt");
            if (!File.Exists(path))
            {
                File.WriteAllText(path, prompt);
            }
        }
    }
}
