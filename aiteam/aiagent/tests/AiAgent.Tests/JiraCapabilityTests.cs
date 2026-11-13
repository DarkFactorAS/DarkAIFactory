using AiAgent.Capabilities.Jira;
using AiAgent.Core;
using AiAgent.Repositories;

namespace AiAgent.Tests;

internal sealed class FakeJira : IJiraClient
{
    public List<JiraIssue> Issues { get; } = [];
    public List<string> Queries { get; } = [];
    public List<(string Key, string Status)> Transitions { get; } = [];
    public List<(string Key, string Label)> AddedLabels { get; } = [];
    public List<string> Created { get; } = [];
    public List<string> Comments { get; } = [];
    public Func<string, bool> MatchQuery { get; set; } = _ => true;

    public Task<string> CreateIssueAsync(string projectKey, string title, string description, CancellationToken ct)
    {
        Created.Add($"{projectKey}:{title}");
        return Task.FromResult($"{projectKey}-{Created.Count}");
    }

    public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, CancellationToken ct)
    {
        Queries.Add(jql);
        return Task.FromResult<IReadOnlyList<JiraIssue>>(Issues.Where(_ => MatchQuery(jql)).ToList());
    }

    public Task<JiraIssue> GetIssueAsync(string key, CancellationToken ct) =>
        Task.FromResult(Issues.First(issue => issue.Key == key));
    public Task<IReadOnlyList<string>> GetCommentsAsync(string key, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Comments);
    public Task AddLabelAsync(string key, string label, CancellationToken ct) { AddedLabels.Add((key, label)); return Task.CompletedTask; }
    public Task RemoveLabelAsync(string key, string label, CancellationToken ct) => Task.CompletedTask;
    public Task AssignAsync(string key, string accountId, CancellationToken ct) => Task.CompletedTask;
    public Task AddCommentAsync(string key, string text, CancellationToken ct) { Comments.Add(text); return Task.CompletedTask; }
    public Task TransitionToAsync(string key, string status, CancellationToken ct) { Transitions.Add((key, status)); return Task.CompletedTask; }
}

internal sealed class FakeTasks : IAgentTaskProcessorRepository
{
    public List<AgentTask> Tasks { get; } = [];
    public AgentTask Add(string project) =>
        Tasks.AddReturn(new AgentTask(Tasks.Count + 1, project, "T", "D", AgentTaskStatus.New, null, DateTime.UtcNow, DateTime.UtcNow));
    public IReadOnlyList<AgentTask> List(string? status = null) => Tasks.Where(t => status is null || t.Status == status).ToList();
    public AgentTask? Find(long id) => Tasks.SingleOrDefault(t => t.Id == id);
    public AgentTask Create(string p, string t, string d) => Add(p);
    public bool EditNew(long id, string t, string d) => true;
    public AgentTask? Update(long id, string p, string t, string d, string s, string? k) => Find(id);
    public bool TrySetJiraKey(long id, string expected, string key)
    {
        var i = Tasks.FindIndex(t => t.Id == id);
        if (Tasks[i].Status != expected || Tasks[i].JiraKey is not null) return false;
        Tasks[i] = Tasks[i] with { JiraKey = key };
        return true;
    }
    public bool TrySetStatus(long id, string expected, string? key, string status)
    {
        var i = Tasks.FindIndex(t => t.Id == id);
        if (Tasks[i].Status != expected || Tasks[i].JiraKey != key) return false;
        Tasks[i] = Tasks[i] with { Status = status };
        return true;
    }
}

internal static class ListExtensions
{
    public static T AddReturn<T>(this List<T> list, T item) { list.Add(item); return item; }
}

internal sealed class FakePullRequests : IAgentPullRequestRepository
{
    public List<AgentPullRequestRecord> Records { get; } = [];
    public void Record(AgentPullRequestRecord pr) => Records.Add(pr);
    public IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId) => Records.Where(r => r.AgentId == agentId).ToList();
}

internal sealed class FakeMerge(HashSet<string> merged) : IPullRequestMergeChecker
{
    public Task<bool> IsMergedAsync(string token, AgentPullRequestRecord pr, CancellationToken ct) =>
        Task.FromResult(merged.Contains(pr.PullRequestUrl));
}

public sealed class JiraCapabilityTests
{
    private static JiraCapabilityConfig Config(bool intake = true, string sources = "To Do,Backlog") =>
        JiraCapabilityConfig.From(
            new IntegrationSettings
            {
                Jira = new JiraSettings
                {
                    JiraBaseUrl = "https://example.atlassian.net", JiraEmail = "a@b.c", JiraApiToken = "t",
                    JiraUnblockSourceStatuses = sources, JiraIntakeEnabled = intake.ToString()
                }
            },
            new WorkflowLabels("aiagent", new Dictionary<string, string>
            {
                ["PickupLabel"] = "aiagent-picked", ["FailureLabel"] = "aiagent-failed", ["IgnoredLabel"] = "aiagent-ignored"
            }));

