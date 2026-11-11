namespace AiAgent.Repositories;

public interface IAgentConfigRepository
{
    /// <summary>Returns the agent with enabled language model and enabled capabilities, or null.</summary>
    Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);
}
