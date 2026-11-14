namespace AiAgent.Core;

public enum AgentState
{
    Idle,
    Running,
    Error,
    Disabled
}

/// <summary>Writes the live status of an agent (agent_status) that the dashboard shows.</summary>
public interface IAgentStatusWriter
{
    /// <summary>Sets state, current issue and capability, and refreshes the heartbeat.</summary>
    void Set(long agentId, AgentState state, string? issueKey, AgentCapabilityKind? capability);

    /// <summary>Refreshes only the heartbeat.</summary>
    void Heartbeat(long agentId);
}
