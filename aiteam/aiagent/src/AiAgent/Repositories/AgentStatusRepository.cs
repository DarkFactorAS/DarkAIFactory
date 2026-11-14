using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>Writes agent_status. One row per agent; rows are never deleted because agents are soft deleted.</summary>
public sealed class AgentStatusRepository(IDbConnectionFactory connectionFactory) : IAgentStatusWriter
{
    public void Set(long agentId, AgentState state, string? issueKey, AgentCapabilityKind? capability)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_status` (`agent_id`, `state`, `current_issue_key`, `current_capability`, `heartbeat_at`) " +
            "VALUES (@id, @state, @issue, @capability, NOW()) " +
            "ON DUPLICATE KEY UPDATE `state` = VALUES(`state`), `current_issue_key` = VALUES(`current_issue_key`), " +
            "`current_capability` = VALUES(`current_capability`), `heartbeat_at` = VALUES(`heartbeat_at`)");
        command.AddParameter("@id", agentId);
        command.AddParameter("@state", state.ToString());
        command.AddParameter("@issue", (object?)issueKey ?? DBNull.Value);
        command.AddParameter("@capability", (object?)capability?.ToString() ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Heartbeat(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_status` (`agent_id`, `state`, `heartbeat_at`) VALUES (@id, 'Idle', NOW()) " +
            "ON DUPLICATE KEY UPDATE `heartbeat_at` = VALUES(`heartbeat_at`)");
        command.AddParameter("@id", agentId);
        command.ExecuteNonQuery();
    }
}
