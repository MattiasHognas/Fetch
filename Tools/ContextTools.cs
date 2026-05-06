using System.Text.Json;

namespace Fetch.Tools;

public sealed class ContextPackTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets, AgentConfig config) : ITool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets; private readonly AgentConfig _config = config;

    public string Name => "context_pack"; public string Description => "Pack multiple files into one bounded context. Input: one path per line."; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        var paths = input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(_config.MaxContextPackFiles).ToList();
        if (paths.Count == 0)
        {
            return "No files requested.";
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
    private static string AddLineNumbers(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select((line, i) => $"{i + 1,4}: {line}"));
}

public sealed record FileRangeRequest(string File, int Start, int End);

public sealed class FileRangeContextTool(FileReadRegistry registry, PathSandbox sandbox, SecretPolicy secrets) : ITool
{
    private readonly FileReadRegistry _registry = registry; private readonly PathSandbox _sandbox = sandbox; private readonly SecretPolicy _secrets = secrets;

    public string Name => "read_ranges"; public string Description => "Read specific line ranges. Input JSON: [{file,start,end}]"; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        List<FileRangeRequest>? ranges;
        try
        {
            ranges = JsonSerializer.Deserialize<List<FileRangeRequest>>(input, AgentConfig.JsonOptions());
        }
        catch (Exception ex) { return $"Invalid range JSON: {ex.Message}"; }
        if (ranges is null || ranges.Count == 0)
        {
            return "No ranges requested.";
        }

        var chunks = new List<string>();
        foreach (FileRangeRequest? r in ranges.Take(12))
        {
            var path = _sandbox.Resolve(r.File);
            _secrets.ThrowIfSensitive(path);
            if (!File.Exists(path))
            {
                chunks.Add($"## {r.File}\n[not found]");
                continue;
            }
            var content = await File.ReadAllTextAsync(path);
            _registry.MarkRead(path, content);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var start = Math.Clamp(r.Start, 1, lines.Length);
            var end = Math.Clamp(r.End, start, lines.Length);
            IEnumerable<string> selected = lines.Skip(start - 1).Take(end - start + 1).Select((line, i) => $"{start + i,4}: {line}");
            chunks.Add($"## {r.File}:{start}-{end}\n```text\n{string.Join("\n", selected)}\n```");
        }
        return string.Join("\n\n", chunks);
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