    private static AgentContext Context(params string[] projects) =>
        new(new AgentDefinition(1, "a", "r", "b", "Claude", null, true, false), projects, new AlwaysLock());

    private static JiraIssue Issue(string key, string status, params JiraBlocker[] blockers) =>
        new(key, "s", "d", status) { Blockers = blockers };

    private static JiraCapability Capability(
        IJiraClient jira, FakeTasks tasks, FakePullRequests prs, JiraCapabilityConfig config, params string[] merged) =>
        new(() => config, _ => jira, tasks, prs, new FakeMerge([.. merged]),
            _ => new Uri("https://github.com/o/r"));

    [Fact]
    public void ConfigDefaultsAndValidation()
    {
        var config = Config();
        Assert.Equal(["To Do", "Backlog"], config.UnblockSourceStatuses);
        Assert.Equal("Selected for Development", config.SelectedStatus);
        Assert.Equal(["Backlog"], Config(sources: " Backlog ,").UnblockSourceStatuses);
    }

    [Fact]
    public async Task IntakeCreatesIssuesOnlyForAgentProjects()
    {
        var jira = new FakeJira();
        var tasks = new FakeTasks();
        tasks.Add("DFAI");
        tasks.Add("OTHER");
        await Capability(jira, tasks, new FakePullRequests(), Config()).RunCycleAsync(Context("DFAI"), default);

        Assert.Equal(["DFAI:T"], jira.Created);
        Assert.Contains(("DFAI-1", "Selected for Development"), jira.Transitions);
        Assert.Equal(AgentTaskStatus.Created, tasks.Tasks[0].Status);
        Assert.Equal(AgentTaskStatus.New, tasks.Tasks[1].Status);
    }

    [Fact]
    public async Task IntakeCanBeDisabled()
    {
        var jira = new FakeJira();
        var tasks = new FakeTasks();
        tasks.Add("DFAI");
        await Capability(jira, tasks, new FakePullRequests(), Config(intake: false)).RunCycleAsync(Context("DFAI"), default);
        Assert.Empty(jira.Created);
    }

    [Fact]
    public async Task IntakeMarksTaskFailedAfterMaxAttempts()
    {
        var tasks = new FakeTasks();
        tasks.Add("DFAI");
        var jira = new ThrowingJira();
        var capability = Capability(jira, tasks, new FakePullRequests(), Config());
        for (var i = 0; i < JiraCapabilityConfig.MaxIntakeAttempts; i++)
        {
            await capability.RunCycleAsync(Context("DFAI"), default);
        }

        Assert.Equal(AgentTaskStatus.Failed, tasks.Tasks[0].Status);
    }

