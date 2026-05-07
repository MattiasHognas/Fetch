namespace Fetch.Planning;

public enum TaskKind
{
    Generic,
    ArchitectureDocs,
    Documentation,
    Refactor,
    BugFix,
    Feature,
    Question
}

public sealed record Playbook(
    TaskKind Kind,
    string? RequiredFirstTool,
    IReadOnlyList<string> Steps,
    string Hint);

/// <summary>
/// Deterministic, keyword-based classifier. Runs locally before the LLM planner so weak local models
/// can rely on a fixed playbook instead of inventing a plan.
/// </summary>
public static class TaskClassifier
{
    public static TaskKind Classify(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return TaskKind.Generic;
        }

        var t = task.ToLowerInvariant();

        return Mentions(t, "architecture", "class diagram", "mermaid", "overview of the codebase", "high-level design", "describe the codebase", "describe the structure", "structure of the app", "structure of the project", "system design", "high level architecture")
            || (Mentions(t, "architecture") && Mentions(t, ".md", "docs", "describe", "diagram", "explain"))
            ? TaskKind.ArchitectureDocs
            : Mentions(t, "readme", "changelog", "release notes", "documentation", "document the", "write docs", "doc comment")
            || (Mentions(t, "docs/") && Mentions(t, "write", "create", "add"))
            ? TaskKind.Documentation
            : Mentions(t, "refactor", "rename ", "extract method", "extract class", "split ", "reorganize", "restructure", "clean up")
            ? TaskKind.Refactor
            : Mentions(t, "bug", "fix ", "broken", "regression", "crash", "exception", "error in ", "stack trace", "doesn't work", "does not work", "failing test", "fails to ")
            ? TaskKind.BugFix
            : Mentions(t, "implement ", "add support for", "add a ", "add an ", "build a ", "build an ", "create a ", "create an ", "introduce ", "new feature", "new tool", "new command")
            ? TaskKind.Feature
            : Mentions(t, "what is", "what does", "how does", "where is", "explain how", "why does", "tell me about")
            ? TaskKind.Question
            : TaskKind.Generic;
    }

    public static Playbook GetPlaybook(TaskKind kind)
    {
        return kind switch
        {
            TaskKind.ArchitectureDocs => new Playbook(
                kind,
                RequiredFirstTool: "code_map",
                Steps:
                [
                    "Get a code map of the repository (code_map)",
                    "Read 3-6 anchor files identified in the code map (read_ranges)",
                    "Map semantic relationships between the anchor files (relationship_map)",
                    "Draft and write the documentation file (apply_diff)",
                    "Verify the file was created (read_file)"
                ],
                Hint: "Architecture/overview task: ground in code_map output, then build relationship edges between anchor files before drafting any document; only call apply_diff after reading concrete files."),
            TaskKind.Documentation => new Playbook(
                kind,
                RequiredFirstTool: "code_map",
                Steps:
                [
                    "Get a code map of the repository (code_map)",
                    "Read the relevant files in detail (read_ranges or read_file)",
                    "Draft and write the documentation (apply_diff)",
                    "Verify the file was written (read_file)"
                ],
                Hint: "Documentation task: gather concrete evidence from the repo before writing."),
            TaskKind.Refactor => new Playbook(
                kind,
                RequiredFirstTool: "code_map",
                Steps:
                [
                    "Get a code map to locate affected types (code_map)",
                    "Read the target files (read_ranges)",
                    "Find all call sites (references_search)",
                    "Apply focused patches (apply_diff)",
                    "Build to verify (run_command)"
                ],
                Hint: "Refactor task: find every call site before editing; build after each meaningful change."),
            TaskKind.BugFix => new Playbook(
                kind,
                RequiredFirstTool: null,
                Steps:
                [
                    "Reproduce or read the failing area (search_content or read_ranges)",
                    "Read suspected files (read_ranges)",
                    "Apply a focused patch (apply_diff)",
                    "Run the relevant test or build (run_command)"
                ],
                Hint: "Bug fix: isolate the failing call path before editing."),
            TaskKind.Feature => new Playbook(
                kind,
                RequiredFirstTool: null,
                Steps:
                [
                    "Get a code map to find the right insertion point (code_map)",
                    "Read related files (read_ranges)",
                    "Add or modify code (apply_diff)",
                    "Build/test to verify (run_command)"
                ],
                Hint: "New feature: place code consistently with existing structure visible in code_map."),
            TaskKind.Question => new Playbook(
                kind,
                RequiredFirstTool: null,
                Steps:
                [
                    "Search for the topic (search_content or symbol_search)",
                    "Read the relevant files (read_ranges)",
                    "Return a final answer that cites file paths"
                ],
                Hint: "Question: answer from concrete file evidence, not memory."),
            TaskKind.Generic => throw new NotImplementedException(),
            _ => new Playbook(
                            kind,
                            RequiredFirstTool: null,
                            Steps:
                            [
                                "Inspect repo instructions",
                    "Find relevant files",
                    "Make a focused change if needed",
                    "Verify"
                            ],
                            Hint: "Generic task: keep changes small and verify immediately.")
        };
    }

    private static bool Mentions(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}
