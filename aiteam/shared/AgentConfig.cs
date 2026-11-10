using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

public static class TaskMetricStatus
{
    public const string Success = "success";
    public const string Failed = "failed";

    public static bool IsValid(string? status) => status is Success or Failed;
}

public sealed record LanguageModel(long Id, string ModelId, string DisplayName, bool Enabled);

public sealed record AgentDefinition(
    long Id, string Name, string Role, string Instructions, long LanguageModelId,
    DateTime Created, DateTime Updated);

public sealed record AgentCapability(long Id, long AgentId, string Name, bool Enabled);

public sealed record TaskMetric(
    long Id, long AgentId, string TaskKey, long? TokensIn, long? TokensOut, int ToolCallCount,
    long DurationMs, string Status, DateTime Started, DateTime? Completed);

public sealed class LanguageModelRepository(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<LanguageModel> List(bool enabledOnly = false)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT id, model_id, display_name, enabled FROM language_models " +
            "WHERE (@enabled_only = 0 OR enabled = 1) ORDER BY id");
        command.AddParameter("@enabled_only", enabledOnly ? 1 : 0);
        using var reader = command.ExecuteReader();
        var models = new List<LanguageModel>();
        while (reader.Read())
        {
            models.Add(new LanguageModel(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        return models;
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var command = connectionFactory.CreateCommand("UPDATE language_models SET enabled = @enabled WHERE id = @id");
        command.AddParameter("@enabled", enabled ? 1 : 0);
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }
}

public sealed class AgentDefinitionRepository(IDbConnectionFactory connectionFactory)
{
    private const string Columns = "id, name, role, instructions, language_model_id, created, updated";

    public IReadOnlyList<AgentDefinition> List()
    {
        using var command = connectionFactory.CreateCommand($"SELECT {Columns} FROM agent_definitions ORDER BY id");
        using var reader = command.ExecuteReader();
        var agents = new List<AgentDefinition>();
        while (reader.Read())
        {
            agents.Add(Map(reader));
        }

        return agents;
    }

    public AgentDefinition? FindByName(string name)
    {
        using var command = connectionFactory.CreateCommand($"SELECT {Columns} FROM agent_definitions WHERE name = @name");
        command.AddParameter("@name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<AgentCapability> ListCapabilities(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT id, agent_id, name, enabled FROM agent_capabilities WHERE agent_id = @agent_id ORDER BY name");
        command.AddParameter("@agent_id", agentId);
        using var reader = command.ExecuteReader();
        var capabilities = new List<AgentCapability>();
        while (reader.Read())
        {
            capabilities.Add(new AgentCapability(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3)));
        }

        return capabilities;
    }

    public void SetCapability(long agentId, string name, bool enabled)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO agent_capabilities (agent_id, name, enabled) VALUES (@agent_id, @name, @enabled)
            ON DUPLICATE KEY UPDATE enabled = VALUES(enabled)
            """);
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@name", name);
        command.AddParameter("@enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static AgentDefinition Map(System.Data.IDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt64(4), reader.GetDateTime(5), reader.GetDateTime(6));
}

public sealed class TaskMetricRepository(IDbConnectionFactory connectionFactory)
{
    public void Record(
        long agentId, string taskKey, long? tokensIn, long? tokensOut, int toolCallCount,
        long durationMs, string status, DateTime started, DateTime? completed)
    {
        if (!TaskMetricStatus.IsValid(status))
        {
            throw new ArgumentException("Status must be 'success' or 'failed'.", nameof(status));
        }

        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO task_metrics (agent_id, task_key, tokens_in, tokens_out, tool_call_count,
                duration_ms, status, started, completed)
            VALUES (@agent_id, @task_key, @tokens_in, @tokens_out, @tool_call_count,
                @duration_ms, @status, @started, @completed)
            """);
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@task_key", taskKey);
        command.AddParameter("@tokens_in", tokensIn);
        command.AddParameter("@tokens_out", tokensOut);
        command.AddParameter("@tool_call_count", toolCallCount);
        command.AddParameter("@duration_ms", durationMs);
        command.AddParameter("@status", status);
        command.AddParameter("@started", started);
        command.AddParameter("@completed", completed);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TaskMetric> ListForTask(string taskKey)
    {
        using var command = connectionFactory.CreateCommand(
            """
            SELECT id, agent_id, task_key, tokens_in, tokens_out, tool_call_count, duration_ms, status, started, completed
            FROM task_metrics WHERE task_key = @task_key ORDER BY id
            """);
        command.AddParameter("@task_key", taskKey);
        using var reader = command.ExecuteReader();
        var metrics = new List<TaskMetric>();
        while (reader.Read())
        {
            metrics.Add(new TaskMetric(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetInt32(5), reader.GetInt64(6), reader.GetString(7), reader.GetDateTime(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
        }

        return metrics;
    }
}