    private sealed class ThrowingJira : IJiraClient
    {
        public Task<string> CreateIssueAsync(string p, string t, string d, CancellationToken ct) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiraIssue>>([]);
        public Task<JiraIssue> GetIssueAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetCommentsAsync(string key, CancellationToken ct) => throw new NotSupportedException();
        public Task AddLabelAsync(string key, string label, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveLabelAsync(string key, string label, CancellationToken ct) => throw new NotSupportedException();
        public Task AssignAsync(string key, string accountId, CancellationToken ct) => throw new NotSupportedException();
        public Task AddCommentAsync(string key, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task TransitionToAsync(string key, string status, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ClosesInReviewIssueOnlyWhenAllPullRequestsMerged()
    {
        var jira = new FakeJira { MatchQuery = q => q.Contains("\"In Review\"") };
        jira.Issues.Add(Issue("DFAI-1", "In Review"));
        jira.Issues.Add(Issue("DFAI-2", "In Review"));
        jira.Issues.Add(Issue("DFAI-3", "In Review"));
        var prs = new FakePullRequests();
        prs.Records.Add(new(1, "DFAI-1", "https://github.com/o/r", 1, "u1"));
        prs.Records.Add(new(1, "DFAI-1", "https://github.com/o/r", 2, "u2"));
        prs.Records.Add(new(1, "DFAI-2", "https://github.com/o/r", 3, "u3"));
        prs.Records.Add(new(1, "DFAI-2", "https://github.com/o/r", 4, "u4"));
        await Capability(jira, new FakeTasks(), prs, Config(), "u1", "u2", "u3").RunCycleAsync(Context("DFAI"), default);

        Assert.Equal([("DFAI-1", "Done")], jira.Transitions);
    }

    [Fact]
    public async Task UnblocksIssueWhenAllBlockersDone()
    {
        var jira = new FakeJira { MatchQuery = q => q.Contains("\"To Do\", \"Backlog\"") };
        jira.Issues.Add(Issue("DFAI-1", "To Do", new JiraBlocker("DFAI-9", "Done"), new JiraBlocker("DFAI-8", "done")));
        jira.Issues.Add(Issue("DFAI-2", "Backlog", new JiraBlocker("DFAI-9", "Done"), new JiraBlocker("DFAI-7", "In Progress")));
        jira.Issues.Add(Issue("DFAI-3", "To Do"));
        await Capability(jira, new FakeTasks(), new FakePullRequests(), Config()).RunCycleAsync(Context("DFAI"), default);

        Assert.Equal([("DFAI-1", "Selected for Development")], jira.Transitions);
    }

    [Fact]
    public async Task UnblockUsesConfiguredSourceStatuses()
    {
        var jira = new FakeJira();
        await Capability(jira, new FakeTasks(), new FakePullRequests(), Config(sources: "Icebox"))
            .RunCycleAsync(Context("DFAI"), default);
        Assert.Contains(jira.Queries, q => q.Contains("status in (\"Icebox\")"));
    }

    [Fact]
    public async Task RecoveryStoresPendingPullRequestAndRejectsForeignRepository()
    {
        var jira = new FakeJira { MatchQuery = q => q.Contains(PullRequestTrackingRecovery.PendingLabel) };
        jira.Issues.Add(Issue("DFAI-1", "In Review"));
        jira.Comments.Add($"{PullRequestTrackingRecovery.PendingCommentPrefix}https://github.com/o/r/pull/5");
        var prs = new FakePullRequests();
        await Capability(jira, new FakeTasks(), prs, Config()).RunCycleAsync(Context("DFAI"), default);
        Assert.Equal(5, Assert.Single(prs.Records).PullRequestNumber);

        jira.Comments.Clear();
        jira.Comments.Add($"{PullRequestTrackingRecovery.PendingCommentPrefix}https://github.com/evil/x/pull/6");
        var prs2 = new FakePullRequests();
        await Capability(jira, new FakeTasks(), prs2, Config()).RunCycleAsync(Context("DFAI"), default);
        Assert.Empty(prs2.Records);
    }

    [Fact]
    public async Task PickupClaimsLabelsAndMovesToInProgress()
    {
        var jira = new FakeJira();
        var issue = Issue("DFAI-1", "Selected for Development");
        jira.Issues.Add(issue);
        var workflow = new JiraWorkflow(jira, Config());
        var context = new AgentContext(
            new AgentDefinition(1, "a", "r", "b", "Claude", null, true, false), ["DFAI"], new AlwaysLock());

        using var pickup = await workflow.TryPickupAsync(context, issue, default);

        Assert.NotNull(pickup);
        Assert.Contains(("DFAI-1", "aiagent-picked"), jira.AddedLabels);
        Assert.Contains(("DFAI-1", "In Progress"), jira.Transitions);
        await workflow.FindWorkAsync("DFAI", default);
        var jql = jira.Queries.Last();
        Assert.Contains("aiagent-failed", jql);
        Assert.Contains("aiagent-picked", jql);
        Assert.Contains("aiagent-ignored", jql);
    }

    [Fact]
    public async Task PickupSkipsBlockedAndAlreadyPickedIssues()
    {
        var jira = new FakeJira();
        var blocked = Issue("DFAI-1", "Selected for Development", new JiraBlocker("DFAI-9", "In Progress"));
        var picked = Issue("DFAI-2", "Selected for Development") with { Labels = ["aiagent-picked"] };
        jira.Issues.AddRange([blocked, picked]);
        var workflow = new JiraWorkflow(jira, Config());
        var context = new AgentContext(
            new AgentDefinition(1, "a", "r", "b", "Claude", null, true, false), ["DFAI"], new AlwaysLock());

        Assert.Null(await workflow.TryPickupAsync(context, blocked, default));
        Assert.Null(await workflow.TryPickupAsync(context, picked, default));
        Assert.Empty(jira.AddedLabels);
    }

    [Fact]
    public async Task FailureRestoresStatusAndAddsFailureLabel()
    {
        var jira = new FakeJira();
        var issue = Issue("DFAI-1", "Selected for Development");
        jira.Issues.Add(issue);
        var workflow = new JiraWorkflow(jira, Config());
        var context = new AgentContext(
            new AgentDefinition(1, "a", "r", "b", "Claude", null, true, false), ["DFAI"], new AlwaysLock());
        using var pickup = (await workflow.TryPickupAsync(context, issue, default))!;

        await workflow.ReportFailureAsync(pickup, new InvalidOperationException("x"), null, null, default);

        Assert.Contains(("DFAI-1", "Selected for Development"), jira.Transitions);
        Assert.Contains(("DFAI-1", "aiagent-failed"), jira.AddedLabels);
    }

    [Fact]
    public async Task OnlyRunsWhenJiraCapabilityEnabled()
    {
        var jira = new FakeJira();
        var tasks = new FakeTasks();
        tasks.Add("DFAI");
        var repo = new FakeRepository();
        var loop = new AgentLoop(
            1, repo, [Capability(jira, tasks, new FakePullRequests(), Config())], new AlwaysLock(), TimeSpan.FromMilliseconds(10));

        await loop.RunCycleAsync(default);
        Assert.Empty(jira.Created);

        repo.Capabilities = [AgentCapabilityKind.Jira];
        await loop.RunCycleAsync(default);
        Assert.Single(jira.Created);
    }

    private sealed class AlwaysLock : IAgentClaimLock
    {
        public IDisposable? TryAcquire(long agentId, string issueKey) => new Handle();
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
