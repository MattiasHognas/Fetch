using System.CommandLine;

var root = new RootCommand("Fetch");
var sessionOption = new Option<string?>("--session", "Session id to use");
var newSessionOption = new Option<bool>("--new-session", "Create a new timestamped session");

static Runtime BuildRuntime(string? sessionId, bool newSession)
{
    var repoRoot = Directory.GetCurrentDirectory();
    var config = AgentConfig.Load(repoRoot);
    AgentSession session = newSession ? AgentSession.New(config) : !string.IsNullOrWhiteSpace(sessionId) ? new AgentSession(sessionId, config) : AgentSession.Current(config);
    var sandbox = new PathSandbox(repoRoot);
    var secrets = new SecretPolicy(config);
    var prompts = new PromptCatalog(config);
    var llm = new LlmClient(config);
    var todoStore = new TodoStore(session);
    var logger = new SessionLogger(session);
    var ignore = new IgnoreRules(config, repoRoot);
    var repo = new RepoMap(ignore, sandbox);
    var fileReads = new FileReadRegistry();
    var policy = new CommandPolicy(config);
    var embeddings = new EmbeddingClient(config);
    var semanticIndex = new SemanticIndex(config, sandbox, secrets, embeddings, ignore);
    var searchContent = new SearchContentTool(sandbox);
    var lspSelector = new LspServerSelector(config, sandbox);
    var contextRefiner = new ContextRefiner(llm, prompts);
    var state = new AgentRuntimeState
    {
        SemanticSearchReady = semanticIndex.Exists
    };
    var events = new AgentEventStore();

    ITool[] tools =
    [
        new TodoReadTool(todoStore),
        new TodoWriteTool(todoStore),
        new RepoTreeTool(ignore, sandbox),
        new SearchTool(repo),
        searchContent,
        new CodeMapTool(config, sandbox, ignore, lspSelector),
        new RelationshipMapTool(config, sandbox, lspSelector),
        new LspSymbolSearchTool(config, sandbox, lspSelector, searchContent),
        new LspReferencesSearchTool(config, sandbox, lspSelector, searchContent),
        new SemanticSearchTool(semanticIndex),
        new RefineContextTool(contextRefiner),
        new GetFileSummaryTool(fileReads, sandbox, secrets),
        new FileRangeContextTool(fileReads, sandbox, secrets),
        new ContextPackTool(fileReads, sandbox, secrets, config),
        new ReadFileTool(fileReads, sandbox, secrets),
        new ListFilesTool(sandbox),
        new CreateFileTool(sandbox, secrets),
        new DeleteFileTool(sandbox, secrets),
        new RenameFileTool(sandbox, secrets),
        new ApplyPatchTool(sandbox, secrets),
        new ApplyDiffTool(fileReads, sandbox, secrets, config),
        new ShellTool(policy, session, sandbox, config)
    ];

    var agent = new AgentLoop(llm, tools, logger, todoStore, config, prompts, session, state, events, semanticIndex);
    var slash = new SlashCommandHandler(session, todoStore, llm, config, sandbox, state, tools, prompts, semanticIndex);
    return new Runtime(session, llm, todoStore, agent, slash, config, sandbox, state, events, tools, semanticIndex, logger);
}

var tuiCommand = new Command("tui", "Open terminal UI");
tuiCommand.AddOption(sessionOption);
tuiCommand.AddOption(newSessionOption);
tuiCommand.SetHandler((sessionId, newSession) =>
{
    Runtime runtime = BuildRuntime(sessionId, newSession);
    try
    {
        var health = new HealthChecker(runtime.Config, runtime.Sandbox).CheckAsync().GetAwaiter().GetResult();
        Console.WriteLine(health);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Health check failed: {ex.Message}");
    }
    if (runtime.Config.AutoReindex)
    {
        AgentLoop.TriggerBackgroundReindex(runtime.SemanticIndex, runtime.State, runtime.Logger);
    }
    var app = new TuiApp(runtime.Agent, runtime.Events, runtime.State, runtime.Tools);
    app.Run();
}, sessionOption, newSessionOption);

