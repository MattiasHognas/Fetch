namespace Fetch.Lsp;

public sealed class LspServerSelector(AgentConfig config, PathSandbox sandbox)
{
    private readonly AgentConfig _config = config; private readonly PathSandbox _sandbox = sandbox;

    public LspServerConfig? SelectForRepo()
    {
        return !_config.Lsp.Enabled
            ? null
            : _config.Lsp.Servers.FirstOrDefault(s => s.Enabled && RootMarkerOk(s) && CommandOk(s));
    }

    public bool RootMarkerOk(LspServerConfig server)
    {
        return server.RootMarkers.Length == 0 || server.RootMarkers.Any(MarkerExists);
    }

    public static bool CommandOk(LspServerConfig server) => CommandExists(server.Command);

    private bool MarkerExists(string marker)
    {
        var trimmed = marker.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        if (trimmed.IndexOfAny(['*', '?']) >= 0)
        {
            return Directory.EnumerateFileSystemEntries(_sandbox.Root, trimmed, SearchOption.TopDirectoryOnly).Any();
        }

        var path = Path.Combine(_sandbox.Root, trimmed);
        return File.Exists(path) || Directory.Exists(path);
    }

    private static bool CommandExists(string command) => (Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? []).Any(p => File.Exists(Path.Combine(p, OperatingSystem.IsWindows() ? command + ".exe" : command)));
}
