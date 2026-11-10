using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

public sealed record NewLanguageModel(long Id, string Name, bool Enabled);

public sealed record NewAgentConfig(
    long Id, string Name, string Role, string TaskInstruction, string BehaviorInstruction, long? LanguageModelId);

public sealed record NewAgentCapability(long AgentId, string Capability, bool Enabled);

public sealed record NewAgentMetric(
    long Id, long AgentId, string? JiraKey, long InputTokens, long OutputTokens, long ToolCalls,
    long TaskDurationMs, long SuccessCount, long FailureCount, DateTime Created);

public sealed record NewAgentMetricTotals(
    long InputTokens, long OutputTokens, long ToolCalls, long TaskDurationMs, long SuccessCount, long FailureCount);

public sealed class NewLanguageModelRepository(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<NewLanguageModel> List(bool enabledOnly = false)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT id, name, enabled FROM new_language_models WHERE (@enabled_only = 0 OR enabled = 1) ORDER BY id");
        command.AddParameter("@enabled_only", enabledOnly ? 1 : 0);
        using var reader = command.ExecuteReader();
        var models = new List<NewLanguageModel>();
        while (reader.Read())
        {
            models.Add(new NewLanguageModel(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return models;
    }

    public void Upsert(string name, bool enabled)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO new_language_models (name, enabled) VALUES (@name, @enabled)
            ON DUPLICATE KEY UPDATE enabled = @enabled
            """);
        command.AddParameter("@name", name);
        command.AddParameter("@enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public bool SetEnabled(long id, bool enabled)
    {
        using var command = connectionFactory.CreateCommand("UPDATE new_language_models SET enabled = @enabled WHERE id = @id");
        command.AddParameter("@enabled", enabled ? 1 : 0);
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }
}

public sealed class NewAgentConfigRepository(IDbConnectionFactory connectionFactory)
{
    private const string Columns = "id, name, role, task_instruction, behavior_instruction, language_model_id";

    public IReadOnlyList<NewAgentConfig> List()
    {
        using var command = connectionFactory.CreateCommand($"SELECT {Columns} FROM new_agent_config ORDER BY id");
        using var reader = command.ExecuteReader();
        var agents = new List<NewAgentConfig>();
        while (reader.Read())
        {
            agents.Add(Map(reader));
        }

        return agents;
    }

    public NewAgentConfig? FindByName(string name)
    {
        using var command = connectionFactory.CreateCommand($"SELECT {Columns} FROM new_agent_config WHERE name = @name");
        command.AddParameter("@name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Upsert(
        string name, string role, string taskInstruction, string behaviorInstruction, long? languageModelId)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO new_agent_config (name, role, task_instruction, behavior_instruction, language_model_id)
            VALUES (@name, @role, @task_instruction, @behavior_instruction, @language_model_id)
            ON DUPLICATE KEY UPDATE role = @role, task_instruction = @task_instruction,
                behavior_instruction = @behavior_instruction, language_model_id = @language_model_id
            """);
        command.AddParameter("@name", name);
        command.AddParameter("@role", role);
        command.AddParameter("@task_instruction", taskInstruction);
        command.AddParameter("@behavior_instruction", behaviorInstruction);
        command.AddParameter("@language_model_id", languageModelId ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public bool Delete(long id)
    {
        using var command = connectionFactory.CreateCommand("DELETE FROM new_agent_config WHERE id = @id");
        command.AddParameter("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    private static NewAgentConfig Map(System.Data.IDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5));
}

public sealed class NewAgentCapabilityRepository(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<NewAgentCapability> List(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT agent_id, capability, enabled FROM new_agent_capabilities WHERE agent_id = @agent_id ORDER BY capability");
        command.AddParameter("@agent_id", agentId);
        using var reader = command.ExecuteReader();
        var capabilities = new List<NewAgentCapability>();
        while (reader.Read())
        {
            capabilities.Add(new NewAgentCapability(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return capabilities;
    }

    public void SetEnabled(long agentId, string capability, bool enabled)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO new_agent_capabilities (agent_id, capability, enabled) VALUES (@agent_id, @capability, @enabled)
            ON DUPLICATE KEY UPDATE enabled = @enabled
            """);
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@capability", capability);
        command.AddParameter("@enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }
}

public sealed class NewAgentMetricsRepository(IDbConnectionFactory connectionFactory)
{
    public void Record(
        long agentId, string? jiraKey, long inputTokens, long outputTokens, long toolCalls, long taskDurationMs,
        bool succeeded)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO new_agent_metrics (agent_id, jira_key, input_tokens, output_tokens, tool_calls,
                task_duration_ms, success_count, failure_count)
            VALUES (@agent_id, @jira_key, @input_tokens, @output_tokens, @tool_calls,
                @task_duration_ms, @success_count, @failure_count)
            """);
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@jira_key", jiraKey ?? (object)DBNull.Value);
        command.AddParameter("@input_tokens", inputTokens);
        command.AddParameter("@output_tokens", outputTokens);
        command.AddParameter("@tool_calls", toolCalls);
        command.AddParameter("@task_duration_ms", taskDurationMs);
        command.AddParameter("@success_count", succeeded ? 1L : 0L);
        command.AddParameter("@failure_count", succeeded ? 0L : 1L);
        command.ExecuteNonQuery();
    }

    public NewAgentMetricTotals GetTotals(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            """
            SELECT COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), COALESCE(SUM(tool_calls), 0),
                COALESCE(SUM(task_duration_ms), 0), COALESCE(SUM(success_count), 0), COALESCE(SUM(failure_count), 0)
            FROM new_agent_metrics WHERE agent_id = @agent_id
            """);
        command.AddParameter("@agent_id", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new NewAgentMetricTotals(
                Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)),
                Convert.ToInt64(reader.GetValue(4)), Convert.ToInt64(reader.GetValue(5)))
            : new NewAgentMetricTotals(0, 0, 0, 0, 0, 0);
    }
}
