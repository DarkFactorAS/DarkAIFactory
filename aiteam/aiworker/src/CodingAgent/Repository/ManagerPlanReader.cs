using DFCommonLib.DataAccess;

namespace CodingAgent.Repository;

/// <summary>
/// Read-only access to the plans the manager agent stores in `manager_plans`.
/// The manager saves the plan before adding the analyzed label, so a plan is
/// guaranteed to exist for issues selected by the default worker query.
/// </summary>
internal sealed class ManagerPlanReader(IDbConnectionFactory connectionFactory)
{
    public string? Find(string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT plan FROM manager_plans WHERE jira_key = @jira_key");
        command.AddParameter("@jira_key", jiraKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }
}
