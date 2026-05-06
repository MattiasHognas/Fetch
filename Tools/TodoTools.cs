using System.Text.Json;

namespace Fetch.Tools;

public sealed record TodoItem(int Id, string Text, string Status);

public sealed class TodoStore(AgentSession session)
{
    private readonly string _path = session.TodosPath;

    public async Task<List<TodoItem>> ReadAsync()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return !File.Exists(_path)
            ? []
            : JsonSerializer.Deserialize<List<TodoItem>>(await File.ReadAllTextAsync(_path), AgentConfig.JsonOptions()) ?? [];
    }
    public async Task WriteAsync(List<TodoItem> todos)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(todos, AgentConfig.JsonOptions()));
    }
}

public sealed class TodoReadTool(TodoStore store) : ITool
{
    private readonly TodoStore _store = store;

    public string Name => "todo_read";
    public string Description => "Read the current agent todo list.";
    public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        List<TodoItem> todos = await _store.ReadAsync();
        return todos.Count == 0 ? "No todos." : JsonSerializer.Serialize(todos, AgentConfig.JsonOptions());
    }
}

public sealed class TodoWriteTool(TodoStore store) : ITool
{
    private readonly TodoStore _store = store;

    public string Name => "todo_write";
    public string Description => "Replace the current todo list. Input JSON array: [{id,text,status}] with status pending|in_progress|done.";
    public ApprovalMode Approval => ApprovalMode.Auto;
    public async Task<string> RunAsync(string input)
    {
        List<TodoItem>? todos;
        try
        {
            todos = JsonSerializer.Deserialize<List<TodoItem>>(input, AgentConfig.JsonOptions());
        }
        catch (Exception ex) { return $"Invalid todo JSON: {ex.Message}"; }
        if (todos is null)
        {
            return "Invalid todo JSON.";
        }

        var valid = new[] { "pending", "in_progress", "done" };
        foreach (TodoItem t in todos)
        {
            if (!valid.Contains(t.Status))
            {
                return $"Invalid status for todo {t.Id}: {t.Status}";
            }
        }

        await _store.WriteAsync(todos);
        return $"Saved {todos.Count} todo(s).";
    }
}
