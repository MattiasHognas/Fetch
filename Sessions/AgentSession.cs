namespace Fetch.Sessions;

public sealed class AgentSession
{
    public string Id
    {
        get;
    }
    public string DirectoryPath
    {
        get;
    }
    public string LogPath => Path.Combine(DirectoryPath, "log.jsonl");
    public string TodosPath => Path.Combine(DirectoryPath, "todos.json");
    public string CommandHistoryPath => Path.Combine(DirectoryPath, "command-history.jsonl");
    public string SummaryPath => Path.Combine(DirectoryPath, "summary.md");

    public AgentSession(string id, AgentConfig config)
    {
        Id = id;
        DirectoryPath = Path.Combine(config.SessionRoot, id);
        _ = Directory.CreateDirectory(DirectoryPath);
    }

    public static AgentSession Current(AgentConfig config) => new($"current-{Environment.ProcessId}", config);
    public static AgentSession New(AgentConfig config) => new(DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), config);

    public string RecentLogTail(int maxLines = 40) => !File.Exists(LogPath)
        ? ""
        : string.Join(Environment.NewLine, File.ReadLines(LogPath).TakeLast(maxLines));
    public string ReadSummary() => File.Exists(SummaryPath) ? File.ReadAllText(SummaryPath) : "";
}
