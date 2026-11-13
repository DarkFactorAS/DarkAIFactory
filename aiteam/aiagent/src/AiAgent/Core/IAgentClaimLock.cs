namespace AiAgent.Core;

/// <summary>Cross-process claim on a unit of work (an issue handled by an agent).</summary>
public interface IAgentClaimLock
{
    /// <summary>Returns a handle that releases the claim on dispose, or null when already claimed.</summary>
    IDisposable? TryAcquire(long agentId, string issueKey);
}
