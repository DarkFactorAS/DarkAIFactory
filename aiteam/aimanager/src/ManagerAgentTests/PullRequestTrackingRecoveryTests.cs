using AiTeam.Tests;
using ManagerAgent.Repository;
using Pr = ManagerAgent.GitHubRepository.PullRequestInfo;
using State = ManagerAgent.GitHubRepository.PullRequestState;

namespace ManagerAgent.Tests;

public sealed class PullRequestTrackingRecoveryTests
{
    private static readonly Uri RepositoryUrl = new("https://github.com/owner/repo.git");

    private static readonly AgentConfiguration Configuration = new(
        new Uri("https://example.atlassian.net"), "bot@example.com", "token", "gh", "repo",
        RepositoryUrl, "project = X", "picked", "failed",
        "In Progress", "In Review", TimeSpan.FromSeconds(30));

    private static Pr Open(long number, string head, string title = "Work", State state = State.Open) =>
        new(number, $"https://github.com/owner/repo/pull/{number}", state, "master", head, title);

    private sealed class Fixture
    {
        public DatabaseStub Database { get; } = new();
        public List<Pr> OpenPullRequests { get; } = [];
        public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal);
        public List<string> JiraLookups { get; } = [];
        public List<(string Key, long Number, string Url, string Repository)> Recorded { get; } = [];

        public Fixture(params (string Key, long Number)[] tracked)
        {
            Database.Rows.Columns.Add("jira_key", typeof(string));
            Database.Rows.Columns.Add("repository_url", typeof(string));
            Database.Rows.Columns.Add("pr_number", typeof(long));
            Database.Rows.Columns.Add("pr_url", typeof(string));
            foreach (var (key, number) in tracked)
            {
                Database.Rows.Rows.Add(key, RepositoryUrl.AbsoluteUri, number, $"https://github.com/owner/repo/pull/{number}");
            }

            Database.OnNonQuery = command =>
            {
                Assert.StartsWith("INSERT INTO pull_requests", command.CommandText);
                Recorded.Add((
                    (string)command.Values["@jira_key"],
                    (long)command.Values["@pr_number"],
                    (string)command.Values["@pr_url"],
                    (string)command.Values["@repository_url"]));
                return 1;
            };
        }

        public Task<IReadOnlyList<Pr>> RunAsync() =>
            new PullRequestTrackingRecovery(
                Configuration,
                "DFAI",
                new PullRequestRepository(Database),
                (key, _) =>
                {
                    JiraLookups.Add(key);
                    return Statuses.TryGetValue(key, out var status)
                        ? Task.FromResult(new JiraIssue(key, "S", "D", status))
                        : throw new InvalidOperationException($"Issue {key} does not exist.");
                },
                _ => Task.FromResult<IReadOnlyList<Pr>>(OpenPullRequests))
                .RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OpenUntrackedPullRequestInReview_IsRecorded()
    {
        var fixture = new Fixture(("DFAI-91", 61));
        fixture.OpenPullRequests.Add(Open(63, "ai-agent/DFAI-100-494ad451027f497a805e977246978f98"));
        fixture.Statuses["DFAI-100"] = "In Review";

        var recorded = await fixture.RunAsync();

        Assert.Equal([63L], recorded.Select(pr => pr.Number));
        Assert.Equal(
            [("DFAI-100", 63L, "https://github.com/owner/repo/pull/63", RepositoryUrl.AbsoluteUri)],
            fixture.Recorded);
    }

    [Fact]
    public async Task AlreadyTrackedPullRequest_IsNotLookedUpOrRecorded()
    {
        var fixture = new Fixture(("DFAI-100", 63));
        fixture.OpenPullRequests.Add(Open(63, "ai-agent/DFAI-100"));
        fixture.Statuses["DFAI-100"] = "In Review";

        Assert.Empty(await fixture.RunAsync());
        Assert.Empty(fixture.JiraLookups);
        Assert.Empty(fixture.Recorded);
    }

    [Theory]
    [InlineData("In Progress")]
    [InlineData("Done")]
    [InlineData("")]
    public async Task IssueNotInReview_IsNotRecorded(string status)
    {
        var fixture = new Fixture();
        fixture.OpenPullRequests.Add(Open(63, "ai-agent/DFAI-100"));
        fixture.Statuses["DFAI-100"] = status;

        Assert.Empty(await fixture.RunAsync());
        Assert.Empty(fixture.Recorded);
    }

    [Fact]
    public async Task StatusComparison_IgnoresCase()
    {
        var fixture = new Fixture();
        fixture.OpenPullRequests.Add(Open(63, "ai-agent/DFAI-100"));
        fixture.Statuses["DFAI-100"] = "in review";

        Assert.Single(await fixture.RunAsync());
    }

    [Fact]
    public async Task PullRequestWithoutProjectKey_IsSkippedWithoutJiraLookup()
    {
        var fixture = new Fixture();
        fixture.OpenPullRequests.Add(Open(70, "feature/something", "Some change"));
        fixture.OpenPullRequests.Add(Open(71, "ai-agent/OTHER-5", "OTHER-5: elsewhere"));
        fixture.OpenPullRequests.Add(Open(72, "ai-agent/rollback-pr-63", "Revert \"DFAI-100: x\""));

        Assert.Empty(await fixture.RunAsync());
        Assert.Empty(fixture.JiraLookups);
    }

    [Fact]
    public async Task JiraFailure_IsLoggedAndOtherPullRequestsContinue()
    {
        var fixture = new Fixture();
        fixture.OpenPullRequests.Add(Open(80, "ai-agent/DFAI-404"));
        fixture.OpenPullRequests.Add(Open(81, "ai-agent/DFAI-101"));
        fixture.Statuses["DFAI-101"] = "In Review";

        var recorded = await fixture.RunAsync();

        Assert.Equal([81L], recorded.Select(pr => pr.Number));
    }

    [Fact]
    public async Task NonOpenPullRequest_IsIgnored()
    {
        var fixture = new Fixture();
        fixture.OpenPullRequests.Add(Open(63, "ai-agent/DFAI-100", state: State.Closed));
        fixture.Statuses["DFAI-100"] = "In Review";

        Assert.Empty(await fixture.RunAsync());
    }

    [Theory]
    [InlineData("ai-agent/DFAI-100-494ad451027f497a805e977246978f98", "x", "DFAI-100")]
    [InlineData("ai-agent/DFAI-100", "x", "DFAI-100")]
    [InlineData("ai-agent/dfai-7-abc", "x", "DFAI-7")]
    [InlineData("feature/manual", "DFAI-100: Agent DB config schema", "DFAI-100")]
    [InlineData("feature/manual", "[DFAI-12] Fix", "DFAI-12")]
    [InlineData("ai-agent/DFAI-1000x", "x", null)]
    [InlineData("ai-agent/cherry-pick-pr-63", "Cherry-pick \"DFAI-100: x\" (#63)", null)]
    [InlineData("feature/manual", "Implements DFAI-100", null)]
    public void FindJiraKey_ReadsBranchThenTitle(string head, string title, string? expected) =>
        Assert.Equal(expected, PullRequestTrackingRecovery.FindJiraKey(Open(1, head, title), "DFAI"));
}
