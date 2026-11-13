namespace AiAgent.Core;

/// <summary>Capabilities of an agent, declared in execution order (Jira → Planning → Builder → Review).</summary>
public enum AgentCapabilityKind
{
    Jira = 0,
    Planning = 1,
    Builder = 2,
    Review = 3
}
