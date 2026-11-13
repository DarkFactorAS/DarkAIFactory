using System.Security.Cryptography;
using System.Text;
using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>MySQL named lock (GET_LOCK) so two agent processes never work on the same issue.</summary>
public sealed class AgentClaimLock(IDbConnectionFactory connectionFactory) : IAgentClaimLock
{
    public IDisposable? TryAcquire(long agentId, string issueKey)
    {
        var command = connectionFactory.CreateCommand("SELECT GET_LOCK(@lock_name, 0)");
        command.AddParameter("@lock_name", LockName(issueKey));
        try
        {
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                return command;
            }

            command.Dispose();
            return null;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    // Per issue (not per agent): a claim must exclude every agent.
    internal static string LockName(string issueKey) =>
        $"aiagent-issue-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issueKey.ToUpperInvariant())))[..48]}";
}
