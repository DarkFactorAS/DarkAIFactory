using DFCommonLib.DataAccess;
using AiTeam.Configuration;

namespace CodingAgent.Repository;

internal sealed class ManagerPlanReader(IDbConnectionFactory connectionFactory)
{
    public ManagerPlan? Find(string jiraKey, long? projectId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT plan, posted_to_jira, requirements_hash, source_updated FROM manager_plans WHERE jira_key = @jira_key" +
            (projectId.HasValue ? " AND manager_project_id = @project_id" : string.Empty));
        command.AddParameter("@jira_key", jiraKey);
        if (projectId.HasValue)
        {
            command.AddParameter("@project_id", projectId.Value);
        }
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ManagerPlan(reader.GetString(0), reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }
}
