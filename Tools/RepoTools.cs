using System.Diagnostics;

namespace Fetch.Tools;

public sealed class RepoMap
{
    private readonly IgnoreRules _ignore; private readonly PathSandbox _sandbox; private readonly List<string> _files = [];
    public IReadOnlyList<string> Files => _files;
    public RepoMap(IgnoreRules ignore, PathSandbox sandbox)
    {
        _ignore = ignore;
        _sandbox = sandbox;
        Load();
    }
    private void Load()
    {
        foreach (var file in Directory.GetFiles(_sandbox.Root, "*.*", SearchOption.AllDirectories))
        {
            var rel = _sandbox.Relative(file);
            if (!_ignore.IsIgnored(rel))
            {
                _files.Add(rel);
            }
        }
    }
}

public sealed class RepoTreeTool(IgnoreRules ignore, PathSandbox sandbox) : ITool
{
    private readonly IgnoreRules _ignore = ignore; private readonly PathSandbox _sandbox = sandbox;

    public string Name => "repo_tree"; public string Description => "Show repository tree. Input: optional depth number, default 2."; public ApprovalMode Approval => ApprovalMode.Auto;
    public Task<string> RunAsync(string input)
    {
        var depth = int.TryParse(input.Trim(), out var d) ? Math.Clamp(d, 1, 6) : 2;
        var lines = new List<string>();
        Walk(_sandbox.Root, "", 0, depth, lines);
        return Task.FromResult(string.Join("\n", lines.Take(500)));
    }
    private void Walk(string dir, string indent, int depth, int max, List<string> lines)
    {
        if (depth > max)
        {
            return;
        }

        foreach (var sub in Directory.GetDirectories(dir).OrderBy(x => x))
        {
            var rel = _sandbox.Relative(sub);
            if (_ignore.IsIgnored(rel))
            {
                continue;
            }

            lines.Add($"{indent}{Path.GetFileName(sub)}/");
            Walk(sub, indent + "  ", depth + 1, max, lines);
        }
        foreach (var file in Directory.GetFiles(dir).OrderBy(x => x).Take(100))
        {
            var rel = _sandbox.Relative(file);
            if (!_ignore.IsIgnored(rel))
            {
                lines.Add($"{indent}{Path.GetFileName(file)}");
            }
        }
    }
}

public sealed class SearchTool(RepoMap repo) : ITool
{
    private readonly RepoMap _repo = repo;

    public string Name => "search_files"; public string Description => "Search files by keyword/path."; public ApprovalMode Approval => ApprovalMode.Auto;
    public Task<string> RunAsync(string input)
    {
        var term = input.Trim();
        return Task.FromResult(string.Join("\n", _repo.Files.Where(f => f.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(50)));
    }
}

public sealed class SearchContentTool(PathSandbox sandbox, AgentConfig config) : ITool
{
    private readonly PathSandbox _sandbox = sandbox;
    private readonly AgentConfig _config = config;

    public string Name => "search_content"; public string Description => "Search file contents using ripgrep. Input: search term or regex."; public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        var query = input.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Empty search.";
        }

        if (!CommandExists("rg"))
        {
            return "ripgrep (rg) is not installed.";
        }

        var psi = new ProcessStartInfo { FileName = "rg", WorkingDirectory = _sandbox.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("--line-number");
        psi.ArgumentList.Add("--hidden");
        psi.ArgumentList.Add("--glob");
        psi.ArgumentList.Add("!.git");
        psi.ArgumentList.Add("--glob");
        psi.ArgumentList.Add("!.agent");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(query);
        psi.ArgumentList.Add(".");
        Process p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var text = string.IsNullOrWhiteSpace(output) ? error : output;
        return text.Length > _config.MaxToolResultChars ? text[.._config.MaxToolResultChars] + "\n[truncated]" : text;
    }
    private static bool CommandExists(string command) => (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? []).Any(p => File.Exists(Path.Combine(p, OperatingSystem.IsWindows() ? command + ".exe" : command)));
}
