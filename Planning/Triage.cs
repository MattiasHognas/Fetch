using System.Text.Json;

namespace Fetch.Planning;

public sealed record TriageResult(TaskKind Kind, PhasePlan Plan, string Goal, bool NeedsTests, string LlmRaw);

/// <summary>
/// Runs a single small-context LLM call to classify the task and pick the ordered list of phases.
/// Falls back to the deterministic keyword classifier when the LLM returns invalid JSON.
/// </summary>
public sealed class TriageRunner(LlmClient llm, PromptCatalog prompts, AgentConfig config, PathSandbox sandbox)
{
    private readonly LlmClient _llm = llm;
    private readonly PromptCatalog _prompts = prompts;
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;

    public async Task<TriageResult> RunAsync(string task, string? agentMd)
    {
        var snapshot = ProbeRepo();
        var prompt = _prompts.Render(PromptId.Triage, new()
        {
            ["task"] = task,
            ["agent_md"] = agentMd ?? "",
            ["repo_snapshot"] = snapshot.Description
        });
        var raw = await _llm.ChatAsync(prompt);
        return TryParse(raw, snapshot, out var parsed)
            ? parsed
            : Fallback(task, snapshot, raw);
    }

    private static bool TryParse(string raw, RepoSnapshot snapshot, out TriageResult result)
    {
        result = null!;
        if (!JsonHelper.TryParseObject(raw, out var doc, out _) || doc is null)
        {
            return false;
        }
        try
        {
            var root = doc.RootElement;
            var kindStr = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "Generic" : "Generic";
            if (!Enum.TryParse<TaskKind>(kindStr, ignoreCase: true, out var kind))
            {
                kind = TaskKind.Generic;
            }
            var phases = new List<AgentPhase>();
            if (root.TryGetProperty("phases", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in p.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && Enum.TryParse<AgentPhase>(el.GetString(), true, out var ph) && ph != AgentPhase.Triage)
                    {
                        phases.Add(ph);
                    }
                }
            }
            var isGreenfield = (root.TryGetProperty("isGreenfield", out var g) && g.ValueKind == JsonValueKind.True) || snapshot.IsGreenfield;
            var goal = root.TryGetProperty("goal", out var gl) ? gl.GetString() ?? "" : "";
            var needsTests = !root.TryGetProperty("needsTests", out var nt) || nt.ValueKind != JsonValueKind.False;
            if (phases.Count == 0)
            {
                phases = DefaultPhases(kind, isGreenfield);
            }
            result = new TriageResult(kind, new PhasePlan(kind, phases, isGreenfield, goal), goal, needsTests, raw);
            return true;
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static TriageResult Fallback(string task, RepoSnapshot snapshot, string raw)
    {
        var kind = TaskClassifier.Classify(task);
        var phases = DefaultPhases(kind, snapshot.IsGreenfield);
        return new TriageResult(kind, new PhasePlan(kind, phases, snapshot.IsGreenfield, task), task, true, raw);
    }

    private static List<AgentPhase> DefaultPhases(TaskKind kind, bool isGreenfield)
    {
        if (isGreenfield)
        {
            return [AgentPhase.Planning, AgentPhase.Editing, AgentPhase.Verification];
        }
        return kind switch
        {
            TaskKind.Question => [AgentPhase.Discovery, AgentPhase.Answering],
            TaskKind.ArchitectureDocs or TaskKind.Documentation =>
                [AgentPhase.Discovery, AgentPhase.Editing, AgentPhase.Verification],
            TaskKind.BugFix or TaskKind.Refactor or TaskKind.Feature =>
                [AgentPhase.Discovery, AgentPhase.Planning, AgentPhase.Editing, AgentPhase.Verification],
            _ => [AgentPhase.Discovery, AgentPhase.Planning, AgentPhase.Editing, AgentPhase.Verification]
        };
    }

    private RepoSnapshot ProbeRepo()
    {
        try
        {
            var root = _sandbox.Root;
            var skip = new HashSet<string>(_config.AlwaysIgnoredPaths, StringComparer.OrdinalIgnoreCase);
            string[] codeExt = [".cs", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs", ".py", ".java", ".rb", ".cpp", ".c", ".h", ".hpp"];
            var sourceCount = 0;
            var topLevel = new List<string>();
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var name = Path.GetFileName(path);
                if (skip.Contains(name) || name.StartsWith('.'))
                {
                    continue;
                }
                topLevel.Add(name + (Directory.Exists(path) ? "/" : ""));
            }
            foreach (var path in EnumerateFilesShallow(root, skip, maxDepth: 3))
            {
                var ext = Path.GetExtension(path);
                if (codeExt.Contains(ext, StringComparer.OrdinalIgnoreCase))
                {
                    sourceCount++;
                    if (sourceCount > 8)
                    {
                        break;
                    }
                }
            }
            var isGreenfield = sourceCount <= 3;
            var top = string.Join(", ", topLevel.Take(20));
            var description = $"sourceFiles~={(sourceCount > 8 ? ">8" : sourceCount.ToString(System.Globalization.CultureInfo.InvariantCulture))}, isGreenfield={isGreenfield.ToString().ToLowerInvariant()}, topLevel=[{top}]";
            return new RepoSnapshot(isGreenfield, description);
        }
        catch
        {
            return new RepoSnapshot(false, "(repo probe failed)");
        }
    }

    private static IEnumerable<string> EnumerateFilesShallow(string root, HashSet<string> skip, int maxDepth)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            string[] subs;
            try
            {
                files = Directory.GetFiles(dir);
                subs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }
            foreach (var f in files)
            {
                yield return f;
            }
            if (depth >= maxDepth)
            {
                continue;
            }
            foreach (var s in subs)
            {
                var name = Path.GetFileName(s);
                if (skip.Contains(name) || name.StartsWith('.'))
                {
                    continue;
                }
                stack.Push((s, depth + 1));
            }
        }
    }

    private sealed record RepoSnapshot(bool IsGreenfield, string Description);
}
