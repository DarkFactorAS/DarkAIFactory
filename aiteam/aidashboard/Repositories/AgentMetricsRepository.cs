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
    private const int ColumnsPerWindow = 10;

    // Aggregates are computed by the database so each poll transfers one row per agent instead of all run history.
    // filter selects the window: "1 = 1" for all time, "r.started_at >= @since" for the recent window.
    private static string WindowColumns(string filter) =>
        $"SUM(CASE WHEN {filter} AND r.outcome = 'Succeeded' THEN 1 ELSE 0 END), " +
        $"SUM(CASE WHEN {filter} AND r.outcome = 'Failed' THEN 1 ELSE 0 END), " +
        $"SUM(CASE WHEN {filter} AND r.finished_at IS NOT NULL THEN {WallClockMs} END), " +
        $"AVG(CASE WHEN {filter} AND r.finished_at IS NOT NULL THEN {WallClockMs} END), " +
        $"SUM(CASE WHEN {filter} THEN r.llm_duration_ms END), " +
        $"AVG(CASE WHEN {filter} THEN r.llm_duration_ms END), " +
        $"SUM(CASE WHEN {filter} THEN r.input_tokens END), " +
        $"SUM(CASE WHEN {filter} THEN r.output_tokens END), " +
        $"SUM(CASE WHEN {filter} THEN r.tool_calls END), " +
        $"SUM(CASE WHEN {filter} THEN r.cost_usd END)";

    private const string WallClockMs =
        "GREATEST(0, TIMESTAMPDIFF(MICROSECOND, r.started_at, r.finished_at)) / 1000.0";

    private static readonly string MetricsSql =
        "SELECT a.id, " + WindowColumns("1 = 1") + ", " + WindowColumns("r.started_at >= @since") + " " +
        "FROM agent_definitions a LEFT JOIN agent_runs r ON r.agent_id = a.id " +
        "WHERE a.deleted_at IS NULL GROUP BY a.id ORDER BY a.id";

    public IReadOnlyList<AgentMetrics> List()
    {
        var result = new List<AgentMetrics>();
        using var command = connectionFactory.CreateCommand(MetricsSql);
        command.AddParameter("@since", DateTime.UtcNow - AgentMetricsAggregator.RecentWindow);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AgentMetrics(
                reader.GetInt64(0),
                ReadTotals(reader, 1),
                ReadTotals(reader, 1 + ColumnsPerWindow)));
        }

        return result;
    }

    private static AgentMetricsTotals ReadTotals(System.Data.IDataRecord reader, int offset)
    {
        object? Value(int i) => reader.IsDBNull(offset + i) ? null : reader.GetValue(offset + i);
        double? AsDouble(int i) => Value(i) is { } v ? Convert.ToDouble(v) : null;
        long? AsLong(int i) => Value(i) is { } v ? Convert.ToInt64(v) : null;

        return new AgentMetricsTotals(
            (int)(AsLong(0) ?? 0),
            (int)(AsLong(1) ?? 0),
            AsDouble(2), AsDouble(3), AsDouble(4), AsDouble(5),
            AsLong(6), AsLong(7), AsLong(8),
            Value(9) is { } cost ? Convert.ToDecimal(cost) : null);
    }
}
