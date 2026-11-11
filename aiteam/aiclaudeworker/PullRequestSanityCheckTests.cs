using AiTeam.Tests;
using ClaudeCodingAgent.Repository;
using Pr = ClaudeCodingAgent.GitHubRepository.PullRequestInfo;
using State = ClaudeCodingAgent.GitHubRepository.PullRequestState;

namespace ClaudeCodingAgent.Tests;

public sealed class PullRequestSanityCheckTests
{
    private static readonly Uri RepositoryUrl = new("https://github.com/owner/repo");
    private const string Parent = "ai-agent/TASK-1";

    private static readonly AgentConfiguration Configuration = new(
        new Uri("https://example.atlassian.net"), "bot@example.com", "token", "gh", "repo",
        RepositoryUrl, "project = X", "picked", "failed",
        "In Progress", "In Review", TimeSpan.FromSeconds(30));

    private sealed class FakeGitHub : IPullRequestSanityGitHub
    {
        public List<Pr> PullRequests { get; } = [];
        public HashSet<string> Branches { get; } = ["master"];
        public List<string> Calls { get; } = [];
        public List<string> Comments { get; } = [];
        private long _next = 100;

        public Pr Add(long number, State state, string head, string baseBranch, string title = "Work")
        {
            var pr = new Pr(number, $"https://github.com/owner/repo/pull/{number}", state, baseBranch, head, title);
            PullRequests.Add(pr);
            return pr;
        }

        public Task<Pr> GetPullRequestAsync(long number, CancellationToken cancellationToken)
        {
            Calls.Add($"get {number}");
            return Task.FromResult(PullRequests.Single(pr => pr.Number == number));
        }

        public Task<bool> BranchExistsAsync(string branch, CancellationToken cancellationToken) =>
            Task.FromResult(Branches.Contains(branch));

        public Task CreateBranchFromDefaultBranchAsync(string branch, CancellationToken cancellationToken)
        {
            Calls.Add($"create-branch {branch}");
            Branches.Add(branch);
            return Task.CompletedTask;
        }

        public Task<Pr?> FindPullRequestAsync(string head, string baseBranch, CancellationToken cancellationToken) =>
            Task.FromResult(PullRequests.LastOrDefault(pr => pr.HeadBranch == head && pr.BaseBranch == baseBranch));

        public Task<string> CreatePullRequestAsync(
            string head, string baseBranch, string title, string body, CancellationToken cancellationToken)
        {
            Calls.Add($"create-pr {head}->{baseBranch}");
            return Task.FromResult(Add(++_next, State.Open, head, baseBranch, title).Url);
        }

        public Task ClosePullRequestWithCommentAsync(long number, string comment, CancellationToken cancellationToken)
        {
            Calls.Add($"close {number}");
            Comments.Add(comment);
            var index = PullRequests.FindIndex(pr => pr.Number == number);
            PullRequests[index] = PullRequests[index] with { State = State.Closed };
            return Task.CompletedTask;
        }

        public Task AddPullRequestCommentAsync(long number, string comment, CancellationToken cancellationToken)
        {
            Calls.Add($"comment {number}");
            Comments.Add(comment);
            return Task.CompletedTask;
        }

        public Task<string> CreateRevertPullRequestAsync(long number, string branch, CancellationToken cancellationToken)
        {
            Calls.Add($"revert {number}");
            Branches.Add(branch);
            var original = PullRequests.Single(pr => pr.Number == number);
            return Task.FromResult(Add(++_next, State.Open, branch, original.BaseBranch).Url);
        }

        public Task PushCherryPickBranchAsync(
            long number, string targetBranch, string branch, CancellationToken cancellationToken)
        {
            Calls.Add($"cherry-pick {number} onto {targetBranch}");
            Branches.Add(branch);
            return Task.CompletedTask;
        }
    }

    private sealed class Fixture
    {
        public FakeGitHub GitHub { get; } = new();
        public DatabaseStub Database { get; } = new();
        public List<string> Replaced { get; } = [];
        public List<string> Deleted { get; } = [];
        public List<string> JiraLookups { get; } = [];
        public Dictionary<string, JiraIssue> Issues { get; } = new()
        {
            ["TASK-2"] = new JiraIssue("TASK-2", "S", "D") { IssueTypeName = "Sub-Task", ParentKey = "TASK-1" },
            ["TASK-3"] = new JiraIssue("TASK-3", "S", "D") { IssueTypeName = "Sub-Task", ParentKey = "TASK-1" },
            ["TASK-9"] = new JiraIssue("TASK-9", "S", "D") { IssueTypeName = "Task" }
        };

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
                if (command.CommandText.StartsWith("UPDATE pull_requests", StringComparison.Ordinal))
                {
                    Replaced.Add($"{command.Values["@old_pr_number"]}->{command.Values["@new_pr_number"]}");
                }

