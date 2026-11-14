using System.Net;
using System.Text;
using AiAgent.Capabilities.Builder;
using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.Repositories;
using AiAgent.Runner;

namespace AiAgent.Tests;

public sealed class BuilderCapabilityTests : IDisposable
{
    private static readonly Uri RepoUrl = new("https://github.com/o/r");
    private readonly string root = Path.Combine(Path.GetTempPath(), "builder-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeJira(params BuilderIssue[] issues) : IBuilderJira
    {
        public List<string> Calls { get; } = [];
        public BuilderIssue? Refreshed { get; set; }
        public Task<IReadOnlyList<BuilderIssue>> FindWorkAsync(string jql, CancellationToken ct)
        { Calls.Add("find:" + jql); return Task.FromResult<IReadOnlyList<BuilderIssue>>(issues); }
        public Task<BuilderIssue> GetIssueAsync(string key, CancellationToken ct)
        { Calls.Add("get"); var first = Calls.Count(c => c == "get") == 1; return Task.FromResult(first ? issues.First(i => i.Key == key) : Refreshed ?? issues.First(i => i.Key == key)); }
        public Task<IReadOnlyList<string>> GetCommentsAsync(string key, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddLabelAsync(string key, string label, CancellationToken ct) { Calls.Add("label+:" + label); return Task.CompletedTask; }
        public Task RemoveLabelAsync(string key, string label, CancellationToken ct) { Calls.Add("label-:" + label); return Task.CompletedTask; }
        public Task AssignAsync(string key, string account, CancellationToken ct) { Calls.Add("assign"); return Task.CompletedTask; }
        public Task AddCommentAsync(string key, string text, CancellationToken ct) { Calls.Add("comment:" + text); return Task.CompletedTask; }
        public Task TransitionToAsync(string key, string status, CancellationToken ct) { Calls.Add("transition:" + status); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class FakePlans(AgentPlan? plan) : IAgentPlanRepository
    {
        public AgentPlan? Get(long agentId, string jiraKey) => plan;
        public void Save(AgentPlan p) { }
        public void MarkPostedToJira(long agentId, string jiraKey, string requirementsHash) { }
        public void ReplacePlan(long agentId, string jiraKey, string requirementsHash, string updatedPlan) { }
        public void RebindProject(long agentId, string jiraKey, long managerProjectId) { }
    }

    private sealed class FakeRunner(int exitCode = 0) : IRunnerClient
    {
        public List<RunnerRequest> Requests { get; } = [];
        public Task<RunnerResponse> RunAsync(RunnerRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new RunnerResponse(exitCode, "done", null, null, 1));
        }
    }

    private sealed class Git(bool changes = true) : IGitRunner
    {
        public Task<GitResult> RunAsync(string dir, IReadOnlyList<string> args, bool creds, CancellationToken ct, bool allowExitCodeOne = false)
        {
            if (args[0] == "diff" && args.Contains("--name-only")) return Task.FromResult(new GitResult(0, "a.cs\0", ""));
            if (args[0] == "diff") return Task.FromResult(new GitResult(changes ? 1 : 0, "", ""));
            return Task.FromResult(new GitResult(0, "", ""));
        }
    }

    private sealed class Prs : IAgentPullRequestRepository
    {
        public List<AgentPullRequestRecord> Items { get; } = [];
        public void Record(AgentPullRequestRecord pr) => Items.Add(pr);
        public IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId) => Items;
    }

    private sealed class GitHubStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.Method == HttpMethod.Get
                ? "{\"default_branch\":\"main\"}"
                : "{\"html_url\":\"https://github.com/o/r/pull/9\",\"number\":9}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static BuilderSettings Settings(string jql = "") => new(
        new Uri("https://x.atlassian.net"), "e", "t", "repo-token", "", jql, "Selected for Development",
        "aiagent-picked", "aiagent-failed", "aiagent-analyzed", "aiagent-ignored",
        "In Progress", "In Review", "Done", "", "dotnet build", "dotnet test");

    private static BuilderIssue Issue(string summary = "Do it") =>
        new("DFAI-1", summary, "desc", "Selected for Development") { Labels = ["aiagent-analyzed"] };

    private static AgentPlan CurrentPlan(BuilderIssue issue, bool posted = true) => new(
        1, 1, issue.Key, "the plan", posted, ManagerPlan.Fingerprint(issue.Summary, issue.Description, RepoUrl));

    private (BuilderCapability Capability, Prs Prs) Create(
        FakeJira jira, AgentPlan? plan, FakeRunner runner, BuilderSettings? settings = null, bool changes = true)
    {
        var prs = new Prs();
        var capability = new BuilderCapability(
            () => settings ?? Settings(),
            () => [new ManagerProject(1, "DFAI", RepoUrl.AbsoluteUri)],
            new FakePlans(plan),
            _ => jira,
            (url, s) => new GitHubCore(new Git(changes), url, "token", prs, new GitHubStub()),
            runner,
            root);
        return (capability, prs);
    }

    private sealed class FreeLock : IAgentClaimLock
    {
        public IDisposable? TryAcquire(long agentId, string issueKey) => new Handle();
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    private static AgentContext Context() => new(
        new AgentDefinition(1, "agent", "role", "behavior", "Claude", null, true, false), ["DFAI"], new FreeLock());

    [Fact]
    public async Task PlannedIssueEndsAsPullRequestAndInReview()
    {
        var issue = Issue();
        var jira = new FakeJira(issue);
        var runner = new FakeRunner();
        var (capability, prs) = Create(jira, CurrentPlan(issue), runner);

        await capability.RunCycleAsync(Context(), default);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(RunnerModes.Implement, request.Mode);
        Assert.Equal("dotnet build", request.BuildCommand);
        Assert.Equal("dotnet test", request.TestCommand);
        Assert.Contains("the plan", request.Prompt);
        var record = Assert.Single(prs.Items);
        Assert.Equal("https://github.com/o/r/pull/9", record.PullRequestUrl);
        Assert.Contains("label+:aiagent-picked", jira.Calls);
        Assert.Equal("transition:In Review", jira.Calls[^1]);
        Assert.DoesNotContain("label+:aiagent-failed", jira.Calls);
        Assert.Contains(jira.Calls, c => c.StartsWith("find:") && c.Contains("aiagent-analyzed") && c.Contains("aiagent-picked"));
    }

    [Fact]
    public async Task StaleOrUnpostedPlanBlocksImplementation()
    {
        var issue = Issue();
        foreach (var plan in new[] { CurrentPlan(issue, posted: false), CurrentPlan(Issue("Old title")) })
        {
            var jira = new FakeJira(issue);
            var runner = new FakeRunner();
            var (capability, prs) = Create(jira, plan, runner);
            await capability.RunCycleAsync(Context(), default);
            Assert.Empty(runner.Requests);
            Assert.Empty(prs.Items);
            Assert.DoesNotContain("label+:aiagent-picked", jira.Calls);
        }
    }

    [Fact]
    public async Task MissingPlanWaitsUnlessCustomJqlAllowsLocalPlan()
    {
        var issue = Issue();
        var jira = new FakeJira(issue);
        var runner = new FakeRunner();
        await Create(jira, null, runner).Capability.RunCycleAsync(Context(), default);
        Assert.Empty(runner.Requests);

        var localRunner = new FakeRunner();
        var (capability, prs) = Create(new FakeJira(issue), null, localRunner, Settings("status = Open"));
        await capability.RunCycleAsync(Context(), default);
        Assert.Equal([RunnerModes.Plan, RunnerModes.Implement], localRunner.Requests.Select(r => r.Mode));
        Assert.Single(prs.Items);
    }

    [Fact]
    public async Task ChangedRequirementsFailTheIssue()
    {
        var issue = Issue();
        var jira = new FakeJira(issue) { Refreshed = Issue("Changed") };
        var runner = new FakeRunner();
        var (capability, prs) = Create(jira, CurrentPlan(issue), runner);
        await capability.RunCycleAsync(Context(), default);
        Assert.Empty(runner.Requests);
        Assert.Empty(prs.Items);
        Assert.Contains("label+:aiagent-failed", jira.Calls);
    }

    [Fact]
    public async Task RunnerFailureLabelsIssueFailedWithoutPullRequest()
    {
        var issue = Issue();
        var jira = new FakeJira(issue);
        var (capability, prs) = Create(jira, CurrentPlan(issue), new FakeRunner(exitCode: 1));
        await capability.RunCycleAsync(Context(), default);
        Assert.Empty(prs.Items);
        Assert.Contains("label+:aiagent-failed", jira.Calls);
        Assert.DoesNotContain("transition:In Review", jira.Calls);
        Assert.Contains(jira.Calls, c => c.StartsWith("comment:") && c.Contains("could not complete"));
    }

    [Fact]
    public async Task NoChangesMeansNoPullRequest()
    {
        var issue = Issue();
        var jira = new FakeJira(issue);
        var (capability, prs) = Create(jira, CurrentPlan(issue), new FakeRunner(), changes: false);
        await capability.RunCycleAsync(Context(), default);
        Assert.Empty(prs.Items);
        Assert.DoesNotContain("transition:In Review", jira.Calls);
    }

    [Fact]
    public async Task SubTaskTargetsParentBranch()
    {
        var issue = Issue() with { ParentKey = "DFAI-154" };
        var jira = new FakeJira(issue);
        var runner = new FakeRunner();
        var (capability, _) = Create(jira, CurrentPlan(issue), runner);
        await capability.RunCycleAsync(Context(), default);
        Assert.Contains("aiagent/parent/DFAI-154", runner.Requests[0].Prompt);
    }

    [Fact]
    public async Task FailingClaimSkipsIssue()
    {
        var issue = Issue();
        var jira = new FakeJira(issue);
        var runner = new FakeRunner();
        var (capability, _) = Create(jira, CurrentPlan(issue), runner);
        var context = new AgentContext(Context().Agent, ["DFAI"], new NoLock());
        await capability.RunCycleAsync(context, default);
        Assert.Empty(runner.Requests);
        Assert.DoesNotContain("label+:aiagent-picked", jira.Calls);
    }

    [Fact]
    public async Task OtherProjectKeysAreNotProcessed()
    {
        var issue = Issue();
        var jira = new FakeJira(issue);
        var (capability, _) = Create(jira, CurrentPlan(issue), new FakeRunner());
        var context = new AgentContext(Context().Agent, ["OTHER"], new FreeLock());
        await capability.RunCycleAsync(context, default);
        Assert.Empty(jira.Calls);
    }

    [Fact]
    public void ImplementationPromptDoesNotRunTheRetiredAgentsTestProjects()
    {
        var prompt = PromptBuilder.BuildImplementationPrompt(new BuilderIssue("DFAI-1", "Title", "Description"), "Plan");

        Assert.Contains("aiteam/aiagent/tests/AiAgent.Tests/AiAgent.Tests.csproj", prompt);
        Assert.DoesNotContain("CodingAgent.Tests.csproj", prompt);
        Assert.DoesNotContain("ManagerAgent.Tests.csproj", prompt);
        Assert.DoesNotContain("ALL test projects", prompt);
    }
}
