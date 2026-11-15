using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>
/// MySQL named lock (GET_LOCK) per agent. The lock belongs to the connection held by the returned handle, so it is
/// released (RELEASE_LOCK) when the handle is disposed, or when the container stops and its connection closes.
/// </summary>
public sealed class AgentOwnershipLock(IDbConnectionFactory connectionFactory) : IAgentOwnershipLock
{
    public IDisposable? TryAcquire(long agentId) =>
        MySqlNamedLock.TryAcquire(connectionFactory, LockName(agentId));

    internal static string LockName(long agentId) =>
        $"aiagent-agent-{agentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
