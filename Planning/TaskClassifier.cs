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

/// <summary>
/// Deterministic, keyword-based classifier. Used as the fallback when LLM triage returns invalid JSON.
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

    private static bool Mentions(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));
}
