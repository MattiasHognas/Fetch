namespace Fetch.Lsp;

public sealed class LspServerSelector(AgentConfig config, PathSandbox sandbox)
{
    private readonly AgentConfig _config = config; private readonly PathSandbox _sandbox = sandbox;

    public LspServerConfig? SelectForRepo()
    {
        return !_config.Lsp.Enabled
            ? null
            : _config.Lsp.Servers.FirstOrDefault(s => s.Enabled && s.RootMarkers.Any(m => File.Exists(Path.Combine(_sandbox.Root, m))) && CommandExists(s.Command));
    }
    private static bool CommandExists(string command) => (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? []).Any(p => File.Exists(Path.Combine(p, OperatingSystem.IsWindows() ? command + ".exe" : command)));
}
