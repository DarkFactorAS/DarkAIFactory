using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

public sealed class AgentMetricsRepository(IDbConnectionFactory connectionFactory)
{
    public void Record(AgentTaskMetric metric)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_task_metrics` (`agent_id`, `agent_task_id`, `jira_key`, `language_model`, "
            + "`input_tokens`, `output_tokens`, `tool_calls`, `duration_ms`, `succeeded`, `finished`) "
            + "VALUES (@agent_id, @agent_task_id, @jira_key, @language_model, @input_tokens, @output_tokens, "
            + "@tool_calls, @duration_ms, @succeeded, UTC_TIMESTAMP())");
        command.AddParameter("@agent_id", metric.AgentId);
        command.AddParameter("@agent_task_id", (object?)metric.AgentTaskId ?? DBNull.Value);
        command.AddParameter("@jira_key", (object?)metric.JiraKey ?? DBNull.Value);
        command.AddParameter("@language_model", metric.LanguageModel);
        command.AddParameter("@input_tokens", metric.TokensIn);
        command.AddParameter("@output_tokens", (object?)metric.TokensOut ?? DBNull.Value);
        command.AddParameter("@tool_calls", metric.ToolCalls);
        command.AddParameter("@duration_ms", metric.DurationMs);
        command.AddParameter("@succeeded", metric.Status == AgentRunStatus.Success);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AgentTaskMetric> GetForAgent(long agentId, int limit = 100)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `agent_id`, `agent_task_id`, `jira_key`, `language_model`, `input_tokens`, `output_tokens`, "
            + "`tool_calls`, `duration_ms`, `succeeded` FROM `agent_task_metrics` "
            + "WHERE `agent_id` = @agent_id ORDER BY `started` DESC, `id` DESC LIMIT @limit");
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@limit", limit);
        using var reader = command.ExecuteReader();
        var metrics = new List<AgentTaskMetric>();
        while (reader.Read())
        {
            var taskOrdinal = reader.GetOrdinal("agent_task_id");
            var jiraOrdinal = reader.GetOrdinal("jira_key");
            var outOrdinal = reader.GetOrdinal("output_tokens");
            metrics.Add(new AgentTaskMetric(
                reader.GetInt64(reader.GetOrdinal("agent_id")),
                reader.GetString(reader.GetOrdinal("language_model")),
                reader.GetInt64(reader.GetOrdinal("input_tokens")),
                reader.IsDBNull(outOrdinal) ? null : reader.GetInt64(outOrdinal),
                reader.GetInt32(reader.GetOrdinal("tool_calls")),
                reader.GetInt64(reader.GetOrdinal("duration_ms")),
                reader.GetBoolean(reader.GetOrdinal("succeeded")) ? AgentRunStatus.Success : AgentRunStatus.Failed,
                reader.IsDBNull(taskOrdinal) ? null : reader.GetInt64(taskOrdinal),
                reader.IsDBNull(jiraOrdinal) ? null : reader.GetString(jiraOrdinal)));
        }

        return metrics;
    }
}