                if (command.CommandText.StartsWith("DELETE FROM pull_requests", StringComparison.Ordinal))
                {
                    Deleted.Add($"{command.Values["@pr_number"]}");
                }

                return 1;
            };
        }

        public Task<IReadOnlyList<SanityCheckAction>> RunAsync() =>
            new PullRequestSanityCheck(
                Configuration,
                new PullRequestRepository(Database),
                (key, _) =>
                {
                    JiraLookups.Add(key);
                    return Task.FromResult(Issues[key]);
                },
                GitHub).RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OpenPullRequestWithWrongBase_IsReplacedByPullRequestIntoParentBranch()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master", "Do work");

        var actions = await fixture.RunAsync();

        var action = Assert.Single(actions);
        Assert.Equal(PullRequestSanityCheck.RepairedByOpenPullRequest, action.Kind);
        Assert.Equal("https://github.com/owner/repo/pull/101", action.NewPullRequestUrl);
        Assert.Equal(
            ["get 5", $"create-branch {Parent}", $"create-pr ai-agent/TASK-2-abc->{Parent}", "close 5"],
            fixture.GitHub.Calls);
        Assert.Contains("pull/101", Assert.Single(fixture.GitHub.Comments));
        Assert.Equal(["5->101"], fixture.Replaced);
        Assert.Equal("Do work", fixture.GitHub.PullRequests.Single(pr => pr.Number == 101).Title);
    }

    [Fact]
    public async Task OpenPullRequestWithWrongBase_DoesNotRecreateExistingParentBranch()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Branches.Add(Parent);
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master");

        await fixture.RunAsync();

        Assert.DoesNotContain(fixture.GitHub.Calls, call => call.StartsWith("create-branch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenPullRequestWithWrongBase_IsIdempotentAfterInterruptedRun()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Branches.Add(Parent);
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master");
        // A previous run created the replacement but stopped before closing the old pull request.
        fixture.GitHub.Add(7, State.Open, "ai-agent/TASK-2-abc", Parent);

        var actions = await fixture.RunAsync();

        Assert.Equal("https://github.com/owner/repo/pull/7", Assert.Single(actions).NewPullRequestUrl);
        Assert.DoesNotContain(fixture.GitHub.Calls, call => call.StartsWith("create-pr", StringComparison.Ordinal));
        Assert.Contains("close 5", fixture.GitHub.Calls);
        Assert.Equal(["5->7"], fixture.Replaced);
    }

    [Fact]
    public async Task OpenPullRequestWithWrongBase_SecondRunDoesNothing()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master");
        await fixture.RunAsync();
        fixture.GitHub.Calls.Clear();
        fixture.Replaced.Clear();
        // The table now tracks the replacement pull request, which targets the parent branch.
        fixture.Database.Rows.Rows[0]["pr_number"] = 101L;

        var actions = await fixture.RunAsync();

        Assert.Empty(actions);
        Assert.Equal(["get 101"], fixture.GitHub.Calls);
        Assert.Empty(fixture.Replaced);
    }

    [Fact]
    public async Task MergedPullRequestWithWrongBase_IsRolledBackAndCherryPickedIntoParentBranch()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Merged, "ai-agent/TASK-2-abc", "master", "Do work");

        Assert.Empty(await fixture.RunAsync());
        Assert.DoesNotContain($"create-branch {Parent}", fixture.GitHub.Calls);
        var rollback = fixture.GitHub.PullRequests.Single(pr => pr.HeadBranch == "ai-agent/rollback-pr-5");
        var rollbackIndex = fixture.GitHub.PullRequests.IndexOf(rollback);
        fixture.GitHub.PullRequests[rollbackIndex] = rollback with { State = State.Merged };
        fixture.GitHub.Calls.Clear();

        var actions = await fixture.RunAsync();

        var action = Assert.Single(actions);
        Assert.Equal(PullRequestSanityCheck.RepairedByMergedPullRequest, action.Kind);
        Assert.Equal(
            [
                "get 5", $"create-branch {Parent}", $"cherry-pick 5 onto {Parent}",
                $"create-pr ai-agent/cherry-pick-pr-5->{Parent}"
            ],
            fixture.GitHub.Calls);
        rollback = fixture.GitHub.PullRequests.Single(pr => pr.HeadBranch == "ai-agent/rollback-pr-5");
        Assert.Equal("master", rollback.BaseBranch);
        var cherryPick = fixture.GitHub.PullRequests.Single(pr => pr.HeadBranch == "ai-agent/cherry-pick-pr-5");
        Assert.Equal(Parent, cherryPick.BaseBranch);
        Assert.Equal(cherryPick.Url, action.NewPullRequestUrl);
        Assert.Equal([$"5->{cherryPick.Number}"], fixture.Replaced);
    }

    [Fact]
    public async Task MergedPullRequestWithClosedRollback_IsNotReplaced()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Merged, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(8, State.Closed, "ai-agent/rollback-pr-5", "master");

        Assert.Empty(await fixture.RunAsync());

        Assert.DoesNotContain($"create-branch {Parent}", fixture.GitHub.Calls);
        Assert.DoesNotContain($"cherry-pick 5 onto {Parent}", fixture.GitHub.Calls);
        Assert.Empty(fixture.Replaced);
    }

    [Fact]
    public async Task MisplacedMergesForSameParent_WaitForAllRollbacksBeforeCreatingParentBranch()
    {
        var fixture = new Fixture(("TASK-2", 5), ("TASK-3", 6));
        fixture.GitHub.Add(5, State.Merged, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(6, State.Merged, "ai-agent/TASK-3-abc", "master");

        Assert.Empty(await fixture.RunAsync());
        Assert.DoesNotContain($"create-branch {Parent}", fixture.GitHub.Calls);
        foreach (var rollback in fixture.GitHub.PullRequests
                     .Where(pr => pr.HeadBranch.StartsWith("ai-agent/rollback-pr-", StringComparison.Ordinal))
                     .ToArray())
        {
            var index = fixture.GitHub.PullRequests.IndexOf(rollback);
            fixture.GitHub.PullRequests[index] = rollback with { State = State.Merged };
        }

        fixture.GitHub.Calls.Clear();
        var actions = await fixture.RunAsync();

        Assert.Equal(2, actions.Count);
        Assert.Single(fixture.GitHub.Calls, call => call == $"create-branch {Parent}");
        Assert.True(
            fixture.GitHub.Calls.IndexOf($"create-branch {Parent}") <
            fixture.GitHub.Calls.FindIndex(call => call.StartsWith("cherry-pick", StringComparison.Ordinal)));
        Assert.Equal(2, fixture.GitHub.Calls.Count(call => call.StartsWith("cherry-pick", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task OpenPullRequest_DoesNotSeedParentWhileAnotherMisplacedMergeIsUnrolledBack()
    {
        var fixture = new Fixture(("TASK-2", 5), ("TASK-3", 6));
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(6, State.Merged, "ai-agent/TASK-3-abc", "master");

        Assert.Empty(await fixture.RunAsync());

        Assert.DoesNotContain($"create-branch {Parent}", fixture.GitHub.Calls);
        Assert.DoesNotContain(
            fixture.GitHub.Calls,
            call => call.StartsWith("create-pr ai-agent/TASK-2-abc->", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergedPullRequestWithClosedCherryPick_IsRecreated()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Branches.UnionWith([Parent, "ai-agent/rollback-pr-5", "ai-agent/cherry-pick-pr-5"]);
        fixture.GitHub.Add(5, State.Merged, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(8, State.Open, "ai-agent/rollback-pr-5", "master");
        fixture.GitHub.Add(9, State.Closed, "ai-agent/cherry-pick-pr-5", Parent);

        var actions = await fixture.RunAsync();

        var replacement = Assert.Single(fixture.GitHub.PullRequests, pr => pr.Number == 101);
        Assert.Equal(PullRequestSanityCheck.RepairedByMergedPullRequest, Assert.Single(actions).Kind);
        Assert.Equal(replacement.Url, actions[0].NewPullRequestUrl);
        Assert.Contains($"create-pr ai-agent/cherry-pick-pr-5->{Parent}", fixture.GitHub.Calls);
        Assert.Equal([$"5->{replacement.Number}"], fixture.Replaced);
    }

    [Fact]
    public async Task MergedPullRequestWithWrongBase_IsIdempotentWhenRollbackAndCherryPickExist()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Branches.UnionWith([Parent, "ai-agent/rollback-pr-5", "ai-agent/cherry-pick-pr-5"]);
        fixture.GitHub.Add(5, State.Merged, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(8, State.Open, "ai-agent/rollback-pr-5", "master");
        fixture.GitHub.Add(9, State.Open, "ai-agent/cherry-pick-pr-5", Parent);

        var actions = await fixture.RunAsync();

        Assert.Equal("https://github.com/owner/repo/pull/9", Assert.Single(actions).NewPullRequestUrl);
        Assert.Equal(["get 5"], fixture.GitHub.Calls);
        Assert.Empty(fixture.Deleted);
    }

    [Fact]
    public async Task ClosedUnmergedPullRequestWithWrongBase_IsCommentedOnceAndRowDeleted()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Closed, "ai-agent/TASK-2-abc", "master");

        var actions = await fixture.RunAsync();

        var action = Assert.Single(actions);
        Assert.Equal(PullRequestSanityCheck.RemovedClosedPullRequest, action.Kind);
        Assert.Equal(["5"], fixture.Deleted);
        Assert.Equal(["subtask was created from the wrong branch"], fixture.GitHub.Comments);
        Assert.Single(fixture.GitHub.Calls, call => call == "comment 5");
        Assert.Empty(fixture.Replaced);
    }

    [Fact]
    public async Task ClosedPullRequestWithClosedReplacement_IsCleanedUp()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Closed, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(6, State.Closed, "ai-agent/TASK-2-abc", Parent);

        await fixture.RunAsync();

        Assert.Equal(["5"], fixture.Deleted);
        Assert.Single(fixture.GitHub.Comments);
    }

    [Fact]
    public async Task ClosedPullRequestWithOpenReplacement_MovesTrackingWithoutComment()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, State.Closed, "ai-agent/TASK-2-abc", "master");
        fixture.GitHub.Add(6, State.Open, "ai-agent/TASK-2-abc", Parent);

        await fixture.RunAsync();

        Assert.Equal(["5->6"], fixture.Replaced);
        Assert.Empty(fixture.Deleted);
        Assert.Empty(fixture.GitHub.Comments);
    }

    [Fact]
    public async Task MergedPullRequestWithWrongBase_ResumesWhenRollbackBranchWasPushedWithoutPullRequest()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Branches.UnionWith([Parent, "ai-agent/rollback-pr-5"]);
        fixture.GitHub.Add(5, State.Merged, "ai-agent/TASK-2-abc", "master");

        await fixture.RunAsync();

        Assert.DoesNotContain("revert 5", fixture.GitHub.Calls);
        Assert.Contains("create-pr ai-agent/rollback-pr-5->master", fixture.GitHub.Calls);
        Assert.Contains($"create-pr ai-agent/cherry-pick-pr-5->{Parent}", fixture.GitHub.Calls);
    }

    [Fact]
    public async Task PullRequestsOfNonSubTasks_AreNeverTouched()
    {
        var fixture = new Fixture(("TASK-9", 5), ("TASK-9", 6));
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-9-abc", "master");
        fixture.GitHub.Add(6, State.Merged, "ai-agent/TASK-9-def", "master");

        var actions = await fixture.RunAsync();

        Assert.Empty(actions);
        Assert.Empty(fixture.GitHub.Calls);
        Assert.Empty(fixture.Replaced);
        Assert.Equal(["TASK-9"], fixture.JiraLookups);
    }

    [Fact]
    public async Task LocalizedSubTaskWithParentKey_IsReconciled()
    {
        var fixture = new Fixture(("TASK-2", 5));
        fixture.Issues["TASK-2"] = fixture.Issues["TASK-2"] with { IssueTypeName = "Tâche personnalisée" };
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master");

        Assert.Single(await fixture.RunAsync());
        Assert.Contains($"create-branch {Parent}", fixture.GitHub.Calls);
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("Merged")]
    [InlineData("Closed")]
    public async Task CorrectBasePullRequests_AreLeftAlone(string stateName)
    {
        var state = Enum.Parse<State>(stateName);
        var fixture = new Fixture(("TASK-2", 5));
        fixture.GitHub.Add(5, state, "ai-agent/TASK-2-abc", Parent);

        Assert.Empty(await fixture.RunAsync());
        Assert.Equal(["get 5"], fixture.GitHub.Calls);
    }

    [Fact]
    public async Task FailureForOnePullRequest_DoesNotStopTheOthers()
    {
        var fixture = new Fixture(("TASK-2", 4), ("TASK-2", 5));
        fixture.GitHub.Branches.Add(Parent);
        fixture.GitHub.Add(5, State.Open, "ai-agent/TASK-2-abc", "master");

        var actions = await fixture.RunAsync();

        Assert.Equal(5, Assert.Single(actions).PullRequestNumber);
    }
}
