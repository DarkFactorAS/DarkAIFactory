using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

internal sealed class ManagerProjectRepository(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<ManagerProject> GetProjects()
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT id, jira_project_key, target_repository_url FROM manager_projects ORDER BY id");
        using var reader = command.ExecuteReader();
        var projects = new List<ManagerProject>();
        while (reader.Read())
        {
            projects.Add(new ManagerProject(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetString(reader.GetOrdinal("jira_project_key")),
                reader.GetString(reader.GetOrdinal("target_repository_url"))));
        }

        return projects;
    }
}
