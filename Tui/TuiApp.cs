using Terminal.Gui;

namespace Fetch.Tui;

public sealed class TuiApp(AgentLoop agent, AgentEventStore events, AgentRuntimeState state, IEnumerable<ITool> tools) : IDisposable
{
    private readonly AgentLoop _agent = agent; private readonly AgentEventStore _events = events; private readonly AgentRuntimeState _state = state; private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name);
    private ListView _timeline = null!; private TextView _details = null!; private TextField _prompt = null!;

    public void Run()
    {
        Application.Init();
        Toplevel top = Application.Top;
        var win = new Window("Fetch") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() };
        var menu = new MenuBar([new MenuBarItem("_Agent", [new MenuItem("_Replay last command", "", async () => await ReplayLastCommandAsync()), new MenuItem("_Quit", "", () => Application.RequestStop())])]);
        _timeline = new ListView(Array.Empty<string>()) { X = 0, Y = 0, Width = 35, Height = Dim.Fill() - 3 };
        _details = new TextView { X = Pos.Right(_timeline) + 1, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() - 3, ReadOnly = true, WordWrap = false };
        _prompt = new TextField("") { X = 0, Y = Pos.Bottom(_timeline) + 1, Width = Dim.Fill() - 10, Height = 1 };
        var send = new Button("Send") { X = Pos.Right(_prompt) + 1, Y = Pos.Top(_prompt) };
        send.Clicked += async () => await SubmitPromptAsync();
        _prompt.KeyPress += async e => { if (e.KeyEvent.Key == Key.Enter) { e.Handled = true; await SubmitPromptAsync(); } };
        _timeline.OpenSelectedItem += _ => ShowSelectedEvent();
        _timeline.SelectedItemChanged += _ => ShowSelectedEvent();
        _events.Added += _ => Application.MainLoop.Invoke(UpdateTimeline);
        win.Add(_timeline, _details, _prompt, send);
        top.Add(menu, win);
        UpdateTimeline();
        Application.Run();
        Application.Shutdown();
    }
    private async Task SubmitPromptAsync()
    {
        var input = _prompt.Text.ToString();
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        _prompt.Text = "";
        if (input == "/exit")
        {
            Application.RequestStop();
            return;
        }
        _ = Task.Run(async () => { try { await _agent.RunAsync(input); } catch (Exception ex) { _events.Add(AgentEventType.Error, "Agent error", ex.ToString()); } });
        await Task.CompletedTask;
    }
    private void UpdateTimeline()
    {
        var items = _events.Events.Select(e => $"{e.Timestamp:HH:mm:ss} {ShortType(e.Type)} {e.Title}").ToList();
        _timeline.SetSource(items);
        if (items.Count > 0)
        {
            _timeline.SelectedItem = items.Count - 1;
        }

        ShowSelectedEvent();
    }
    private void ShowSelectedEvent()
    {
        if (_events.Events.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_timeline.SelectedItem, 0, _events.Events.Count - 1);
        AgentEvent ev = _events.Events[index];
        _details.Text = $"Time: {ev.Timestamp}\nType: {ev.Type}\nTitle: {ev.Title}\n\nTool: {ev.Tool ?? "-"}\nInput:\n{ev.Input ?? "-"}\n\nBody:\n{ev.Body}";
    }
    private async Task ReplayLastCommandAsync()
    {
        if (_state.LastCommand is null)
        {
            _ = MessageBox.Query("Replay", "No command to replay.", "OK");
            return;
        }
        if (!_tools.TryGetValue("run_command", out ITool? tool))
        {
            _ = MessageBox.Query("Replay", "run_command unavailable.", "OK");
            return;
        }
        var confirm = MessageBox.Query("Replay command", _state.LastCommand.Input, "Replay", "Cancel");
        if (confirm != 0)
        {
            return;
        }

        _events.Add(AgentEventType.ToolCall, "Replay command", _state.LastCommand.Input, "run_command", _state.LastCommand.Input);
        var result = await tool.RunAsync(_state.LastCommand.Input);
        _events.Add(AgentEventType.ToolResult, "Replay result", result, "run_command", _state.LastCommand.Input);
    }
    private static string ShortType(AgentEventType type) => type switch { AgentEventType.UserInput => "USER", AgentEventType.LlmResponse => "LLM ", AgentEventType.ToolCall => "CALL", AgentEventType.ToolResult => "DONE", AgentEventType.Error => "ERR ", AgentEventType.Final => "FIN ", _ => "EVT " };

    public void Dispose()
    {
        _timeline?.Dispose();
        _details?.Dispose();
        _prompt?.Dispose();
    }
}
