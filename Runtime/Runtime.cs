namespace Fetch.Runtime;

public sealed record Runtime(
    AgentSession Session,
    LlmClient Llm,
    TodoStore TodoStore,
    AgentLoop Agent,
    SlashCommandHandler Slash,
    AgentConfig Config,
    PathSandbox Sandbox,
    AgentRuntimeState State,
    AgentEventStore Events,
    ITool[] Tools,
    SemanticIndex SemanticIndex);
