using System.Text.Json;

namespace Fetch.Planning;

public sealed record TriageResult(TaskKind Kind, PhasePlan Plan, string Goal, bool NeedsTests, string LlmRaw);

public sealed class TriageFailureException(string message, string rawResponse) : Exception(message)
{
    public string RawResponse { get; } = rawResponse;
}

/// <summary>
/// Runs a single small-context LLM call to classify the task and pick the ordered list of phases.
/// The model must return a complete, valid triage payload; invalid or incomplete triage blocks the run.
/// </summary>
public sealed class TriageRunner(LlmClient llm, PromptCatalog prompts, AgentConfig config, PathSandbox sandbox)
{
    private readonly LlmClient _llm = llm;
    private readonly PromptCatalog _prompts = prompts;
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;

    public async Task<TriageResult> RunAsync(string task, string? agentMd)
    {
        RepoSnapshot snapshot = ProbeRepo();
        var prompt = _prompts.Render(PromptId.Triage, new()
        {
            ["task"] = task,
            ["agent_md"] = agentMd ?? "",
            ["repo_snapshot"] = snapshot.Description
        });
        var raw = await _llm.ChatAsync(prompt);
        return ParseOrThrow(raw);
    }

    private static TriageResult ParseOrThrow(string raw)
    {
        if (!JsonHelper.TryParseObject(raw, out JsonDocument? doc, out _) || doc is null)
        {
            throw new TriageFailureException("The model returned non-JSON triage output. Expected a single JSON object with kind, phases, isGreenfield, goal, and needsTests.", raw);
        }

        try
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TriageFailureException("The triage response must be a JSON object.", raw);
            }

            if (!TryGetRequiredString(root, "kind", out var kindStr)
                || !Enum.TryParse(kindStr, ignoreCase: true, out TaskKind kind))
            {
                var allowedKinds = string.Join(", ", Enum.GetNames<TaskKind>());
                var invalidKind = string.IsNullOrWhiteSpace(kindStr) ? "(missing)" : kindStr;
                throw new TriageFailureException($"Invalid triage kind '{invalidKind}'. Allowed kinds: {allowedKinds}.", raw);
            }

            if (!TryGetRequiredPhases(root, out List<AgentPhase> phases, out var phasesError))
            {
                throw new TriageFailureException(phasesError, raw);
            }

            if (!TryGetRequiredBoolean(root, "isGreenfield", out var isGreenfield))
            {
                throw new TriageFailureException("Missing or invalid triage field 'isGreenfield'. Expected true or false.", raw);
            }

            if (!TryGetRequiredString(root, "goal", out var goal) || string.IsNullOrWhiteSpace(goal))
            {
                throw new TriageFailureException("Missing or invalid triage field 'goal'. Expected a short non-empty string.", raw);
            }

            if (!TryGetRequiredBoolean(root, "needsTests", out var needsTests))
            {
                throw new TriageFailureException("Missing or invalid triage field 'needsTests'. Expected true or false.", raw);
            }

            var trimmedGoal = goal.Trim();
            return new TriageResult(kind, new PhasePlan(kind, phases, isGreenfield, trimmedGoal), trimmedGoal, needsTests, raw);
        }
        finally
        {
            doc.Dispose();
        }
    }

    private static bool TryGetRequiredString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetRequiredBoolean(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool TryGetRequiredPhases(JsonElement root, out List<AgentPhase> phases, out string error)
    {
        phases = [];
        error = "";
        if (!root.TryGetProperty("phases", out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            error = "Missing or invalid triage field 'phases'. Expected a non-empty array of phase names.";
            return false;
        }

        var parsed = new List<AgentPhase>();
        var seen = new HashSet<AgentPhase>();
        foreach (JsonElement element in property.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                error = "Invalid triage field 'phases'. Every phase must be a string name.";
                return false;
            }

            var phaseName = element.GetString();
            if (!Enum.TryParse(phaseName, ignoreCase: true, out AgentPhase phase) || phase == AgentPhase.Triage)
            {
                error = $"Invalid triage phase '{phaseName ?? "(missing)"}'.";
                return false;
            }

            if (!seen.Add(phase))
            {
                error = $"Duplicate triage phase '{phase}'. Return each phase at most once.";
                return false;
            }

            parsed.Add(phase);
        }

        if (parsed.Count == 0)
        {
            error = "Missing or invalid triage field 'phases'. Expected a non-empty array of phase names.";
            return false;
        }

        phases = parsed;
        return true;
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
            (var dir, var depth) = stack.Pop();
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
