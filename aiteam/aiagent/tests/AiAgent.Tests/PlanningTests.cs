using AiAgent.Capabilities.Planning;
using AiAgent.Core;
using AiAgent.Providers;
using AiAgent.Repositories;

namespace AiAgent.Tests;

internal sealed class FakePlanRepository : IAgentPlanRepository
{
    public Dictionary<string, AgentPlan> Plans { get; } = [];
    public AgentPlan? Get(long agentId, string jiraKey) => Plans.GetValueOrDefault(jiraKey);
    public void Save(AgentPlan plan) => Plans[plan.JiraKey] = plan;
    public void MarkPostedToJira(long agentId, string jiraKey, string requirementsHash) =>
        Plans[jiraKey] = Plans[jiraKey] with { PostedToJira = true };
    public void ReplacePlan(long agentId, string jiraKey, string requirementsHash, string plan) =>
        Plans[jiraKey] = Plans[jiraKey] with { Plan = plan };
    public void RebindProject(long agentId, string jiraKey, long managerProjectId) =>
        Plans[jiraKey] = Plans[jiraKey] with { ManagerProjectId = managerProjectId };
}

internal sealed class FakePlanningJira : IPlanningJira
{
    public Dictionary<string, JiraIssue> Issues { get; } = [];
    public Dictionary<string, List<string>> Comments { get; } = [];
    public List<string> Transitions { get; } = [];
    public List<string> Queries { get; } = [];
    private int _nextSubtask = 100;

    public Task<IReadOnlyList<JiraIssue>> SearchAsync(string jql, CancellationToken cancellationToken)
    {
        Queries.Add(jql);
        return Task.FromResult<IReadOnlyList<JiraIssue>>(Issues.Values.ToList());
    }

