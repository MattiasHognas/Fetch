using System.Text.Json;

namespace Fetch.Sessions;

public sealed class SessionLogger(AgentSession session)
{
    private readonly string _path = session.LogPath;

    public async Task LogAsync(string type, object data)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            type,
            data
        };
        await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }
}
