using DFCommonLib.DataAccess;

namespace ManagerAgent.Repository;

internal sealed record ManagerPlan(string Plan, bool PostedToJira);

internal sealed class ManagerPlanRepository(IDbConnectionFactory connectionFactory)
{
    public ManagerPlan? Find(string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT plan, posted_to_jira FROM manager_plans WHERE jira_key = @jira_key");
        command.AddParameter("@jira_key", jiraKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ManagerPlan(reader.GetString(0), reader.GetBoolean(1))
            : null;
    }

    public void Save(long projectId, string jiraKey, string plan)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO manager_plans (manager_project_id, jira_key, plan) VALUES (@project_id, @jira_key, @plan)");
        command.AddParameter("@project_id", projectId);
        command.AddParameter("@jira_key", jiraKey);
        command.AddParameter("@plan", plan);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Could not save the plan for {jiraKey}.");
        }
    }

    public void MarkPostedToJira(string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE manager_plans SET posted_to_jira = 1 WHERE jira_key = @jira_key");
        command.AddParameter("@jira_key", jiraKey);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Could not mark the saved plan for {jiraKey} as posted to Jira.");
        }
    }
}
