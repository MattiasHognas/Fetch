namespace Fetch.Tools;

public sealed class CommandPolicy(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    public bool IsAllowed(string command)
    {
        command = command.Trim();
        return !command.Contains("..")
            && !_config.BlockedCommandTokens.Any(command.Contains)
            && !_config.BlockedCommandPrefixes.Any(p => command.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
