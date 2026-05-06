namespace Fetch.Events;

public sealed class AgentEventStore
{
    private readonly List<AgentEvent> _events = [];
    public IReadOnlyList<AgentEvent> Events => _events;
    public event Action<AgentEvent>? Added;
    public void Add(AgentEventType type, string title, string body, string? tool = null, string? input = null)
    {
        var ev = new AgentEvent(Guid.NewGuid(), DateTimeOffset.Now, type, title, body, tool, input);
        _events.Add(ev);
        Added?.Invoke(ev);
    }
}
