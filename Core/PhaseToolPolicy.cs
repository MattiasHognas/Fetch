namespace Fetch.Core;

/// <summary>
/// Hard-gates which tools the LLM may call in each phase. Out-of-phase calls are rejected by the loop
/// and the unavailable tools are not even rendered into the per-step prompt, which is the main mechanism
/// keeping the agent inside a 16k context window.
/// </summary>
public static class PhaseToolPolicy
{
    private static readonly Dictionary<AgentPhase, HashSet<string>> Allowed = new()
    {
        [AgentPhase.Triage] = new(StringComparer.Ordinal),
        [AgentPhase.Discovery] = new(StringComparer.Ordinal)
        {
            "code_map", "repo_tree", "list_files", "search", "search_content",
            "symbol_search", "references_search", "semantic_search", "relationship_map",
            "read_ranges", "read_file", "context_pack", "get_file_summary", "refine_context",
            "todo_read"
        },
        [AgentPhase.Planning] = new(StringComparer.Ordinal)
        {
            "todo_write", "todo_read"
        },
        [AgentPhase.Editing] = new(StringComparer.Ordinal)
        {
            "apply_diff", "apply_patch", "create_file", "delete_file", "rename_file",
            "read_file", "read_ranges", "todo_write", "todo_read"
        },
        [AgentPhase.Verification] = new(StringComparer.Ordinal)
        {
            "run_command", "read_file", "read_ranges", "todo_write", "todo_read"
        },
        [AgentPhase.Answering] = new(StringComparer.Ordinal)
        {
            "search_content", "symbol_search", "read_ranges", "read_file", "semantic_search",
            "todo_read"
        }
    };

    public static bool IsAllowed(string toolName, AgentPhase phase) =>
        Allowed.TryGetValue(phase, out HashSet<string>? set) && set.Contains(toolName);

    public static IEnumerable<ITool> Filter(IEnumerable<ITool> tools, AgentPhase phase) =>
        Allowed.TryGetValue(phase, out HashSet<string>? set)
            ? tools.Where(t => set.Contains(t.Name))
            : [];

    public static IReadOnlyCollection<string> AllowedToolNames(AgentPhase phase) =>
        Allowed.TryGetValue(phase, out HashSet<string>? set) ? set : Array.Empty<string>();

    public static string PhaseHint(AgentPhase phase) => phase switch
    {
        AgentPhase.Discovery =>
            "Discovery phase: gather concrete repo evidence. Use code_map/read_ranges/search_content/symbol_search. Do NOT write files.",
        AgentPhase.Planning =>
            "Planning phase: turn evidence into a concrete todo list with todo_write. Each todo should name the tool that will satisfy it, e.g. 'Read AgentLoop.cs (read_ranges)'. Then return phaseDone.",
        AgentPhase.Editing =>
            "Editing phase: apply focused patches (apply_diff/apply_patch) or create files. Read first, write second. Do NOT run commands or do new exploration here.\n" +
            "For architecture/documentation tasks, the first Editing-phase tool call must be apply_diff or create_file for the docs target. If that first write attempt fails, you may read the target file before retrying.\n" +
            "You MUST attempt apply_diff (or another mutation tool) for the current todo before returning {\"phaseDone\":true}. phaseDone with no successful mutation will be rejected.\n" +
            "apply_diff input MUST be a raw V4A patch string. Example:\n" +
            "*** Begin Patch\n" +
            "*** Update File: path/to/File.cs\n" +
            "@@\n" +
            "-old line exactly as in file\n" +
            "+new line\n" +
            "*** End Patch\n" +
            "Rules: include 1-3 unchanged context lines around each hunk; '-' lines must match the file byte-for-byte; do not wrap the patch in JSON, quotes, or markdown fences.",

        AgentPhase.Verification =>
            "Verification phase: run the narrowest relevant build/test/inspection via run_command and verify the change. Do NOT edit files here.",
        AgentPhase.Answering =>
            "Answering phase: synthesize a final answer that cites concrete file paths. Use read_ranges to confirm specifics. End with {\"final\":\"...\"}.",
        AgentPhase.Triage => throw new NotImplementedException(),
        _ => ""
    };
}
