using System.Data;
using DFCommonLib.DataAccess;

namespace AiDashboard.Agents;

public sealed record AgentCapabilityView(string Capability, bool Enabled);

public sealed record AgentStatusView(
    string State, string? CurrentIssueKey, string? CurrentCapability, DateTime? HeartbeatAt);

public sealed record AgentView(
    long Id, string Name, string Role, string Behavior, string LlmProvider, string? LlmModel, bool Enabled,
    DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<AgentCapabilityView> Capabilities,
    IReadOnlyList<string> Projects, AgentStatusView? Status);

public sealed record AgentWrite(
    string Name, string Role, string Behavior, string LlmProvider, string? LlmModel, bool Enabled,
    IReadOnlyList<AgentCapabilityView> Capabilities, IReadOnlyList<string> Projects);

public interface IAgentDefinitionRepository
{
    IReadOnlyList<AgentView> List();
    AgentView? Find(long id);
    bool NameExists(string name, long? excludeId);
    IReadOnlyList<string> ExistingProjectKeys(IReadOnlyCollection<string> keys);
    IReadOnlyList<string> ListProjectKeys();
    AgentView Create(AgentWrite agent);
    AgentView? Update(long id, AgentWrite agent);
    bool SetEnabled(long id, bool enabled);
    bool SoftDelete(long id);
}

public sealed class AgentDefinitionRepository(IDbConnectionFactory connectionFactory) : IAgentDefinitionRepository
{
    private const string AgentColumns =
        "id, name, role, behavior, llm_provider, llm_model, enabled, created_at, updated_at";

    public IReadOnlyList<AgentView> List() => Load(null);

    // 0 is the "all agents" sentinel in Load, so nonpositive IDs must never reach it.
    public AgentView? Find(long id) => id > 0 ? Load(id).FirstOrDefault() : null;

