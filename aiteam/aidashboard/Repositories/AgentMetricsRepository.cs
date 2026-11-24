using DFCommonLib.DataAccess;

namespace AiDashboard.Agents;

/// <summary>Aggregated usage of one agent over one window. Null means N/A (no run reported the value).</summary>
public sealed record AgentMetricsTotals(
    int Succeeded, int Failed,
    double? WallClockMs, double? AvgWallClockMs,
    double? LlmDurationMs, double? AvgLlmDurationMs,
    long? InputTokens, long? OutputTokens, long? ToolCalls, decimal? CostUsd);

public sealed record AgentMetrics(long AgentId, AgentMetricsTotals AllTime, AgentMetricsTotals Last7Days);

/// <summary>One agent_runs row, reduced to the columns that are aggregated.</summary>
public sealed record AgentRunRow(
    long AgentId, DateTime StartedAt, DateTime? FinishedAt, string? Outcome, long? LlmDurationMs,
    long? InputTokens, long? OutputTokens, int? ToolCalls, decimal? CostUsd);

public interface IAgentMetricsRepository
{
    IReadOnlyList<AgentMetrics> List();
}

public static class AgentMetricsAggregator
{
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);

    public static IReadOnlyList<AgentMetrics> Aggregate(
        IEnumerable<long> agentIds, IEnumerable<AgentRunRow> runs, DateTime utcNow)
    {
        var since = utcNow - RecentWindow;
        var byAgent = runs.ToLookup(r => r.AgentId);
        return agentIds.Distinct().Select(id =>
        {
            var all = byAgent[id].ToList();
            return new AgentMetrics(id, Totals(all), Totals(all.Where(r => r.StartedAt >= since).ToList()));
        }).ToList();
    }

    private static AgentMetricsTotals Totals(List<AgentRunRow> runs)
    {
        var wall = runs.Where(r => r.FinishedAt is not null)
            .Select(r => (double?)Math.Max(0, (r.FinishedAt!.Value - r.StartedAt).TotalMilliseconds)).ToList();
        var llm = runs.Where(r => r.LlmDurationMs is not null).Select(r => (double?)r.LlmDurationMs).ToList();
        return new AgentMetricsTotals(
            runs.Count(r => r.Outcome == "Succeeded"),
            runs.Count(r => r.Outcome == "Failed"),
            Sum(wall), Average(wall), Sum(llm), Average(llm),
            Sum(runs.Select(r => r.InputTokens)),
            Sum(runs.Select(r => r.OutputTokens)),
            Sum(runs.Select(r => (long?)r.ToolCalls)),
            Sum(runs.Select(r => r.CostUsd)));
    }

    private static double? Sum(List<double?> values) => values.Count == 0 ? null : values.Sum(v => v!.Value);
    private static double? Average(List<double?> values) => values.Count == 0 ? null : values.Average(v => v!.Value);

    private static long? Sum(IEnumerable<long?> values)
    {
        var present = values.Where(v => v is not null).ToList();
        return present.Count == 0 ? null : present.Sum(v => v!.Value);
    }

    private static decimal? Sum(IEnumerable<decimal?> values)
    {
        var present = values.Where(v => v is not null).ToList();
        return present.Count == 0 ? null : present.Sum(v => v!.Value);
    }
}

public sealed class AgentMetricsRepository(IDbConnectionFactory connectionFactory) : IAgentMetricsRepository
{
    public IReadOnlyList<AgentMetrics> List()
    {
        var ids = new List<long>();
        using (var command = connectionFactory.CreateCommand(
            "SELECT id FROM agent_definitions WHERE deleted_at IS NULL ORDER BY id"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        var runs = new List<AgentRunRow>();
        using (var command = connectionFactory.CreateCommand(
            "SELECT r.agent_id, r.started_at, r.finished_at, r.outcome, r.llm_duration_ms, r.input_tokens, " +
            "r.output_tokens, r.tool_calls, r.cost_usd FROM agent_runs r " +
            "JOIN agent_definitions a ON a.id = r.agent_id WHERE a.deleted_at IS NULL"))
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                runs.Add(new AgentRunRow(
                    reader.GetInt64(0), reader.GetDateTime(1),
                    reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4)),
                    reader.IsDBNull(5) ? null : Convert.ToInt64(reader.GetValue(5)),
                    reader.IsDBNull(6) ? null : Convert.ToInt64(reader.GetValue(6)),
                    reader.IsDBNull(7) ? null : Convert.ToInt32(reader.GetValue(7)),
                    reader.IsDBNull(8) ? null : Convert.ToDecimal(reader.GetValue(8))));
            }
        }

        return AgentMetricsAggregator.Aggregate(ids, runs, DateTime.UtcNow);
    }
}