    public Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken) =>
        Task.FromResult(Issues[issueKey]);

    public Task<IReadOnlyList<string>> GetCommentsAsync(string issueKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Comments.GetValueOrDefault(issueKey) ?? []);

    public Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken)
    {
        var issue = Issues[issueKey];
        if (!issue.Labels.Contains(label))
        {
            Issues[issueKey] = issue with { Labels = [.. issue.Labels, label] };
        }

        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(string issueKey, string label, CancellationToken cancellationToken)
    {
        var issue = Issues[issueKey];
        Issues[issueKey] = issue with { Labels = issue.Labels.Where(l => l != label).ToArray() };
        return Task.CompletedTask;
    }

    public Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken)
    {
        if (!Comments.TryGetValue(issueKey, out var list))
        {
            Comments[issueKey] = list = [];
        }

        list.Add(text);
        return Task.CompletedTask;
    }

    public Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken)
    {
        Transitions.Add($"{issueKey}->{statusName}");
        Issues[issueKey] = Issues[issueKey] with { StatusName = statusName };
        return Task.CompletedTask;
    }

    public Task<string> CreateSubtaskAsync(
        string parentKey, string summary, string description, CancellationToken cancellationToken)
    {
        var key = $"DFAI-{_nextSubtask++}";
        Issues[key] = new JiraIssue(key, summary, description, "Selected for Development")
        {
            ParentKey = parentKey, UpdatedAt = "u"
        };
        return Task.FromResult(key);
    }

    public Task LinkIssuesAsync(string blockerKey, string blockedKey, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class FakePlanningRunner(Func<JiraIssue, string> plan) : IPlanningRunner
{
    public List<string> Planned { get; } = [];
    public Task<string> GeneratePlanAsync(
        AgentContext context, IntegrationSettings settings, PlanningConfiguration configuration,
        JiraIssue issue, CancellationToken cancellationToken)
    {
        Planned.Add(issue.Key);
        return Task.FromResult(plan(issue));
    }

    public Task<string> CreateParentPullRequestAsync(
        AgentContext context, IntegrationSettings settings, PlanningConfiguration configuration,
        string headBranch, string issueKey, string summary, CancellationToken cancellationToken) =>
        Task.FromResult("https://github.com/o/r/pull/1");
}

internal sealed class OpenLock : IAgentClaimLock
{
    public IDisposable? TryAcquire(long agentId, string issueKey) => new Handle();
    private sealed class Handle : IDisposable { public void Dispose() { } }
}

public sealed class PlanningTests
{
    private const string Repo = "https://github.com/o/r";

    private static WorkflowLabels Labels() => new("aiagent", new Dictionary<string, string>
    {
        ["PickupLabel"] = "aiagent-picked",
        ["FailureLabel"] = "aiagent-failed",
        ["AnalyzedLabel"] = "aiagent-analyzed",
        ["PlannedLabel"] = "aiagent-planned",
        ["IgnoredLabel"] = "aiagent-ignored"
    });

    private static IntegrationSettings Settings(bool autoApprove = true) => new()
    {
        Jira = new JiraSettings { JiraAutoApproveSmallChanges = autoApprove.ToString() }
    };

    private static JiraIssue Issue(string key = "DFAI-1", params string[] labels) =>
        new(key, "Add feature", "Do the thing", "Selected for Development")
        {
            UpdatedAt = "2026-01-01", Labels = labels
        };

    private static string Fingerprint(JiraIssue issue) =>
        ManagerPlan.Fingerprint(issue.Summary, issue.Description, new Uri(Repo));

    private sealed class Fixture
    {
        public FakePlanningJira Jira { get; } = new();
        public FakePlanRepository Plans { get; } = new();
        public FakePlanningRunner Runner { get; }
        public PlanningCapability Capability { get; }
        public AgentContext Context { get; } = new(
            new AgentDefinition(7, "a", "role", "behavior", "claude", null, true, false), ["DFAI"], new OpenLock());

        public Fixture(string plan, bool autoApprove = true, params JiraIssue[] issues)
        {
            foreach (var issue in issues)
            {
                Jira.Issues[issue.Key] = issue;
            }

            Runner = new FakePlanningRunner(_ => plan);
            Capability = new PlanningCapability(
                () => Settings(autoApprove),
                Labels,
                () => [new ManagerProject(5, "DFAI", Repo), new ManagerProject(6, "OTHER", Repo)],
                Plans,
                _ => Jira,
                Runner);
        }

        public Task RunAsync() => Capability.RunCycleAsync(Context, default);
    }

    [Fact]
    public async Task SmallPlanIsStoredWithFingerprintAndLabelled()
    {
        var issue = Issue();
        var f = new Fixture("""{"decision":"small","plan":"Change file X."}""", true, issue);
        await f.RunAsync();

        var stored = f.Plans.Plans["DFAI-1"];
        Assert.Equal(Fingerprint(issue), stored.RequirementsHash);
        Assert.Equal("Change file X.", stored.Plan);
        Assert.True(stored.PostedToJira);
        Assert.Equal(7, stored.AgentId);
        Assert.Equal(5, stored.ManagerProjectId);
        Assert.Contains("aiagent-analyzed", f.Jira.Issues["DFAI-1"].Labels);
        Assert.Contains("aiagent-planned", f.Jira.Issues["DFAI-1"].Labels);
        Assert.Contains(f.Jira.Comments["DFAI-1"], c => c.Contains("Change file X."));
    }

    [Fact]
    public async Task PlainTextPlanIsStoredAsIs()
    {
        var f = new Fixture("Just do it.", true, Issue());
        await f.RunAsync();
        Assert.Equal("Just do it.", f.Plans.Plans["DFAI-1"].Plan);
        Assert.Contains("aiagent-analyzed", f.Jira.Issues["DFAI-1"].Labels);
    }

    [Fact]
    public async Task CurrentPlanIsReusedAndNotRegenerated()
    {
        var f = new Fixture("""{"decision":"small","plan":"P"}""", true, Issue());
        await f.RunAsync();
        await f.RunAsync();
        Assert.Single(f.Runner.Planned);
        Assert.Single(f.Jira.Comments["DFAI-1"], c => c.StartsWith("Implementation plan"));
    }

    [Fact]
    public async Task ChangedRequirementsTriggerNewPlan()
    {
        var f = new Fixture("""{"decision":"small","plan":"P"}""", true, Issue());
        await f.RunAsync();
        f.Jira.Issues["DFAI-1"] = f.Jira.Issues["DFAI-1"] with { Description = "Changed" };
        await f.RunAsync();
        Assert.Equal(2, f.Runner.Planned.Count);
        Assert.Equal(
            ManagerPlan.Fingerprint("Add feature", "Changed", new Uri(Repo)), f.Plans.Plans["DFAI-1"].RequirementsHash);
    }

    [Fact]
    public async Task ClarifyPostsQuestionsAndLabelsWithoutHandoff()
    {
        var f = new Fixture("""{"decision":"clarify","questions":["Which db?"]}""", true, Issue());
        await f.RunAsync();
        var labels = f.Jira.Issues["DFAI-1"].Labels;
        Assert.Contains(PlanningConfiguration.DefaultClarificationLabel, labels);
        Assert.DoesNotContain("aiagent-analyzed", labels);
        Assert.Contains(f.Jira.Comments["DFAI-1"], c => c.Contains("1. Which db?"));
        Assert.StartsWith("[triage:clarify]", f.Plans.Plans["DFAI-1"].Plan);
        Assert.True(f.Plans.Plans["DFAI-1"].PostedToJira);
    }

    [Fact]
    public async Task AnalysisOnlyPostsAnalysisAndTransitions()
    {
        var f = new Fixture("""{"decision":"analysis_only","analysis":"It is fine."}""", true, Issue());
        await f.RunAsync();
        Assert.Contains("DFAI-1->Code Review", f.Jira.Transitions);
        Assert.Contains(f.Jira.Comments["DFAI-1"], c => c.Contains("It is fine."));
        Assert.DoesNotContain("aiagent-analyzed", f.Jira.Issues["DFAI-1"].Labels);
    }

    [Fact]
    public async Task NeedsApprovalHoldsUntilApprovedLabelThenHandsOff()
    {
        var f = new Fixture(
            """{"decision":"needs_approval","plan":"Alter schema.","riskAreas":["database"]}""", true, Issue());
        await f.RunAsync();
        var held = f.Jira.Issues["DFAI-1"].Labels;
        Assert.Contains(PlanningConfiguration.DefaultApprovalLabel, held);
        Assert.DoesNotContain("aiagent-analyzed", held);

        await f.Jira.AddLabelAsync("DFAI-1", PlanningConfiguration.DefaultApprovedLabel, default);
        await f.RunAsync();
        var released = f.Jira.Issues["DFAI-1"].Labels;
        Assert.Contains("aiagent-analyzed", released);
        Assert.DoesNotContain(PlanningConfiguration.DefaultApprovalLabel, released);
        Assert.DoesNotContain(PlanningConfiguration.DefaultApprovedLabel, released);
        Assert.DoesNotContain("[triage", f.Plans.Plans["DFAI-1"].Plan);
        Assert.Single(f.Runner.Planned);
    }

    [Fact]
    public async Task SmallNeedsApprovalWhenAutoApproveIsOff()
    {
        var f = new Fixture("""{"decision":"small","plan":"P"}""", false, Issue());
        await f.RunAsync();
        Assert.Contains(PlanningConfiguration.DefaultApprovalLabel, f.Jira.Issues["DFAI-1"].Labels);
        Assert.DoesNotContain("aiagent-analyzed", f.Jira.Issues["DFAI-1"].Labels);
    }

    private const string SplitPlan = """
        {"decision":"split","plan":"Overall.","subtasks":[
          {"title":"B","description":"second","dependsOn":["A"]},
          {"title":"A","description":"first","dependsOn":[]}]}
        """;

    [Fact]
    public async Task SplitIsProposedFirstThenSubtasksAreCreatedAfterApproval()
    {
        var f = new Fixture(SplitPlan, true, Issue());
        await f.RunAsync();
        Assert.Single(f.Jira.Issues);
        Assert.StartsWith("[triage:split_proposal]", f.Plans.Plans["DFAI-1"].Plan);
        Assert.Contains(PlanningConfiguration.DefaultApprovalLabel, f.Jira.Issues["DFAI-1"].Labels);

        await f.Jira.AddLabelAsync("DFAI-1", PlanningConfiguration.DefaultApprovedLabel, default);
        await f.RunAsync();

        var subtasks = f.Jira.Issues.Values.Where(i => i.ParentKey == "DFAI-1").ToList();
        Assert.Equal(2, subtasks.Count);
        Assert.StartsWith("[triage:split]", f.Plans.Plans["DFAI-1"].Plan);
        Assert.Equal(f.Plans.Plans["DFAI-1"].RequirementsHash, Fingerprint(f.Jira.Issues["DFAI-1"]));
        var a = subtasks.Single(i => i.Summary == "A");
        var b = subtasks.Single(i => i.Summary == "B");
        Assert.True(f.Plans.Plans.ContainsKey(a.Key) && f.Plans.Plans.ContainsKey(b.Key));
        Assert.Contains("aiagent-analyzed", f.Jira.Issues[a.Key].Labels);
        Assert.DoesNotContain("aiagent-analyzed", f.Jira.Issues[b.Key].Labels);
        Assert.StartsWith("[triage:dependency_hold]", f.Plans.Plans[b.Key].Plan);
        Assert.Contains("ai-agent/DFAI-1", a.Description);
    }

    [Fact]
    public async Task ExistingBuilderOrFailureLabelsAreNotPlanned()
    {
        var f = new Fixture("""{"decision":"small","plan":"P"}""", true,
            Issue("DFAI-1", "aiagent-picked"), Issue("DFAI-2", "aiagent-failed"));
        await f.RunAsync();
        Assert.Empty(f.Runner.Planned);
        Assert.Empty(f.Plans.Plans);
    }

    [Fact]
    public async Task FailureLabelsIssueAndPostsCommentWhenPlanningThrows()
    {
        var f = new Fixture("""{"decision":"split","plan":"x","subtasks":[]}""", true, Issue());
        await f.RunAsync();
        Assert.Contains("aiagent-failed", f.Jira.Issues["DFAI-1"].Labels);
        Assert.Contains(f.Jira.Comments["DFAI-1"], c => c.Contains("could not complete planning"));
        Assert.Empty(f.Plans.Plans);
    }

    [Fact]
    public async Task OnlyProjectsOfTheAgentAreQueriedAndQueryExcludesOwnedIssues()
    {
        var f = new Fixture("""{"decision":"small","plan":"P"}""", true, Issue());
        await f.RunAsync();
        var jql = f.Jira.Queries.First(q => q.Contains("ORDER BY priority"));
        Assert.Contains("project = \"DFAI\"", jql);
        Assert.Contains("aiagent-failed", jql);
        Assert.Contains("aiagent-picked", jql);
        Assert.Contains("aiagent-ignored", jql);
        Assert.DoesNotContain(f.Jira.Queries, q => q.Contains("OTHER"));
    }

    [Fact]
    public async Task IssuesClaimedByAnotherAgentAreSkipped()
    {
        var f = new Fixture("""{"decision":"small","plan":"P"}""", true, Issue());
        var context = new AgentContext(f.Context.Agent, ["DFAI"], new NoLock());
        await f.Capability.RunCycleAsync(context, default);
        Assert.Empty(f.Runner.Planned);
    }

    [Fact]
    public void KindIsPlanning() =>
        Assert.Equal(AgentCapabilityKind.Planning, new Fixture("x").Capability.Kind);

    [Theory]
    [InlineData("clarify", """{"decision":"clarify","questions":["q"]}""")]
    [InlineData("analysis_only", """{"decision":"analysis_only","analysis":"a"}""")]
    [InlineData("small", """{"decision":"small","plan":"p"}""")]
    [InlineData("needs_approval", """{"decision":"needs_approval","plan":"p"}""")]
    [InlineData("split", """{"decision":"split","plan":"p","subtasks":[{"title":"t","description":"d"}]}""")]
    public void TriageParsesAllFiveDecisions(string name, string json)
    {
        Assert.True(TriageResult.TryParse("```json\n" + json + "\n```", out var result));
        Assert.Equal($"[triage:{name}]", TriageResult.MarkerFor(result!.Decision));
    }

    [Fact]
    public void TriageRejectsCyclesDuplicatesAndTooManySubtasks()
    {
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse(
            """{"decision":"split","plan":"p","subtasks":[{"title":"a","description":"d","dependsOn":["b"]},{"title":"b","description":"d","dependsOn":["a"]}]}"""));
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse(
            """{"decision":"split","plan":"p","subtasks":[{"title":"a","description":"d"},{"title":"A","description":"d"}]}"""));
        var many = string.Join(",", Enumerable.Range(0, 11).Select(i => $$"""{"title":"t{{i}}","description":"d"}"""));
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse(
            $$"""{"decision":"split","plan":"p","subtasks":[{{many}}]}"""));
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse("""{"decision":"nope"}"""));
    }

    [Fact]
    public void ExecutionOrderPutsDependenciesFirst()
    {
        var result = TriageResult.Parse(
            """{"decision":"split","plan":"p","subtasks":[{"title":"b","description":"d","dependsOn":["a"]},{"title":"a","description":"d"}]}""");
        Assert.Equal([1, 0], result.ExecutionOrder());
    }
}
