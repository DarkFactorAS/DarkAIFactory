using System.Text.Json;
using AiAgent.Capabilities.Jira;
using AiAgent.Capabilities.Review;
using AiAgent.Core;
using AiAgent.Metrics;
using AiAgent.Runner;

namespace AiAgent.Tests;

internal sealed class FakeRunRepository : IAgentRunRepository
{
    public List<AgentRunStart> Started { get; } = [];
    public Dictionary<long, AgentRunFinish> Finished { get; } = [];
    public List<long> Deleted { get; } = [];
    public bool FailInsert { get; set; }
    public bool FailFinish { get; set; }

    public long Insert(AgentRunStart start)
    {
        if (FailInsert) throw new InvalidOperationException("db down");
        Started.Add(start);
        return Started.Count;
    }

    public void Finish(long id, AgentRunFinish finish)
    {
        if (FailFinish) throw new InvalidOperationException("db down");
        Finished[id] = finish;
    }

    public void Delete(long id) => Deleted.Add(id);

    public AgentRunFinish Only() => Assert.Single(Finished).Value;
}

public sealed class AgentRunTests
{
    private static AgentContext Context(string provider = "Claude") =>
        new(new AgentDefinition(7, "a", "r", "b", provider, "m1", true, false), ["DFAI"], new TrackingClaimLock());

    private static RunnerResponse Response(RunnerUsage? usage, long ms = 100) => new(0, "ok", usage, null, ms);

    // ---- stream-json parser and arguments ----

