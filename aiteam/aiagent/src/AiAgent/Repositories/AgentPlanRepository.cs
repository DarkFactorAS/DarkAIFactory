using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

public sealed record AgentPlan(long AgentId, long ManagerProjectId, string JiraKey, string Plan, bool PostedToJira);

/// <summary>agent_plans.</summary>
public sealed class AgentPlanRepository(IDbConnectionFactory connectionFactory)
{
    public AgentPlan? Get(long agentId, string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `agent_id`, `manager_project_id`, `jira_key`, `plan`, `posted_to_jira` " +
            "FROM `agent_plans` WHERE `agent_id` = @agent_id AND `jira_key` = @jira_key");
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@jira_key", jiraKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AgentPlan(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetBoolean(4))
            : null;
    }

    public void Save(AgentPlan plan)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_plans` (`agent_id`, `manager_project_id`, `jira_key`, `plan`, `posted_to_jira`) " +
            "VALUES (@agent_id, @project_id, @jira_key, @plan, @posted) " +
            "ON DUPLICATE KEY UPDATE `manager_project_id` = VALUES(`manager_project_id`), " +
            "`plan` = VALUES(`plan`), `posted_to_jira` = VALUES(`posted_to_jira`)");
        command.AddParameter("@agent_id", plan.AgentId);
        command.AddParameter("@project_id", plan.ManagerProjectId);
        command.AddParameter("@jira_key", plan.JiraKey);
        command.AddParameter("@plan", plan.Plan);
        command.AddParameter("@posted", plan.PostedToJira);
        command.ExecuteNonQuery();
    }
}
