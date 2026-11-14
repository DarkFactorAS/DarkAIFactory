namespace AiAgent.Core;

/// <summary>
/// Ownership of an agent across aiagent containers: only the container holding the lock runs that agent's loop.
/// Disposing the handle releases the agent so another container can take it over.
/// </summary>
public interface IAgentOwnershipLock
{
    /// <summary>Returns a handle when this process now owns the agent, or null when another process does.</summary>
    IDisposable? TryAcquire(long agentId);
}