    private List<AgentView> Load(long? id)
    {
        var agents = new List<(long Id, string Name, string Role, string Behavior, string Provider, string? Model,
            bool Enabled, DateTime Created, DateTime Updated)>();
        using (var command = connectionFactory.CreateCommand(
            $"SELECT {AgentColumns} FROM agent_definitions WHERE deleted_at IS NULL " +
            "AND (@id = 0 OR id = @id) ORDER BY name, id"))
        {
            command.AddParameter("@id", id ?? 0L);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                agents.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    Convert.ToInt64(reader.GetValue(6)) != 0, reader.GetDateTime(7), reader.GetDateTime(8)));
            }
        }

        var capabilities = new Dictionary<long, List<AgentCapabilityView>>();
        using (var command = connectionFactory.CreateCommand(
            "SELECT c.agent_id, c.capability, c.enabled FROM agent_capabilities c " +
            "JOIN agent_definitions a ON a.id = c.agent_id WHERE a.deleted_at IS NULL " +
            "AND (@id = 0 OR a.id = @id) ORDER BY c.agent_id, c.capability"))
        {
            command.AddParameter("@id", id ?? 0L);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Add(capabilities, reader.GetInt64(0),
                    new AgentCapabilityView(reader.GetString(1), Convert.ToInt64(reader.GetValue(2)) != 0));
            }
        }

        var projects = new Dictionary<long, List<string>>();
        using (var command = connectionFactory.CreateCommand(
            "SELECT p.agent_id, p.project_key FROM agent_projects p " +
            "JOIN agent_definitions a ON a.id = p.agent_id WHERE a.deleted_at IS NULL " +
            "AND (@id = 0 OR a.id = @id) ORDER BY p.agent_id, p.project_key"))
        {
            command.AddParameter("@id", id ?? 0L);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Add(projects, reader.GetInt64(0), reader.GetString(1));
            }
        }

        var statuses = new Dictionary<long, AgentStatusView>();
        using (var command = connectionFactory.CreateCommand(
            "SELECT s.agent_id, s.state, s.current_issue_key, s.current_capability, s.heartbeat_at " +
            "FROM agent_definitions a LEFT JOIN agent_status s ON s.agent_id = a.id " +
            "WHERE a.deleted_at IS NULL AND s.agent_id IS NOT NULL AND (@id = 0 OR a.id = @id)"))
        {
            command.AddParameter("@id", id ?? 0L);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                statuses[reader.GetInt64(0)] = new AgentStatusView(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDateTime(4));
            }
        }

        return agents.Select(a => new AgentView(
            a.Id, a.Name, a.Role, a.Behavior, a.Provider, a.Model, a.Enabled, a.Created, a.Updated,
            capabilities.GetValueOrDefault(a.Id) ?? [],
            projects.GetValueOrDefault(a.Id) ?? [],
            statuses.GetValueOrDefault(a.Id))).ToList();
    }

    private static void Add<T>(Dictionary<long, List<T>> map, long key, T value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    public bool NameExists(string name, long? excludeId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT COUNT(*) FROM agent_definitions WHERE deleted_at IS NULL AND name = @name AND id <> @id");
        command.AddParameter("@name", name);
        command.AddParameter("@id", excludeId ?? 0L);
        using var reader = command.ExecuteReader();
        return reader.Read() && Convert.ToInt64(reader.GetValue(0)) > 0;
    }

    public IReadOnlyList<string> ListProjectKeys()
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT jira_project_key FROM manager_projects ORDER BY jira_project_key");
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public IReadOnlyList<string> ExistingProjectKeys(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var names = keys.Select((_, i) => $"@k{i}").ToArray();
        using var command = connectionFactory.CreateCommand(
            $"SELECT jira_project_key FROM manager_projects WHERE jira_project_key IN ({string.Join(", ", names)})");
        var index = 0;
        foreach (var key in keys)
        {
            command.AddParameter(names[index++], key);
        }

        using var reader = command.ExecuteReader();
        var found = new List<string>();
        while (reader.Read())
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    public AgentView Create(AgentWrite agent)
    {
        long id = 0;
        InTransaction((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO agent_definitions (name, role, behavior, llm_provider, llm_model, enabled) " +
                "VALUES (@name, @role, @behavior, @provider, @model, @enabled)", agent);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT LAST_INSERT_ID()";
            id = Convert.ToInt64(command.ExecuteScalar());
            ReplaceChildren(connection, transaction, id, agent);
        });
        return Find(id) ?? throw new InvalidOperationException("Could not read back the created agent.");
    }

    public AgentView? Update(long id, AgentWrite agent)
    {
        var updated = false;
        InTransaction((connection, transaction) =>
        {
            updated = Execute(connection, transaction,
                "UPDATE agent_definitions SET name = @name, role = @role, behavior = @behavior, " +
                "llm_provider = @provider, llm_model = @model, enabled = @enabled " +
                "WHERE id = @id AND deleted_at IS NULL", agent, id) > 0;
            if (updated)
            {
                ReplaceChildren(connection, transaction, id, agent);
            }
        });
        return updated ? Find(id) : null;
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE agent_definitions SET enabled = @enabled WHERE id = @id AND deleted_at IS NULL");
        command.AddParameter("@enabled", enabled ? 1 : 0);
        command.AddParameter("@id", id);
        command.ExecuteNonQuery();
        return Find(id) is not null;
    }

    public bool SoftDelete(long id)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE agent_definitions SET deleted_at = NOW(), enabled = FALSE WHERE id = @id AND deleted_at IS NULL");
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    private void InTransaction(Action<IDbConnection, IDbTransaction> work)
    {
        using var connection = connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            work(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static int Execute(
        IDbConnection connection, IDbTransaction transaction, string sql, AgentWrite agent, long? id = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Bind(command, "@name", agent.Name);
        Bind(command, "@role", agent.Role);
        Bind(command, "@behavior", agent.Behavior);
        Bind(command, "@provider", agent.LlmProvider);
        Bind(command, "@model", agent.LlmModel);
        Bind(command, "@enabled", agent.Enabled ? 1 : 0);
        if (id is not null)
        {
            Bind(command, "@id", id.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static void ReplaceChildren(IDbConnection connection, IDbTransaction transaction, long id, AgentWrite agent)
    {
        NonQuery(connection, transaction, "DELETE FROM agent_capabilities WHERE agent_id = @id", ("@id", id));
        NonQuery(connection, transaction, "DELETE FROM agent_projects WHERE agent_id = @id", ("@id", id));
        foreach (var capability in agent.Capabilities)
        {
            NonQuery(connection, transaction,
                "INSERT INTO agent_capabilities (agent_id, capability, enabled) VALUES (@id, @capability, @enabled)",
                ("@id", id), ("@capability", capability.Capability), ("@enabled", capability.Enabled ? 1 : 0));
        }

        foreach (var project in agent.Projects)
        {
            NonQuery(connection, transaction,
                "INSERT INTO agent_projects (agent_id, project_key) VALUES (@id, @project)",
                ("@id", id), ("@project", project));
        }
    }

    private static void NonQuery(
        IDbConnection connection, IDbTransaction transaction, string sql, params (string Name, object Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            Bind(command, name, value);
        }

        command.ExecuteNonQuery();
    }

    private static void Bind(IDbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
