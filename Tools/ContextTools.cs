using System.Text.Json;

namespace Fetch.Tools;

public sealed class ContextPackTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets, AgentConfig config) : ITool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets; private readonly AgentConfig _config = config;

    public string Name => "context_pack"; public string Description => "Pack multiple files into one bounded context. Input: one repo-relative path per line."; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "No files requested.";
        }
        if (input.Contains("|||", StringComparison.Ordinal))
        {
            return "Invalid input. context_pack expects one repo-relative path per line, not path|||content. To create or edit a file, use apply_diff.";
        }
        if (input.Contains("```", StringComparison.Ordinal))
        {
            return "Invalid input. context_pack does not accept fenced code blocks. Provide one repo-relative path per line.";
        }

        var rawLines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var paths = new List<string>();
        var rejected = new List<string>();
        foreach (var line in rawLines)
        {
            if (LooksLikePath(line))
            {
                if (!paths.Contains(line))
                {
                    paths.Add(line);
                }
            }
            else
            {
                rejected.Add(line);
            }
            if (paths.Count >= _config.MaxContextPackFiles)
            {
                break;
            }
        }
        if (paths.Count == 0)
        {
            var hint = rejected.Count > 0 ? $" Rejected lines did not look like file paths (e.g. '{rejected[0]}'). Provide one repo-relative path per line." : "";
            return "No valid files requested." + hint;
        }

        var chunks = new List<string>();
        var total = 0;
        foreach (var rel in paths)
        {
            var path = _sandbox.Resolve(rel);
            _secrets.ThrowIfSensitive(path);
            if (!File.Exists(path))
            {
                chunks.Add($"## {rel}\n[not found]");
                continue;
            }
            var text = await File.ReadAllTextAsync(path);
            _registry.MarkRead(path, text);
            var trimmed = text.Length > _config.MaxContextPackFileChars ? text[.._config.MaxContextPackFileChars] + "\n[file truncated]" : text;
            var numbered = AddLineNumbers(trimmed);
            var chunk = $"## {rel}\n```text\n{numbered}\n```";
            if (total + chunk.Length > _config.MaxContextPackTotalChars)
            {
                chunks.Add("[context pack truncated]");
                break;
            }
            chunks.Add(chunk);
            total += chunk.Length;
        }
        return string.Join("\n\n", chunks);
    }

    private static bool LooksLikePath(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }
        if (line.Contains(' ') && !line.Contains('/') && !line.Contains('\\'))
        {
            return false;
        }
        // Disallow obvious markdown/prose lines.
        return !line.StartsWith('*') && !line.StartsWith('-') && !line.StartsWith('+') && line.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }
    private static string AddLineNumbers(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select((line, i) => $"{i + 1,4}: {line}"));
}

public sealed record FileRangeRequest(string File, int? Start, int? End);

