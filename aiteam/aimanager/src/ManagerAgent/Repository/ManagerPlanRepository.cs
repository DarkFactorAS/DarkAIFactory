using DFCommonLib.DataAccess;
using AiTeam.Configuration;

namespace ManagerAgent.Repository;

internal sealed class ManagerPlanRepository(IDbConnectionFactory connectionFactory)
{
    public ManagerPlan? Find(string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT manager_project_id, plan, posted_to_jira, requirements_hash, source_updated FROM manager_plans WHERE jira_key = @jira_key");
        command.AddParameter("@jira_key", jiraKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ManagerPlan(reader.GetString(1), reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(0))
            : null;
    }

    public void RebindProject(long projectId, string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE manager_plans SET manager_project_id = @project_id WHERE jira_key = @jira_key");
        command.AddParameter("@project_id", projectId);
        command.AddParameter("@jira_key", jiraKey);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Could not rebind the plan for {jiraKey} to manager project {projectId}.");
        }
    }

    public void Save(long projectId, string jiraKey, string plan, string requirementsHash, string sourceUpdated)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO manager_plans (manager_project_id, jira_key, plan, requirements_hash, source_updated)
            VALUES (@project_id, @jira_key, @plan, @requirements_hash, @source_updated)
            ON DUPLICATE KEY UPDATE manager_project_id = VALUES(manager_project_id), plan = VALUES(plan),
                requirements_hash = VALUES(requirements_hash), source_updated = VALUES(source_updated),
                posted_to_jira = 0, created = CURRENT_TIMESTAMP
            """);
        command.AddParameter("@project_id", projectId);
        command.AddParameter("@jira_key", jiraKey);
        command.AddParameter("@plan", plan);
        command.AddParameter("@requirements_hash", requirementsHash);
        command.AddParameter("@source_updated", sourceUpdated);
        if (command.ExecuteNonQuery() is not (1 or 2))
        {
            throw new InvalidOperationException($"Could not save the plan for {jiraKey}.");
        }
    }

    public void MarkPostedToJira(string jiraKey, string requirementsHash)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE manager_plans SET posted_to_jira = 1 WHERE jira_key = @jira_key AND requirements_hash = @requirements_hash");
        command.AddParameter("@jira_key", jiraKey);
        command.AddParameter("@requirements_hash", requirementsHash);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Could not mark the saved plan for {jiraKey} as posted to Jira.");
        }
    }

    public void ReplacePlan(string jiraKey, string requirementsHash, string plan)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE manager_plans SET plan = @plan WHERE jira_key = @jira_key AND requirements_hash = @requirements_hash");
        command.AddParameter("@plan", plan);
        command.AddParameter("@jira_key", jiraKey);
        command.AddParameter("@requirements_hash", requirementsHash);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Could not update the saved plan for {jiraKey}.");
        }
    }
}
