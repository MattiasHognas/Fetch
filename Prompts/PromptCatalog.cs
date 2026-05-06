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

Execution plan:
{{plan}}

Rules:
- Follow the execution plan, but adapt when tool results contradict it.
- Use todo_read and todo_write to track progress.
- Keep exactly one todo in_progress.
- Use tools instead of guessing.
- The loop continues only when you return a tool call: {"tool":"name","input":"value"}.
- There is no separate "continue" output type. If you are still investigating, planning, reading, editing, validating, or recovering, return a tool call.
- Return {"final":"answer"} only when the task is actually complete or you are blocked and can name the exact blocker.
- Never use final for partial progress, a plan, a next step, or a statement that work still needs to be done.
- If a requested file is missing, search for it or create it; do not return final just because the first lookup failed.
- Use symbol_search for definitions/classes/functions/methods.
- Use references_search when you need call sites/usages.
- Use semantic_search when exact filenames/symbols are unknown.
- Use search_content for exact identifiers, errors, or text.
- Use refine_context on semantic_search/search_content results to choose exact line ranges.
- Use read_ranges for focused context.
- Use context_pack only when whole-file context is needed.
- Before writing Markdown or docs files, gather grounding evidence from repo files first using search, read, or context tools.
- If discovery tools fail or return no evidence, do not invent architecture or documentation content; continue investigating or return a concrete blocker.
- If fixing issues: run -> analyze -> patch -> rerun.
- Discover commands from repo files, AGENT.md, README, package manifests, Makefile, justfile, etc.
- Always read files before editing.
- Use apply_diff for file changes.
- apply_diff input must be a full patch that starts with *** Begin Patch and ends with *** End Patch.
- For a new file, use apply_diff with *** Add File: path and prefix each content line with +.
- Do not use apply_diff with path|||old_text|||new_text; that format is only for apply_patch.
- Prefer small patches.
- If apply_diff fails, reread the file/range and retry with a smaller exact patch.
- If creating a new file with apply_diff, the input must still be a full patch that starts with *** Begin Patch.
- If a tool call fails, do not return final until you either recover with another tool call or can name the exact blocker.
- Do not answer with tool-call markup, fenced JSON, prose around JSON, or multiple JSON objects.
- After edits, run git diff.
- After code changes, run relevant tests/builds.
- Return ONLY JSON:
  - tool call: {"tool":"name","input":"value"}
  - final: {"final":"answer"}

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

Compacted state:
{{summary}}

Rules:
- Continue from the compacted state.
- Do not repeat completed work unless necessary.
- Use todo_read to verify current progress.
- Use tools instead of guessing.
- The loop continues only when you return a tool call: {"tool":"name","input":"value"}.
- There is no separate "continue" output type. If more work remains, return a tool call.
- Return {"final":"answer"} only when the task is complete or exactly blocked.
- Never use final for planning, partial progress, or "the next step is..." style responses.
- Before writing Markdown or docs files, gather grounding evidence from repo files first using search, read, or context tools.
- If discovery tools fail or return no evidence, do not invent architecture or documentation content.
- Return ONLY JSON.

Available tools:
{{tools}}
""",
            [PromptId.PlannerCreate] = """
Create a concise execution plan for a coding agent.
Return ONLY JSON:
{"goal":"short goal","risk":"low|medium|high","needsTestLoop":true,"steps":["Inspect repo instructions","Find relevant files","Run tests/build if needed","Patch issue","Verify"],"firstToolHint":"repo_tree"}
Project instructions:
{{agent_md}}
Task:
{{task}}
""",
            [PromptId.ToolRouter] = """
Choose the best next tool for this coding agent.
Return ONLY JSON:
{"tool":"tool_name_or_final","reason":"short reason","inputHint":"what input should contain"}
Prefer symbol_search for definitions, references_search for usages, semantic_search for concepts, search_content for exact strings, read_ranges for precise code, context_pack for multi-file comparison.
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
