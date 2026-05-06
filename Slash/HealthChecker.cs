using System.Net.Http.Json;

namespace Fetch.Slash;

public sealed class HealthChecker(AgentConfig config, PathSandbox sandbox)
{
    private readonly AgentConfig _config = config; private readonly PathSandbox _sandbox = sandbox;

    public async Task<string> CheckAsync()
    {
        var lines = new List<string> { "Health Check", "", CheckPath("Repo root", _sandbox.Root), CheckPath("Config", _config.ConfigPath), CheckDirectory("Sessions", _config.SessionRoot), CheckIndexDirectory(_config.IndexRoot), CheckCommand("rg", "ripgrep"), await CheckOllamaAsync("Ollama", _config.ModelBaseUrl), await CheckEmbeddingAsync(), CheckLspServers() };
        return string.Join(Environment.NewLine, lines);
    }
    private static string CheckPath(string label, string path) => File.Exists(path) || Directory.Exists(path) ? $"OK    {label}: {path}" : $"WARN  {label}: not found ({path})";
    private static string CheckDirectory(string label, string path) => Directory.Exists(path) ? $"OK    {label}: {path}" : $"WARN  {label}: not found ({path})";
    private static string CheckIndexDirectory(string path) => Directory.Exists(path) ? $"OK    Index: {path}" : $"INFO  Index: not built yet ({path})";
    private static string CheckCommand(string command, string label) => CommandExists(command) ? $"OK    {label}: {command}" : $"WARN  {label}: {command} not found";
    private static async Task<string> CheckOllamaAsync(string label, string baseUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            HttpResponseMessage res = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/tags");
            return res.IsSuccessStatusCode ? $"OK    {label}: {baseUrl}" : $"WARN  {label}: status {(int)res.StatusCode}";
        }
        catch (Exception ex) { return $"FAIL  {label}: unavailable ({ex.Message})"; }
    }
    private async Task<string> CheckEmbeddingAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            HttpResponseMessage res = await http.PostAsJsonAsync($"{_config.EmbeddingBaseUrl.TrimEnd('/')}/api/embeddings", new
            {
                model = _config.EmbeddingModel,
                prompt = "health check"
            });
            return res.IsSuccessStatusCode ? $"OK    Embeddings: {_config.EmbeddingModel}" : $"WARN  Embeddings: status {(int)res.StatusCode}";
        }
        catch (Exception ex) { return $"FAIL  Embeddings: unavailable ({ex.Message})"; }
    }
    private string CheckLspServers()
    {
        if (!_config.Lsp.Enabled)
        {
            return "INFO  LSP: disabled";
        }

        if (_config.Lsp.Servers.Count == 0)
        {
            return "INFO  LSP: no servers configured";
        }

        var lines = new List<string> { "LSP servers:" };
        foreach (LspServerConfig s in _config.Lsp.Servers)
        {
            if (!s.Enabled)
            {
                lines.Add($"  INFO {s.Id}: disabled");
                continue;
            }
            var cmd = CommandExists(s.Command);
            var marker = s.RootMarkers.Any(m => File.Exists(Path.Combine(_sandbox.Root, m)));
            var status = cmd && marker ? "OK" : "WARN";
            lines.Add($"  {status,-4} {s.Id}: command={s.Command} commandOk={cmd} rootMarkerOk={marker}");
        }
        return string.Join(Environment.NewLine, lines);
    }
    private static bool CommandExists(string command) => (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? []).Any(p => File.Exists(Path.Combine(p, OperatingSystem.IsWindows() ? command + ".exe" : command)));
}