public sealed class FileRangeContextTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "read_ranges"; public string Description => "Read specific line ranges. Input JSON: [{file,start,end}] or {file,start,end}."; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        List<FileRangeRequest>? ranges;
        try
        {
            ranges = ParseRanges(input);
        }
        catch (Exception ex) { return $"Invalid range JSON: {ex.Message}"; }
        if (ranges is null || ranges.Count == 0)
        {
            return "No ranges requested.";
        }

        var chunks = new List<string>();
        foreach (FileRangeRequest? r in ranges.Take(12))
        {
            var requestedFile = r.File;
            string path;
            try
            {
                path = _sandbox.Resolve(requestedFile);
            }
            catch
            {
                chunks.Add($"## {requestedFile}\n[not found]");
                continue;
            }
            if (!File.Exists(path) && TryResolveByLeafName(requestedFile, out var leafResolved, out var leafRelative))
            {
                path = leafResolved;
                requestedFile = leafRelative;
            }
            _secrets.ThrowIfSensitive(path);
            if (!File.Exists(path))
            {
                chunks.Add($"## {r.File}\n[not found - use the full repo-relative path from code_map, e.g. 'Approval/ApprovalPolicy.cs', not just the file name]");
                continue;
            }
            var content = await File.ReadAllTextAsync(path);
            _registry.MarkRead(path, content);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var requestedStart = r.Start ?? 1;
            var requestedEnd = r.End ?? Math.Min(lines.Length, requestedStart + 199);

            if (requestedStart < 1 || requestedEnd < requestedStart)
            {
                chunks.Add($"Invalid input. Requested range {requestedStart}-{requestedEnd} for {r.File} is invalid. Use 1-based inclusive line numbers with end >= start.");
                continue;
            }

            if (requestedStart > lines.Length)
            {
                chunks.Add($"Invalid input. Requested range {requestedStart}-{requestedEnd} starts past the end of {r.File}, which has {lines.Length} line(s). Choose a valid range or a different file from code_map.");
                continue;
            }

            var start = requestedStart;
            var end = Math.Clamp(requestedEnd, start, lines.Length);
            IEnumerable<string> selected = lines.Skip(start - 1).Take(end - start + 1).Select((line, i) => $"{start + i,4}: {line}");
            chunks.Add($"## {requestedFile}:{start}-{end}\n```text\n{string.Join("\n", selected)}\n```");
        }
        return string.Join("\n\n", chunks);
    }

    private bool TryResolveByLeafName(string requested, out string absolutePath, out string relativePath)
    {
        absolutePath = "";
        relativePath = "";
        var leaf = Path.GetFileName(requested);
        if (string.IsNullOrWhiteSpace(leaf) || leaf != requested.Replace('\\', '/').TrimStart('/'))
        {
            return false;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(_sandbox.Root, leaf, SearchOption.AllDirectories))
            {
                var rel = _sandbox.Relative(candidate).Replace('\\', '/');
                if (rel.StartsWith(".git/", StringComparison.Ordinal)
                    || rel.StartsWith(".agent/", StringComparison.Ordinal)
                    || rel.StartsWith("bin/", StringComparison.Ordinal)
                    || rel.StartsWith("obj/", StringComparison.Ordinal)
                    || rel.StartsWith("node_modules/", StringComparison.Ordinal)
                    || rel.Contains("/bin/", StringComparison.Ordinal)
                    || rel.Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }
                absolutePath = candidate;
                relativePath = rel;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static List<FileRangeRequest>? ParseRanges(string input)
    {
        using var doc = JsonDocument.Parse(input);
        return doc.RootElement.ValueKind switch
        {
            JsonValueKind.Array => JsonSerializer.Deserialize<List<FileRangeRequest>>(doc.RootElement.GetRawText(), AgentConfig.JsonOptions()),
            JsonValueKind.Object =>
            [
                JsonSerializer.Deserialize<FileRangeRequest>(doc.RootElement.GetRawText(), AgentConfig.JsonOptions())
                ?? throw new InvalidOperationException("Range object could not be parsed.")
            ],
            JsonValueKind.Undefined => throw new NotImplementedException(),
            JsonValueKind.String => throw new NotImplementedException(),
            JsonValueKind.Number => throw new NotImplementedException(),
            JsonValueKind.True => throw new NotImplementedException(),
            JsonValueKind.False => throw new NotImplementedException(),
            JsonValueKind.Null => throw new NotImplementedException(),
            _ => throw new InvalidOperationException("Expected a JSON object or array of range objects.")
        };
    }
}

public sealed class ContextRefiner(LlmClient llm, PromptCatalog prompts)
{
    private readonly LlmClient _llm = llm; private readonly PromptCatalog _prompts = prompts;

    public Task<string> RefineAsync(string task, string searchResults)
    {
        var p = _prompts.Render(PromptId.ContextRefine, new()
        {
            ["task"] = task,
            ["search_results"] = searchResults
        });
        return _llm.ChatAsync(p);
    }
}

public sealed class RefineContextTool(ContextRefiner refiner) : ITool
{
    private readonly ContextRefiner _refiner = refiner;

    public string Name => "refine_context"; public string Description => "Given task and search results, choose exact file line ranges. Input: TASK\n---\nSEARCH_RESULTS"; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        var parts = input.Split("---", 2);
        return parts.Length != 2
            ? "Invalid input. Use: TASK\\n---\\nSEARCH_RESULTS"
            : await _refiner.RefineAsync(parts[0].Trim(), parts[1].Trim());
    }
}
