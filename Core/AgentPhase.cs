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
/// The ordered set of phases the agent will execute for a given task. Built by <see cref="TriageRunner"/>.
/// </summary>
public sealed record PhasePlan(
    TaskKind Kind,
    IReadOnlyList<AgentPhase> Phases,
    bool IsGreenfield,
    string Goal);
