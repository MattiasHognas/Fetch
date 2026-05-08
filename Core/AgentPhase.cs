namespace Fetch.Core;

public enum AgentPhase
{
    Triage,
    Discovery,
    Planning,
    Editing,
    Verification,
    Answering
}

/// <summary>
/// The ordered set of phases the agent will execute for a given task. Built by <see cref="Fetch.Planning.TriageRunner"/>.
/// </summary>
public sealed record PhasePlan(
    Fetch.Planning.TaskKind Kind,
    IReadOnlyList<AgentPhase> Phases,
    bool IsGreenfield,
    string Goal);
