namespace Fetch.Core;

public sealed class SecretPolicy(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    public bool IsSensitivePath(string path)
    {
        var name = Path.GetFileName(path);
        if (_config.BlockedFileNames.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var ext = Path.GetExtension(path);
        return _config.BlockedExtensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }

    public void ThrowIfSensitive(string path)
    {
        if (IsSensitivePath(path))
        {
            throw new InvalidOperationException($"Blocked sensitive file: {path}");
        }
    }
}
