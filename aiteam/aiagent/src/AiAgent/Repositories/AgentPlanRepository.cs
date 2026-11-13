using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

public sealed record AgentPlan(
    long AgentId, long ManagerProjectId, string JiraKey, string Plan, bool PostedToJira,
    string? RequirementsHash = null, string? SourceUpdated = null);

public interface IAgentPlanRepository
{
    AgentPlan? Get(long agentId, string jiraKey);
    void Save(AgentPlan plan);
}

/// <summary>agent_plans.</summary>
public sealed class AgentPlanRepository(IDbConnectionFactory connectionFactory) : IAgentPlanRepository
{
    public AgentPlan? Get(long agentId, string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `agent_id`, `manager_project_id`, `jira_key`, `plan`, `posted_to_jira`, `requirements_hash`, `source_updated` " +
            "FROM `agent_plans` WHERE `agent_id` = @agent_id AND `jira_key` = @jira_key");
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@jira_key", jiraKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentPlan(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6))
            : null;
    }

    public void Save(AgentPlan plan)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_plans` (`agent_id`, `manager_project_id`, `jira_key`, `plan`, `posted_to_jira`, `requirements_hash`, `source_updated`) " +
            "VALUES (@agent_id, @project_id, @jira_key, @plan, @posted, @hash, @source_updated) " +
            "ON DUPLICATE KEY UPDATE `manager_project_id` = VALUES(`manager_project_id`), " +
            "`plan` = VALUES(`plan`), `posted_to_jira` = VALUES(`posted_to_jira`), " +
            "`requirements_hash` = VALUES(`requirements_hash`), `source_updated` = VALUES(`source_updated`)");
        command.AddParameter("@agent_id", plan.AgentId);
        command.AddParameter("@project_id", plan.ManagerProjectId);
        command.AddParameter("@jira_key", plan.JiraKey);
        command.AddParameter("@plan", plan.Plan);
        command.AddParameter("@posted", plan.PostedToJira);
        command.AddParameter("@hash", plan.RequirementsHash);
        command.AddParameter("@source_updated", plan.SourceUpdated);
        command.ExecuteNonQuery();
    }
}
