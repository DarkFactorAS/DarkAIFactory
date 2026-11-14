using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>
/// MySQL named lock (GET_LOCK) per agent. The lock belongs to the connection held by the returned handle, so it is
/// released when the handle is disposed or when the container stops and its connection closes.
/// </summary>
public sealed class AgentOwnershipLock(IDbConnectionFactory connectionFactory) : IAgentOwnershipLock
{
    public IDisposable? TryAcquire(long agentId)
    {
        var command = connectionFactory.CreateCommand("SELECT GET_LOCK(@lock_name, 0)");
        command.AddParameter("@lock_name", LockName(agentId));
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

    internal static string LockName(long agentId) =>
        $"aiagent-agent-{agentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
