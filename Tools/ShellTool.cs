using System.Diagnostics;
using System.Text.Json;

namespace Fetch.Tools;

public sealed record ShellRequest(string Command, string Cwd = ".", int TimeoutSeconds = 60);
public sealed record ShellResult(string Command, string Cwd, int ExitCode, bool TimedOut, string Stdout, string Stderr, long DurationMs);

public sealed class ShellTool(CommandPolicy policy, AgentSession session, PathSandbox sandbox, AgentConfig config) : ITool, INativeTool
{
    private readonly CommandPolicy _policy = policy; private readonly AgentSession _session = session; private readonly PathSandbox _sandbox = sandbox; private readonly AgentConfig _config = config;

    public string Name => "run_command";
    public string Description => "Run an allowed shell command from JSON arguments.";
    public ApprovalMode Approval => ApprovalMode.Ask;

    public object GetParametersSchema() => NativeToolJson.ObjectSchema(new Dictionary<string, object?>
    {
        ["command"] = NativeToolJson.StringProperty("Shell command to execute."),
        ["cwd"] = NativeToolJson.StringProperty("Optional repo-relative working directory. Defaults to '.'."),
        ["timeoutSeconds"] = NativeToolJson.IntegerProperty("Optional timeout in seconds.", 1)
    }, "command");

    public string ConvertArguments(JsonElement arguments)
    {
        return NativeToolJson.TryGetString(arguments, "command", out var command)
            ? NativeToolJson.SerializeObject(new Dictionary<string, object?>
            {
                ["command"] = command,
                ["cwd"] = NativeToolJson.TryGetString(arguments, "cwd", out var cwd) ? cwd : ".",
                ["timeoutSeconds"] = NativeToolJson.TryGetInt(arguments, "timeoutSeconds", out var timeoutSeconds) ? timeoutSeconds : _config.DefaultCommandTimeoutSeconds
            })
            : "";
    }

    public async Task<string> RunAsync(string input)
    {
        if (!TryParse(input, out ShellRequest? req))
        {
            return JsonSerializer.Serialize(new ShellResult("", ".", -1, false, "", "Invalid input. Provide JSON {\"command\":\"...\",\"cwd\":\".\",\"timeoutSeconds\":60}.", 0), AgentConfig.JsonOptions());
        }

        if (!_policy.IsAllowed(req.Command))
        {
            return JsonSerializer.Serialize(new ShellResult(req.Command, req.Cwd, -1, false, "", "Blocked by command policy.", 0), AgentConfig.JsonOptions());
        }

        var timeout = Math.Clamp(req.TimeoutSeconds <= 0 ? _config.DefaultCommandTimeoutSeconds : req.TimeoutSeconds, 1, _config.MaxCommandTimeoutSeconds);
        var started = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows() ? $"/c {req.Command}" : $"-lc \"{req.Command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _sandbox.Resolve(req.Cwd),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using Process p = Process.Start(psi)!;
        try
        {
            Task<string> outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> errTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            var stdout = await outTask;
            var stderr = await errTask;
            started.Stop();
            var res = new ShellResult(req.Command, req.Cwd, p.ExitCode, false, Trim(stdout), Trim(stderr), started.ElapsedMilliseconds);
            await Log(res);
            return JsonSerializer.Serialize(res, AgentConfig.JsonOptions());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                }
            }
            catch { }
            started.Stop();
            var res = new ShellResult(req.Command, req.Cwd, -1, true, "", $"Command timed out after {timeout}s.", started.ElapsedMilliseconds);
            await Log(res);
            return JsonSerializer.Serialize(res, AgentConfig.JsonOptions());
        }
    }
    private bool TryParse(string input, out ShellRequest request)
    {
        request = new ShellRequest("", ".", _config.DefaultCommandTimeoutSeconds);
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            ShellRequest? parsed = JsonSerializer.Deserialize<ShellRequest>(input, AgentConfig.JsonOptions());
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Command))
            {
                return false;
            }

            request = parsed with
            {
                Cwd = string.IsNullOrWhiteSpace(parsed.Cwd) ? "." : parsed.Cwd,
                TimeoutSeconds = parsed.TimeoutSeconds <= 0 ? _config.DefaultCommandTimeoutSeconds : parsed.TimeoutSeconds
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
    private string Trim(string text) => text.Length <= _config.MaxToolResultChars ? text : text[.._config.MaxToolResultChars] + "\n[truncated]";
    private async Task Log(ShellResult res) => await File.AppendAllTextAsync(_session.CommandHistoryPath, JsonSerializer.Serialize(res) + Environment.NewLine);
}
