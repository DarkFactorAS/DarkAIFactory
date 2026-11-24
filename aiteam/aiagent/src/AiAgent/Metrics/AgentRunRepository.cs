using DFCommonLib.DataAccess;

namespace AiAgent.Metrics;

public interface IAgentRunRepository
{
    /// <summary>Inserts the unfinished row and returns its id.</summary>
    long Insert(AgentRunStart start);

    void Finish(long id, AgentRunFinish finish);

    void Delete(long id);
}

/// <summary>agent_runs.</summary>
public sealed class AgentRunRepository(IDbConnectionFactory connectionFactory) : IAgentRunRepository
{
    public long Insert(AgentRunStart start)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_runs` (`agent_id`, `capability`, `issue_key`, `provider`, `model`, `started_at`) " +
            "VALUES (@agent_id, @capability, @issue_key, @provider, @model, @started_at); SELECT LAST_INSERT_ID();");
        command.AddParameter("@agent_id", start.AgentId);
        command.AddParameter("@capability", start.Capability.ToString());
        command.AddParameter("@issue_key", (object?)start.IssueKey ?? DBNull.Value);
        command.AddParameter("@provider", start.Provider);
        command.AddParameter("@model", (object?)start.Model ?? DBNull.Value);
        command.AddParameter("@started_at", start.StartedAt);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Finish(long id, AgentRunFinish finish)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE `agent_runs` SET `finished_at` = @finished_at, `llm_duration_ms` = @llm, `outcome` = @outcome, " +
            "`failure_reason` = @reason, `input_tokens` = @input, `output_tokens` = @output, " +
            "`cache_read_tokens` = @cache_read, `cache_write_tokens` = @cache_write, `cost_usd` = @cost, " +
            "`tool_calls` = @tools, `num_turns` = @turns WHERE `id` = @id");
        command.AddParameter("@id", id);
        command.AddParameter("@finished_at", finish.FinishedAt);
        command.AddParameter("@llm", Db(finish.LlmDurationMs));
        command.AddParameter("@outcome", finish.Outcome.ToString());
        command.AddParameter("@reason", Db(finish.FailureReason));
        command.AddParameter("@input", Db(finish.InputTokens));
        command.AddParameter("@output", Db(finish.OutputTokens));
        command.AddParameter("@cache_read", Db(finish.CacheReadTokens));
        command.AddParameter("@cache_write", Db(finish.CacheWriteTokens));
        command.AddParameter("@cost", Db(finish.CostUsd));
        command.AddParameter("@tools", Db(finish.ToolCalls));
        command.AddParameter("@turns", Db(finish.NumTurns));
        command.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        using var command = connectionFactory.CreateCommand("DELETE FROM `agent_runs` WHERE `id` = @id");
        command.AddParameter("@id", id);
        command.ExecuteNonQuery();
    }

    private static object Db<T>(T? value) => value is null ? DBNull.Value : value;
}
