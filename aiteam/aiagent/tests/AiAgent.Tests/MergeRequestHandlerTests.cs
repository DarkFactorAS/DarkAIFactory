using AiAgent.Capabilities.Review;
using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.Repositories;

namespace AiAgent.Tests;

public sealed class MergeRequestHandlerTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mr-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static StoredAgentPullRequest Pr() =>
        new(5, new AgentPullRequestRecord(1, "DFAI-1", "https://github.com/o/r", 7, "https://github.com/o/r/pull/7"));

    private sealed class FakeGitHub : IMergeRequestGitHub
    {
        public bool Open { get; set; } = true;
        public PullRequestMergeInfo Info { get; set; } = Clean();
        public Exception? MergeError { get; set; }
        public List<(string Sha, string Method)> Merges { get; } = [];

        public Task<IReadOnlyList<StoredAgentPullRequest>> ListOpenPullRequestsAsync(
            IEnumerable<StoredAgentPullRequest> pullRequests, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredAgentPullRequest>>(Open ? pullRequests.ToList() : []);

        public Task<PullRequestMergeInfo> GetMergeInfoAsync(StoredAgentPullRequest pr, string phrase, CancellationToken ct) =>
            Task.FromResult(Info);

        public Task MergeAsync(StoredAgentPullRequest pr, string headSha, string method, CancellationToken ct)
        {
            if (MergeError is not null) throw MergeError;
            Merges.Add((headSha, method));
            return Task.CompletedTask;
        }
    }

    private static PullRequestMergeInfo Clean() =>
        new(false, "clean", Sha, "aiagent/DFAI-1", ["src/a.cs"]) { BaseBranch = "main" };

    private sealed class FakeGit : IGitRunner
    {
        public IReadOnlyList<string> Conflicts { get; set; } = [];
        public bool FailPush { get; set; }
        public List<string[]> Calls { get; } = [];

        public Task<GitResult> RunAsync(
            string dir, IReadOnlyList<string> args, bool creds, CancellationToken ct, bool allowExitCodeOne = false)
        {
            Calls.Add([.. args]);
            if (args[0] == "push" && FailPush) throw new InvalidOperationException("push rejected");
            if (args[0] == "clone")
            {
                var target = args[^1];
                foreach (var file in Conflicts)
                {
                    var path = Path.Combine(target, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "<<<<<<< HEAD\na\n=======\nb\n>>>>>>> origin/main\n");
                }
            }

            var (exit, output) = args[0] switch
            {
                "rev-parse" => (0, Sha),
                // merge-base --is-ancestor: exit 1 means the base is not contained
                "merge-base" => (BehindBase && args.Any(a => a.StartsWith("refs/remotes/origin/")) ? 1 : 0, ""),
                "merge" => (Conflicts.Count > 0 ? 1 : 0, ""),
                "diff" => (0, string.Join('\0', Conflicts)),
                _ => (0, "")
            };
            return Task.FromResult(new GitResult(exit, output, ""));
        }

        public bool BehindBase { get; set; }
        public bool Pushed => Calls.Any(call => call[0] == "push");
    }

    private sealed class FakeRunner(bool resolve, Exception? error = null) : IFixRunner
    {
        public List<string> Prompts { get; } = [];

        public Task RunAsync(string workspacePath, string prompt, CancellationToken ct)
        {
            Prompts.Add(prompt);
            if (error is not null) throw error;
            if (resolve)
            {
                foreach (var file in Directory.GetFiles(workspacePath, "*", SearchOption.AllDirectories))
                {
                    File.WriteAllText(file, "resolved\n");
                }
            }

            return Task.CompletedTask;
        }
    }

    private (MergeRequestHandler Handler, FakeGitHub GitHub, FakeGit Git, FakeRunner Runner) Create(
        bool resolve = true, Exception? runnerError = null)
    {
        var github = new FakeGitHub();
        var git = new FakeGit();
        var runner = new FakeRunner(resolve, runnerError);
        var options = new ReviewOptions("secret-token", "reviewer", WorkspaceRoot: _root) { MergeMethod = "squash" };
        return (new MergeRequestHandler(options, github, git, runner), github, git, runner);
    }

    private static Task<MergeRequestResult> Run(MergeRequestHandler handler) =>
        handler.HandleAsync("DFAI-1", [Pr()], CancellationToken.None);

    [Fact]
    public async Task CleanPullRequestIsMergedPinnedToTheHeadWithoutAutoMergeOptIn()
    {
        var (handler, github, git, _) = Create();
        github.Info = Clean() with { HeadBranch = "aiagent/parent/DFAI-1" }; // parent-branch exclusion is ignored
        github.Info = github.Info with { ChangedFiles = ["aiteam/x.cs"] };   // agent-code exclusion is ignored

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Merged, result.Outcome);
        Assert.Equal([(Sha, "squash")], github.Merges);
        Assert.False(git.Pushed);
    }

    [Fact]
    public async Task BranchBehindBaseIsUpdatedAndPushedWithoutForceThenWaitsForChecks()
    {
        var (handler, github, git, runner) = Create();
        github.Info = Clean() with { BehindBy = 2 };
        git.BehindBase = true;

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Pending, result.Outcome);
        Assert.True(result.Pushed);
        Assert.Empty(github.Merges);
        Assert.Empty(runner.Prompts);
        var push = Assert.Single(git.Calls, call => call[0] == "push");
        Assert.DoesNotContain("--force", push);
        Assert.DoesNotContain("-f", push);
        Assert.Contains("HEAD:refs/heads/aiagent/DFAI-1", push);
    }

    [Fact]
    public async Task HeadAlreadyContainingBaseSkipsThePush()
    {
        var (handler, github, git, _) = Create();
        github.Info = Clean() with { BehindBy = 1 };
        git.BehindBase = false;

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Pending, result.Outcome);
        Assert.False(result.Pushed);
        Assert.False(git.Pushed);
    }

    [Fact]
    public async Task ConflictsAreResolvedByTheRunnerCommittedAndPushed()
    {
        var (handler, github, git, runner) = Create();
        github.Info = Clean() with { BehindBy = 1 };
        git.BehindBase = true;
        git.Conflicts = ["src/a.cs"];

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Pending, result.Outcome);
        Assert.True(result.Pushed);
        Assert.Contains("src/a.cs", Assert.Single(runner.Prompts));
        Assert.Contains(git.Calls, call => call[0] == "commit");
        Assert.True(git.Pushed);
    }

    [Fact]
    public async Task UnresolvedConflictFailsWithoutPushing()
    {
        var (handler, github, git, _) = Create(resolve: false);
        github.Info = Clean() with { BehindBy = 1 };
        git.BehindBase = true;
        git.Conflicts = ["src/a.cs"];

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Failed, result.Outcome);
        Assert.Contains("conflict markers remain", result.Reason);
        Assert.False(git.Pushed);
    }

    [Fact]
    public async Task FailedRunnerValidationFailsWithoutPushing()
    {
        var (handler, github, git, _) = Create(runnerError: new InvalidOperationException("The runner exited with code 3: build failed"));
        github.Info = Clean() with { BehindBy = 1 };
        git.BehindBase = true;
        git.Conflicts = ["src/a.cs"];

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Failed, result.Outcome);
        Assert.Contains("build failed", result.Reason);
        Assert.False(git.Pushed);
    }

    [Fact]
    public async Task RedChecksFailTheRequest()
    {
        var (handler, github, _, _) = Create();
        github.Info = Clean() with { ChecksProblem = "check 'build' concluded 'failure'" };

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Failed, result.Outcome);
        Assert.Contains("checks are not green", result.Reason);
        Assert.Empty(github.Merges);
    }

    [Fact]
    public async Task PendingChecksAreRetriedInTheNextCycle()
    {
        var (handler, github, _, _) = Create();
        github.Info = Clean() with { ChecksProblem = "check 'build' is not finished", ChecksPending = true };

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Pending, result.Outcome);
        Assert.Empty(github.Merges);
    }

    [Fact]
    public async Task NoOpenPullRequestFails()
    {
        var (handler, github, _, _) = Create();
        github.Open = false;

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Failed, result.Outcome);
        Assert.Contains("no open pull request", result.Reason);
    }

    [Fact]
    public async Task GitHubMergeErrorFailsWithoutLeakingTheToken()
    {
        var (handler, github, _, _) = Create();
        github.MergeError = new HttpRequestException("GitHub request PUT merge failed with 405 secret-token.");

        var result = await Run(handler);

        Assert.Equal(MergeRequestOutcome.Failed, result.Outcome);
        Assert.DoesNotContain("secret-token", result.Reason);
        Assert.Contains("405", result.Reason);
    }

    [Fact]
    public async Task DraftOrNonCleanStateIsNotMerged()
    {
        var (handler, github, _, _) = Create();
        github.Info = Clean() with { IsDraft = true };
        Assert.Equal(MergeRequestOutcome.Failed, (await Run(handler)).Outcome);

        github.Info = Clean() with { MergeableState = "blocked" };
        Assert.Equal(MergeRequestOutcome.Failed, (await Run(handler)).Outcome);

        github.Info = Clean() with { MergeableState = "unknown" };
        Assert.Equal(MergeRequestOutcome.Pending, (await Run(handler)).Outcome);
        Assert.Empty(github.Merges);
    }
}
