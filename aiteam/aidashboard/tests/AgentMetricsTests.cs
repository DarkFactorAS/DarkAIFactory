using AiDashboard.Agents;
using AiDashboard.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class AgentMetricsTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private static AgentRunRow Run(
        long agent, TimeSpan age, string? outcome = "Succeeded", int seconds = 60, long? llm = 30000,
        long? input = 100, long? output = 50, int? tools = 3, decimal? cost = 0.5m) =>
        new(agent, Now - age, outcome is null ? null : Now - age + TimeSpan.FromSeconds(seconds), outcome, llm,
            input, output, tools, cost);

    [Fact]
    public void SevenDayWindow_ExcludesOlderRuns()
    {
        var runs = new[] { Run(1, TimeSpan.FromDays(1)), Run(1, TimeSpan.FromDays(8)) };
        var metrics = AgentMetricsAggregator.Aggregate([1], runs, Now).Single();

        Assert.Equal(2, metrics.AllTime.Succeeded);
        Assert.Equal(1, metrics.Last7Days.Succeeded);
        Assert.Equal(200, metrics.AllTime.InputTokens);
        Assert.Equal(100, metrics.Last7Days.InputTokens);
        Assert.Equal(1.0m, metrics.AllTime.CostUsd);
    }

    [Fact]
    public void SevenDayWindow_IncludesExactBoundary()
    {
        var metrics = AgentMetricsAggregator.Aggregate([1], [Run(1, TimeSpan.FromDays(7))], Now).Single();
        Assert.Equal(1, metrics.Last7Days.Succeeded);
    }

    [Fact]
    public void SumsOverOnlyNulls_AreNull_NotZero()
    {
        var runs = new[] { Run(1, TimeSpan.FromHours(1), llm: null, input: null, output: null, tools: null, cost: null) };
        var totals = AgentMetricsAggregator.Aggregate([1], runs, Now).Single().AllTime;

        Assert.Equal(1, totals.Succeeded);
        Assert.Equal(60000, totals.WallClockMs);
        Assert.Null(totals.LlmDurationMs);
        Assert.Null(totals.AvgLlmDurationMs);
        Assert.Null(totals.InputTokens);
        Assert.Null(totals.OutputTokens);
        Assert.Null(totals.ToolCalls);
        Assert.Null(totals.CostUsd);
    }

    [Fact]
    public void NullsAreIgnoredWhenOtherRunsReportValues_AndAveragesUseReportingRuns()
    {
        var runs = new[]
        {
            Run(1, TimeSpan.FromHours(1), seconds: 60, llm: 1000, cost: 1m),
            Run(1, TimeSpan.FromHours(2), seconds: 120, llm: null, cost: null),
        };
        var totals = AgentMetricsAggregator.Aggregate([1], runs, Now).Single().AllTime;

        Assert.Equal(180000, totals.WallClockMs);
        Assert.Equal(90000, totals.AvgWallClockMs);
        Assert.Equal(1000, totals.LlmDurationMs);
        Assert.Equal(1000, totals.AvgLlmDurationMs);
        Assert.Equal(1m, totals.CostUsd);
    }

    [Fact]
    public void InProgressRuns_CountNeitherSucceededNorFailed()
    {
        var runs = new[]
        {
            Run(1, TimeSpan.FromHours(1), outcome: null, llm: null, input: null, output: null, tools: null, cost: null),
            Run(1, TimeSpan.FromHours(2), outcome: "Failed"),
        };
        var totals = AgentMetricsAggregator.Aggregate([1], runs, Now).Single().AllTime;

        Assert.Equal(0, totals.Succeeded);
        Assert.Equal(1, totals.Failed);
        Assert.Equal(60000, totals.WallClockMs);
    }

    [Fact]
    public void OnlyListedAgentsAreReturned_RunsOfOtherAgentsIgnored()
    {
        // Deleted agents are not in the id list the repository passes in.
        var runs = new[] { Run(1, TimeSpan.FromHours(1)), Run(2, TimeSpan.FromHours(1)) };
        var result = AgentMetricsAggregator.Aggregate([1, 3], runs, Now);

        Assert.Equal([1L, 3L], result.Select(m => m.AgentId));
        Assert.Equal(1, result[0].AllTime.Succeeded);
        Assert.Equal(0, result[1].AllTime.Succeeded);
        Assert.Null(result[1].AllTime.CostUsd);
    }

    private sealed class FakeMetrics(bool fail = false) : IAgentMetricsRepository
    {
        public IReadOnlyList<AgentMetrics> List() => fail
            ? throw new InvalidOperationException("secret")
            : AgentMetricsAggregator.Aggregate([7], [Run(7, TimeSpan.FromHours(1))], Now);
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(bool fail = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAgentMetricsRepository>(new FakeMetrics(fail));
        builder.Services.AddSingleton<IAgentDefinitionRepository, NoAgents>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        AgentEndpoints.Map(app);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private sealed class NoAgents : IAgentDefinitionRepository
    {
        public IReadOnlyList<AgentView> List() => [];
        public AgentView? Find(long id) => null;
        public bool NameExists(string name, long? excludeId) => false;
        public IReadOnlyList<string> ExistingProjectKeys(IReadOnlyCollection<string> keys) => [];
        public IReadOnlyList<string> ListProjectKeys() => [];
        public IReadOnlyList<string> ListBuilderProjectKeys() => [];
        public AgentView Create(AgentWrite agent) => throw new NotSupportedException();
        public AgentView? Update(long id, AgentWrite agent) => throw new NotSupportedException();
        public bool SetEnabled(long id, bool enabled) => throw new NotSupportedException();
        public bool SoftDelete(long id) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Metrics_IsPublicAndNotShadowedByIdRoute()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/api/agents/metrics");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"agentId\":7", json);
        Assert.Contains("\"allTime\":{", json);
        Assert.Contains("\"last7Days\":{", json);
        Assert.Contains("\"succeeded\":1", json);
        Assert.Contains("\"costUsd\":0.5", json);
    }

    [Fact]
    public async Task Metrics_FailureReturns500WithoutDetails()
    {
        var (app, client) = await StartAsync(fail: true);
        await using var _ = app;
        using var __ = client;

        var response = await client.GetAsync("/api/agents/metrics");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret", json);
    }
}
