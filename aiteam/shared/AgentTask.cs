using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

public static class AgentTaskStatus
{
    public const string New = "New";
    public const string Created = "Created";
    public const string Failed = "Failed";

    public static bool IsValid(string? status) => status is New or Created or Failed;
}

public sealed record AgentTask(
    long Id, string ProjectKey, string Title, string Description, string Status,
    string? JiraKey, DateTime Created, DateTime Updated);

public interface IAgentTaskRepository
{
    IReadOnlyList<AgentTask> List(string? status = null);
    AgentTask? Find(long id);
    AgentTask Create(string projectKey, string title, string description);
    AgentTask? Update(long id, string projectKey, string title, string description, string status, string? jiraKey);
}

public sealed class AgentTaskRepository(IDbConnectionFactory connectionFactory) : IAgentTaskRepository
{
    private const string Columns = "id, project_key, title, description, status, jira_key, created, updated";

    public IReadOnlyList<AgentTask> List(string? status = null)
    {
        using var command = connectionFactory.CreateCommand(
            $"SELECT {Columns} FROM agent_tasks WHERE (@status = '' OR status = @status) ORDER BY id DESC");
        command.AddParameter("@status", status ?? string.Empty);
        using var reader = command.ExecuteReader();
        var tasks = new List<AgentTask>();
        while (reader.Read())
        {
            tasks.Add(Map(reader));
        }

        return tasks;
    }

    public AgentTask? Find(long id)
    {
        using var command = connectionFactory.CreateCommand($"SELECT {Columns} FROM agent_tasks WHERE id = @id");
        command.AddParameter("@id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public AgentTask Create(string projectKey, string title, string description)
    {
        long id;
        using (var command = connectionFactory.CreateCommand(
            """
            INSERT INTO agent_tasks (project_key, title, description, status)
            VALUES (@project_key, @title, @description, 'New');
            SELECT LAST_INSERT_ID()
            """))
        {
            command.AddParameter("@project_key", projectKey);
            command.AddParameter("@title", title);
            command.AddParameter("@description", description);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Could not create the agent task.");
            }

            id = Convert.ToInt64(reader.GetValue(0));
        }

        return Find(id) ?? throw new InvalidOperationException("Could not read back the created agent task.");
    }

    public AgentTask? Update(
        long id, string projectKey, string title, string description, string status, string? jiraKey)
    {
        using (var command = connectionFactory.CreateCommand(
            """
            UPDATE agent_tasks SET project_key = @project_key, title = @title, description = @description,
                status = @status, jira_key = NULLIF(@jira_key, '')
            WHERE id = @id
            """))
        {
            command.AddParameter("@project_key", projectKey);
            command.AddParameter("@title", title);
            command.AddParameter("@description", description);
            command.AddParameter("@status", status);
            command.AddParameter("@jira_key", jiraKey ?? string.Empty);
            command.AddParameter("@id", id);
            command.ExecuteNonQuery();
        }

        return Find(id);
    }

    private static AgentTask Map(System.Data.IDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetDateTime(6), reader.GetDateTime(7));
}
