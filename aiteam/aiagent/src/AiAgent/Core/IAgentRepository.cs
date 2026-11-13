namespace AiAgent.Core;

public interface IAgentRepository
{
    IReadOnlyList<AgentDefinition> GetEnabledAgents();

    /// <summary>Returns the agent row or null when it does not exist.</summary>
    AgentDefinition? GetAgent(long agentId);

    IReadOnlySet<AgentCapabilityKind> GetEnabledCapabilities(long agentId);

    IReadOnlyList<string> GetProjectKeys(long agentId);
}
