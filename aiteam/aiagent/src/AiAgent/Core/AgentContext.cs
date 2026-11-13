namespace AiAgent.Core;

/// <summary>Snapshot of the agent configuration handed to capabilities for one cycle.</summary>
public sealed class AgentContext(
    AgentDefinition agent,
    IReadOnlyCollection<string> projectKeys,
    IAgentClaimLock claimLock)
{
    public AgentDefinition Agent { get; } = agent;
    public long AgentId => Agent.Id;
    public IReadOnlyCollection<string> ProjectKeys { get; } = projectKeys;
    public IAgentClaimLock ClaimLock { get; } = claimLock;

    /// <summary>System prompt composed from the agent's role and behavior.</summary>
    public string SystemPrompt => PromptComposer.Compose(Agent);
}