var chatCommand = new Command("chat", "Open interactive chat mode");
chatCommand.AddOption(sessionOption);
chatCommand.AddOption(newSessionOption);
chatCommand.SetHandler(async (sessionId, newSession) =>
{
    Runtime runtime = BuildRuntime(sessionId, newSession);
    if (runtime.Config.AutoReindex)
    {
        AgentLoop.TriggerBackgroundReindex(runtime.SemanticIndex, runtime.State, runtime.Logger);
    }
    Console.WriteLine("Fetch");
    Console.WriteLine("Type a request, or /help. Type /exit to quit.");
    Console.WriteLine($"Session: {runtime.Session.Id}");
    Console.WriteLine($"Repo: {Directory.GetCurrentDirectory()}");
    var health = await new HealthChecker(runtime.Config, runtime.Sandbox).CheckAsync();
    Console.WriteLine(health);
    while (true)
    {
        Console.Write("\n> ");
        var input = Console.ReadLine();
        if (input is null or "/exit")
        {
            break;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (await runtime.Slash.TryHandleAsync(input))
        {
            continue;
        }

        try
        {
            await runtime.Agent.RunAsync(input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Agent failed: {ex.Message}");
        }
    }
}, sessionOption, newSessionOption);

var agentCommand = new Command("agent", "Run the autonomous coding agent");
agentCommand.AddOption(sessionOption);
agentCommand.AddOption(newSessionOption);
var agentPrompt = new Argument<string[]>("prompt", () => [], "Task prompt") { Arity = ArgumentArity.ZeroOrMore };
agentCommand.AddArgument(agentPrompt);
agentCommand.SetHandler(async (prompt, sessionId, newSession) =>
{
    Runtime runtime = BuildRuntime(sessionId, newSession);
    var task = string.Join(" ", prompt);
    if (string.IsNullOrWhiteSpace(task))
    {
        Console.WriteLine("Missing task.");
        return;
    }
    if (runtime.Config.AutoBuildSemanticIndexOnAgentRun && !runtime.SemanticIndex.Exists)
    {
        Task<SemanticIndexStats> indexTask = runtime.SemanticIndex.BuildAsync();
        var delayTask = Task.Delay(TimeSpan.FromSeconds(runtime.Config.SemanticIndexBuildTimeoutSeconds));
        Task completed = await Task.WhenAny(indexTask, delayTask);
        if (completed == indexTask)
        {
            try
            {
                _ = await indexTask;
                runtime.State.SemanticSearchReady = runtime.SemanticIndex.Exists;
                Console.WriteLine("Semantic index built.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Semantic index build failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("Semantic index build still running in background; agent will proceed without it.");
            _ = indexTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    runtime.State.SemanticSearchReady = runtime.SemanticIndex.Exists;
                }
            }, TaskScheduler.Default);
        }
    }
    try
    {
        await runtime.Agent.RunAsync(task);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Agent failed: {ex.Message}");
    }
}, agentPrompt, sessionOption, newSessionOption);

var sessionsCommand = new Command("sessions", "List sessions");
sessionsCommand.SetHandler(() =>
{
    var config = AgentConfig.Load(Directory.GetCurrentDirectory());
    if (!Directory.Exists(config.SessionRoot))
    {
        Console.WriteLine("No sessions.");
        return;
    }
    foreach (var dir in Directory.GetDirectories(config.SessionRoot).OrderByDescending(x => x))
    {
        Console.WriteLine(Path.GetFileName(dir));
    }
});

root.AddCommand(tuiCommand);
root.AddCommand(chatCommand);
root.AddCommand(agentCommand);
root.AddCommand(sessionsCommand);

if (args.Length == 0)
{
    args = ["tui"];
}

return await root.InvokeAsync(args);
