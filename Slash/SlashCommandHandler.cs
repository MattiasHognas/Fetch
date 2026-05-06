namespace Fetch.Slash;

public sealed class SlashCommandHandler(AgentSession session, TodoStore todoStore, LlmClient llm, AgentConfig config, PathSandbox sandbox, AgentRuntimeState state, IEnumerable<ITool> tools, PromptCatalog prompts, SemanticIndex semanticIndex)
{
    private readonly AgentSession _session = session;
    private readonly TodoStore _todoStore = todoStore;
    private readonly AgentConfig _config = config;
    private readonly PathSandbox _sandbox = sandbox;
    private readonly AgentRuntimeState _state = state;
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name);
    private readonly ApprovalPolicy _approvalPolicy = new(config);
    private readonly TranscriptCompactor _compactor = new(llm, prompts);
    private readonly PromptCatalog _prompts = prompts;
    private readonly SemanticIndex _semanticIndex = semanticIndex;

    public async Task<bool> TryHandleAsync(string input)
    {
        if (!input.StartsWith('/'))
        {
            return false;
        }

        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (cmd)
        {
            case "/help":
                PrintHelp();
                return true;
            case "/session":
                PrintSession();
                return true;
            case "/todos":
                await PrintTodosAsync();
                return true;
            case "/status":
                await RunShellAsync("git status");
                return true;
            case "/diff":
                await RunShellAsync("git diff");
                return true;
            case "/log":
                PrintTail(_session.LogPath);
                return true;
            case "/history":
                PrintTail(_session.CommandHistoryPath);
                return true;
            case "/compact":
                await CompactLogAsync();
                return true;
            case "/health":
                Console.WriteLine(await new HealthChecker(_config, _sandbox).CheckAsync());
                return true;
            case "/mode":
                await HandleModeAsync(arg);
                return true;
            case "/prompts":
                _prompts.WriteDefaults();
                Console.WriteLine($"Wrote prompts to {_config.PromptRoot}");
                return true;
            case "/config":
                Console.WriteLine(_config.ConfigPath);
                return true;
            case "/index":
                await HandleIndexAsync();
                return true;
            case "/last-tool":
                PrintExecution(_state.LastTool, "No tool execution recorded.");
                return true;
            case "/last-error":
                PrintExecution(_state.LastError, "No error recorded.");
                return true;
            case "/last-command":
                PrintExecution(_state.LastCommand, "No command recorded.");
                return true;
            case "/replay":
                await ReplayLastCommandAsync();
                return true;
            case "/clear":
                Console.Clear();
                return true;
            default:
                Console.WriteLine("Unknown slash command. Try /help.");
                return true;
        }
    }
    private static void PrintHelp() => Console.WriteLine("""
/help          Show commands
/session       Show current session
/todos         Show todo list
/status        Run git status
/diff          Run git diff
/log           Show recent session log
/history       Show recent command history
/compact       Compact session log into summary.md
/health        Check Ollama, embeddings, rg, index, config, and LSP
/mode MODE     Set approval mode: read-only|ask|auto-safe|dry-run|yolo
/prompts       Export default prompts
/config        Show config path
/index         Build semantic index
/last-tool     Show last tool execution
/last-error    Show last failed tool execution
/last-command  Show last shell command
/replay        Replay last shell command
/clear         Clear terminal
/exit          Exit
""");
    private void PrintSession()
    {
        Console.WriteLine($"Session: {_session.Id}");
        Console.WriteLine($"Path: {_session.DirectoryPath}");
    }
    private async Task PrintTodosAsync()
    {
        List<TodoItem> todos = await _todoStore.ReadAsync();
        if (todos.Count == 0)
        {
            Console.WriteLine("No todos.");
            return;
        }
        foreach (TodoItem t in todos)
        {
            Console.WriteLine($"{t.Id}. [{t.Status}] {t.Text}");
        }
    }
    private async Task RunShellAsync(string command)
    {
        if (!_tools.TryGetValue("run_command", out ITool? tool))
        {
            Console.WriteLine("run_command unavailable.");
            return;
        }
        Console.WriteLine(await tool.RunAsync($"{{\"command\":\"{command}\",\"cwd\":\".\",\"timeoutSeconds\":60}}"));
    }
    private static void PrintTail(string path, int lines = 20)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("No file found.");
            return;
        }
        foreach (var line in File.ReadLines(path).TakeLast(lines))
        {
            Console.WriteLine(line);
        }
    }
    private async Task CompactLogAsync()
    {
        if (!File.Exists(_session.LogPath))
        {
            Console.WriteLine("No session log to compact.");
            return;
        }
        var log = await File.ReadAllTextAsync(_session.LogPath);
        var summary = await _compactor.CompactAsync(log);
        await File.WriteAllTextAsync(_session.SummaryPath, summary);
        Console.WriteLine($"Wrote summary: {_session.SummaryPath}");
    }
    private async Task HandleIndexAsync()
    {
        Console.WriteLine("Building semantic index...");
        SemanticIndexStats stats = await _semanticIndex.BuildAsync();
        Console.WriteLine("Semantic index built.");
        Console.WriteLine($"Indexed files: {stats.IndexedFiles}");
        Console.WriteLine($"Reused files: {stats.ReusedFiles}");
        Console.WriteLine($"Removed files: {stats.RemovedFiles}");
        Console.WriteLine($"Total chunks: {stats.TotalChunks}");
    }
    private async Task HandleModeAsync(string mode)
    {
        var valid = new[] { "read-only", "ask", "auto-safe", "dry-run", "yolo" };
        if (string.IsNullOrWhiteSpace(mode))
        {
            Console.WriteLine("Usage: /mode read-only|ask|auto-safe|dry-run|yolo");
            return;
        }
        if (!valid.Contains(mode))
        {
            Console.WriteLine("Invalid mode.");
            return;
        }
        if (mode == "yolo")
        {
            Console.WriteLine("WARNING: YOLO mode runs mutation tools without asking. Sandbox, secret blocking, and command policy still apply. Type YES to confirm:");
            if (Console.ReadLine() != "YES")
            {
                Console.WriteLine("Cancelled.");
                return;
            }
        }
        _config.ApprovalMode = mode;
        await _config.SaveAsync();
        Console.WriteLine($"Approval mode set to: {mode}");
    }
    private static void PrintExecution(ToolExecution? exec, string empty)
    {
        if (exec is null)
        {
            Console.WriteLine(empty);
            return;
        }
        Console.WriteLine("Tool:");
        Console.WriteLine(exec.Tool);
        Console.WriteLine("\nInput:");
        Console.WriteLine(exec.Input);
        Console.WriteLine("\nResult:");
        var r = exec.Result.Length > 4000 ? exec.Result[..4000] + "\n[truncated]" : exec.Result;
        Console.WriteLine(r);
    }
    private async Task ReplayLastCommandAsync()
    {
        if (_state.LastCommand is null)
        {
            Console.WriteLine("No command to replay.");
            return;
        }
        if (!_tools.TryGetValue("run_command", out ITool? tool))
        {
            Console.WriteLine("run_command tool is unavailable.");
            return;
        }
        Console.WriteLine("Replaying last command:");
        Console.WriteLine(_state.LastCommand.Input);
        ApprovalDecision decision = _approvalPolicy.Decide(tool);
        if (decision == ApprovalDecision.Deny)
        {
            Console.WriteLine("Replay denied by approval policy.");
            return;
        }
        if (decision == ApprovalDecision.DryRun)
        {
            Console.WriteLine("Dry-run: command not executed.");
            return;
        }
        if (decision == ApprovalDecision.Ask)
        {
            Console.Write("[y/N] ");
            if (!string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Replay cancelled.");
                return;
            }
        }
        var result = await tool.RunAsync(_state.LastCommand.Input);
        var err = result.Contains("failed", StringComparison.OrdinalIgnoreCase) || result.Contains("\"ExitCode\": 1") || result.Contains("\"ExitCode\": -1");
        var ex = new ToolExecution("run_command", _state.LastCommand.Input, result, err);
        _state.LastTool = ex;
        _state.LastCommand = ex;
        if (err)
        {
            _state.LastError = ex;
        }

        Console.WriteLine(result);
    }
}