    [Fact]
    public void ClaudeArgumentsUseStreamJsonVerboseAndKeepPermissionFlags()
    {
        var args = RunnerPolicy.BuildClaudeArguments("m", RunnerModes.Implement);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], args.Take(4));
        Assert.DoesNotContain("json", args);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("Bash(git push:*)", args);
        Assert.Contains("Bash(gh:*)", args);
        var plan = RunnerPolicy.BuildClaudeArguments("", RunnerModes.Plan);
        Assert.Contains("default", plan);
        Assert.Contains("Read", plan);
        Assert.Contains("Edit", plan);
    }

    [Fact]
    public void ParserReadsUsageToolCallsAndResultFromStream()
    {
        var stdout = string.Join('\n',
            """{"type":"system","subtype":"init"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"x"},{"type":"tool_use","name":"Read"},{"type":"tool_use","name":"Bash"}]}}""",
            "not json {",
            """{"type":"user","message":{"content":[{"type":"tool_result"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit"}]}}""",
            """{"type":"result","result":"done","duration_ms":4321,"num_turns":3,"total_cost_usd":0.0123,"usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":30,"cache_creation_input_tokens":40}}""");

        var (output, usage, duration) = RunnerPolicy.ParseClaudeStreamJson(stdout, 9);

        Assert.Equal("done", output);
        Assert.Equal(4321, duration);
        Assert.Equal(new RunnerUsage(10, 20, 30, 40, 0.0123m, 3, 3), usage);
    }

    [Fact]
    public void ParserFallsBackWithoutResultEvent()
    {
        var (output, usage, duration) = RunnerPolicy.ParseClaudeStreamJson("garbage\n{broken", 55);
        Assert.Equal("garbage\n{broken", output);
        Assert.Null(usage);
        Assert.Equal(55, duration);
    }

    [Fact]
    public void ParserLeavesMissingCacheFieldsNull()
    {
        var (_, usage, duration) = RunnerPolicy.ParseClaudeStreamJson(
            """{"type":"result","result":"r","usage":{"input_tokens":1,"output_tokens":2}}""", 8);
        Assert.Equal(new RunnerUsage(1, 2, null, null, null, 0, null), usage);
        Assert.Equal(8, duration);
    }

    [Fact]
    public void RunnerUsageSerializesNullableFields()
    {
        var json = JsonSerializer.Serialize(new RunnerResponse(0, "o", new RunnerUsage(1, 2), null, 5));
        var back = JsonSerializer.Deserialize<RunnerResponse>(json)!;
        Assert.Null(back.Usage!.CostUsd);
        Assert.Null(back.Usage.ToolCalls);
    }

    // ---- recorder lifecycle ----

    [Fact]
    public async Task SuccessfulWorkWritesOneFinishedRowWithAccumulatedUsage()
    {
        var repo = new FakeRunRepository();
        var recorder = new AgentRunRecorder(repo);

        await recorder.RecordAsync(Context(), AgentCapabilityKind.Builder, "DFAI-1", run =>
        {
            run.AddUsage(Response(new RunnerUsage(10, 20, 1, 2, 0.5m, 3, 4), 100));
            run.AddUsage(Response(new RunnerUsage(1, 2, 3, null, 0.25m, 1, 1), 50));
            run.Succeed();
            return Task.CompletedTask;
        });

        var start = Assert.Single(repo.Started);
        Assert.Equal((7, AgentCapabilityKind.Builder, "DFAI-1", "Claude", "m1"),
            (start.AgentId, start.Capability, start.IssueKey, start.Provider, start.Model));
        var finish = repo.Only();
        Assert.Equal(AgentRunOutcome.Succeeded, finish.Outcome);
        Assert.Null(finish.FailureReason);
        Assert.Equal(150, finish.LlmDurationMs);
        Assert.Equal((11L, 22L, 4L, 2L, 0.75m, 4, 5),
            (finish.InputTokens!.Value, finish.OutputTokens!.Value, finish.CacheReadTokens!.Value,
                finish.CacheWriteTokens!.Value, finish.CostUsd!.Value, finish.ToolCalls!.Value, finish.NumTurns!.Value));
    }

    [Fact]
    public async Task CopilotRunStoresOnlyLlmTime()
    {
        var repo = new FakeRunRepository();
        await new AgentRunRecorder(repo).RecordAsync(Context("Copilot"), AgentCapabilityKind.Review, "DFAI-1", run =>
        {
            run.AddUsage(Response(null, 321));
            run.Succeed();
            return Task.CompletedTask;
        });

        var finish = repo.Only();
        Assert.Equal(321, finish.LlmDurationMs);
        Assert.Null(finish.InputTokens);
        Assert.Null(finish.OutputTokens);
        Assert.Null(finish.CacheReadTokens);
        Assert.Null(finish.CostUsd);
        Assert.Null(finish.ToolCalls);
        Assert.Null(finish.NumTurns);
    }

    [Fact]
    public async Task ExceptionIsRecordedAsFailedAndRethrown()
    {
        var repo = new FakeRunRepository();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Jira, null, run =>
            {
                run.Succeed();
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal("boom", thrown.Message);
        var finish = repo.Only();
        Assert.Equal(AgentRunOutcome.Failed, finish.Outcome);
        Assert.Equal("boom", finish.FailureReason);
    }

    [Fact]
    public async Task WorkWithoutOutcomeCountsAsFailed()
    {
        var repo = new FakeRunRepository();
        await new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Jira, null, _ => Task.CompletedTask);
        Assert.Equal(AgentRunOutcome.Failed, repo.Only().Outcome);
    }

    [Fact]
    public async Task FailureIsNotMaskedByLaterSuccess()
    {
        var repo = new FakeRunRepository();
        await new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Planning, "K-1", run =>
        {
            run.Fail("bad json");
            run.Succeed();
            return Task.CompletedTask;
        });
        var finish = repo.Only();
        Assert.Equal(AgentRunOutcome.Failed, finish.Outcome);
        Assert.Equal("bad json", finish.FailureReason);
    }

    [Fact]
    public async Task RecorderDatabaseFailuresNeverBreakTheWork()
    {
        var repo = new FakeRunRepository { FailInsert = true, FailFinish = true };
        var ran = false;
        await new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Jira, null, run =>
        {
            ran = true;
            run.Succeed();
            return Task.CompletedTask;
        });
        Assert.True(ran);

        repo.FailInsert = false;
        await new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Jira, null, run =>
        {
            run.Succeed();
            return Task.CompletedTask;
        });
        Assert.Empty(repo.Finished);
    }

    [Fact]
    public async Task BusyRunnerDiscardsTheRow()
    {
        var repo = new FakeRunRepository();
        await Assert.ThrowsAsync<RunnerBusyException>(() =>
            new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Builder, "K-1",
                _ => throw new RunnerBusyException("busy")));
        Assert.Empty(repo.Finished);
        Assert.Equal([1L], repo.Deleted);
    }

    [Fact]
    public async Task DeferredStartWritesNoRowUntilBegun()
    {
        var repo = new FakeRunRepository();
        var recorder = new AgentRunRecorder(repo);
        await recorder.RecordAsync(Context(), AgentCapabilityKind.Planning, "K-1", run =>
        {
            run.Succeed();
            return Task.CompletedTask;
        }, deferStart: true);
        Assert.Empty(repo.Started);
        Assert.Empty(repo.Finished);

        await recorder.RecordAsync(Context(), AgentCapabilityKind.Planning, "K-2", run =>
        {
            run.Begin();
            run.Begin();
            run.Succeed();
            return Task.CompletedTask;
        }, deferStart: true);
        Assert.Single(repo.Started);
        Assert.Equal(AgentRunOutcome.Succeeded, repo.Only().Outcome);
    }

    // ---- capability outcome mapping ----

    [Theory]
    [InlineData(FixOutcome.Pushed, AgentRunOutcome.Succeeded)]
    [InlineData(FixOutcome.Failed, AgentRunOutcome.Failed)]
    [InlineData(FixOutcome.NoChanges, AgentRunOutcome.Failed)]
    public async Task ReviewOutcomeMapping(FixOutcome fix, AgentRunOutcome expected)
    {
        var repo = new FakeRunRepository();
        await new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Review, "K-1", run =>
        {
            ReviewCapability.MapOutcome(run, new FixResult(fix, fix == FixOutcome.Failed ? "tests failed" : null));
            return Task.CompletedTask;
        });
        var finish = repo.Only();
        Assert.Equal(expected, finish.Outcome);
        Assert.Equal(expected == AgentRunOutcome.Failed, finish.FailureReason is not null);
    }

    [Fact]
    public async Task ReviewSkippedWritesNoRow()
    {
        var repo = new FakeRunRepository();
        await new AgentRunRecorder(repo).RecordAsync(Context(), AgentCapabilityKind.Review, "K-1", run =>
        {
            ReviewCapability.MapOutcome(run, new FixResult(FixOutcome.Skipped));
            return Task.CompletedTask;
        });
        Assert.Empty(repo.Finished);
    }

    private static JiraCapabilityConfig JiraConfig() =>
        JiraCapabilityConfig.From(
            new IntegrationSettings
            {
                Jira = new JiraSettings
                {
                    JiraBaseUrl = "https://example.atlassian.net", JiraEmail = "a@b.c", JiraApiToken = "t",
                    JiraIntakeEnabled = "True"
                }
            },
            new WorkflowLabels("aiagent", new Dictionary<string, string>
            {
                ["PickupLabel"] = "aiagent-picked", ["FailureLabel"] = "aiagent-failed", ["IgnoredLabel"] = "aiagent-ignored"
            }));

    [Fact]
    public async Task JiraIntakeRecordsSucceededRunPerTaskAndNothingWithoutWork()
    {
        var repo = new FakeRunRepository();
        var tasks = new FakeTasks();
        var capability = new JiraCapability(
            JiraConfig, _ => new FakeJira(), tasks, new FakePullRequests(), new FakeMerge([]),
            _ => new Uri("https://github.com/o/r"), new AgentRunRecorder(repo));

        await capability.RunCycleAsync(Context(), default);
        Assert.Empty(repo.Started);

        tasks.Add("DFAI");
        await capability.RunCycleAsync(Context(), default);

        Assert.Single(repo.Started);
        Assert.Equal(AgentRunOutcome.Succeeded, repo.Only().Outcome);
        Assert.Equal(AgentCapabilityKind.Jira, repo.Started[0].Capability);
    }

    [Fact]
    public async Task JiraIntakeRecordsFailedRunsUpToTheAttemptLimit()
    {
        var repo = new FakeRunRepository();
        var tasks = new FakeTasks();
        tasks.Add("DFAI");
        var capability = new JiraCapability(
            JiraConfig, _ => new FailingJira(), tasks, new FakePullRequests(), new FakeMerge([]),
            _ => new Uri("https://github.com/o/r"), new AgentRunRecorder(repo));

        for (var i = 0; i < JiraCapabilityConfig.MaxIntakeAttempts; i++)
        {
            await capability.RunCycleAsync(Context(), default);
        }

        Assert.Equal(JiraCapabilityConfig.MaxIntakeAttempts, repo.Finished.Count);
        Assert.All(repo.Finished.Values, finish => Assert.Equal(AgentRunOutcome.Failed, finish.Outcome));
        Assert.Equal(AgentTaskStatus.Failed, tasks.Tasks[0].Status);
    }

    private sealed class FailingJira : IJiraClient
    {
        public Task<string> CreateIssueAsync(string projectKey, string title, string description, CancellationToken ct) =>
            throw new InvalidOperationException("jira down");
        public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JiraIssue>>([]);
        public Task<JiraIssue> GetIssueAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetCommentsAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task AddLabelAsync(string key, string label, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveLabelAsync(string key, string label, CancellationToken ct) => Task.CompletedTask;
        public Task AssignAsync(string key, string accountId, CancellationToken ct) => Task.CompletedTask;
        public Task AddCommentAsync(string key, string text, CancellationToken ct) => Task.CompletedTask;
        public Task TransitionToAsync(string key, string status, CancellationToken ct) => Task.CompletedTask;
    }
}
