namespace AiAgent.Core;

public interface IAgentCapability
{
    AgentCapabilityKind Kind { get; }

    Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken);
}
