using System.Security.Cryptography;
using System.Text;
using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>
/// MySQL named lock (GET_LOCK) so two agent processes never work on the same issue. Disposing the handle releases the
/// lock (RELEASE_LOCK on its own connection), so the next agent can claim the issue as soon as a capability is done.
/// </summary>
public sealed class AgentClaimLock(IDbConnectionFactory connectionFactory) : IAgentClaimLock
{
    public IDisposable? TryAcquire(long agentId, string issueKey) =>
        MySqlNamedLock.TryAcquire(connectionFactory, LockName(issueKey));

    // Per issue (not per agent): a claim must exclude every agent.
    internal static string LockName(string issueKey) =>
        $"aiagent-issue-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issueKey.ToUpperInvariant())))[..48]}";
}
