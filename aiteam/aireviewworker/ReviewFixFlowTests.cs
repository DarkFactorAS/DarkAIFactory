using ReviewWorker;

namespace ReviewWorker.Tests;

public sealed class ReviewFixFlowTests
{
    private static readonly Uri Repo = new("https://github.com/owner/repo");
    private static readonly TrackedPullRequest Pr = new(1, "DFAI-1", Repo, 7, "https://github.com/owner/repo/pull/7");

    private static ReviewComment Comment(long id, string path = "src/A.cs", string body = "rename x") =>
        new(id, 1, 7, path, 3, body, "reviewer", "sha");

    private sealed class Fakes : IFixRunner, IReviewGit, IProjectCommandRunner, IAttemptStore
    {
        public List<string> Calls { get; } = [];
        public List<string> Changed { get; set; } = ["src/A.cs"];
        public Dictionary<long, int> Used { get; } = [];
        public List<(string Status, bool Count, string? Error)> Records { get; } = [];
        public Exception? RunnerError { get; set; }
        public string? CommandFailure { get; set; }
        public string? Pushed { get; private set; }

        public Task RunAsync(string w, string prompt, CancellationToken c)
        {
            Calls.Add("copilot");
            return RunnerError is null ? Task.CompletedTask : Task.FromException(RunnerError);
        }
        public Task<IReadOnlyList<string>> ChangedFilesAsync(string w, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<string>>(Changed);
        public Task DiscardChangesAsync(string w, CancellationToken c) { Calls.Add("discard"); return Task.CompletedTask; }
        public Task CommitAsync(string w, string m, CancellationToken c) { Calls.Add("commit"); return Task.CompletedTask; }
        public Task PushAsync(string w, string b, CancellationToken c) { Calls.Add("push"); Pushed = b; return Task.CompletedTask; }
        Task<string?> IProjectCommandRunner.RunAsync(string command, string w, CancellationToken c)
        {
            Calls.Add(command);
            return Task.FromResult(CommandFailure);
        }
        public IReadOnlyDictionary<long, int> GetAttempts(long id, IReadOnlyCollection<long> ids) => Used;
        public void Record(long id, IReadOnlyCollection<long> ids, string status, bool count, string? error) =>
            Records.Add((status, count, error));
    }

    private static Task<FixResult> Run(Fakes f, FixOptions? options = null, string branch = "feature/x", params ReviewComment[] comments) =>
        new ReviewFixFlow(f, f, f, f, options ?? new FixOptions(3)).RunAsync(
            Pr, branch, comments.Length == 0 ? [Comment(10)] : comments, "/tmp/ws", CancellationToken.None);

    [Fact]
    public async Task PushesToHeadBranchAfterBuildAndTest()
    {
        var f = new Fakes();
        var result = await Run(f, new FixOptions(3, "build", "test"));
        Assert.Equal(FixOutcome.Pushed, result.Outcome);
        Assert.Equal(["copilot", "build", "test", "commit", "push"], f.Calls);
        Assert.Equal("feature/x", f.Pushed);
        Assert.Equal(("InProgress", true, null), f.Records[0]);
        Assert.Equal("Resolved", f.Records[^1].Status);
    }

    [Fact]
    public async Task NoChangesMeansNoPushAndAttemptIsPersisted()
    {
        var f = new Fakes { Changed = [] };
        var result = await Run(f);
        Assert.Equal(FixOutcome.NoChanges, result.Outcome);
        Assert.DoesNotContain("push", f.Calls);
        Assert.DoesNotContain("commit", f.Calls);
        Assert.Contains(f.Records, r => r.Status == "InProgress" && r.Count);
        Assert.Equal("Pending", f.Records[^1].Status);
    }

    [Fact]
    public async Task LastAttemptWithoutChangesIsFailed()
    {
        var f = new Fakes { Changed = [] };
        f.Used[10] = 2;
        await Run(f);
        Assert.Equal("Failed", f.Records[^1].Status);
    }

    [Fact]
    public async Task SkipsWhenMaxAttemptsReached()
    {
        var f = new Fakes();
        f.Used[10] = 3;
        var result = await Run(f);
        Assert.Equal(FixOutcome.Skipped, result.Outcome);
        Assert.Empty(f.Calls);
        Assert.Empty(f.Records);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    [InlineData("MASTER")]
    [InlineData("--force")]
    [InlineData("a..b")]
    [InlineData("")]
    public async Task NeverPushesToProtectedOrInvalidBranch(string branch)
    {
        var f = new Fakes();
        var result = await Run(f, branch: branch);
        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Empty(f.Calls);
        Assert.Equal("Failed", f.Records[^1].Status);
    }

    [Fact]
    public async Task RunnerFailureIsRecordedWithoutPush()
    {
        var f = new Fakes { RunnerError = new InvalidOperationException("boom") };
        var result = await Run(f);
        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal("boom", f.Records[^1].Error);
        Assert.Equal("Pending", f.Records[^1].Status);
        Assert.DoesNotContain("push", f.Calls);
    }

    [Fact]
    public async Task FailingTestDiscardsAndDoesNotPush()
    {
        var f = new Fakes { CommandFailure = "exit code 1" };
        var result = await Run(f, new FixOptions(3, null, "test"));
        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["copilot", "test", "discard"], f.Calls);
        Assert.Contains("test", f.Records[^1].Error);
    }

    [Fact]
    public async Task ChangesOutsideCitedFilesAreDiscarded()
    {
        var f = new Fakes { Changed = ["src/A.cs", "src/Other.cs"] };
        var result = await Run(f);
        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["copilot", "discard"], f.Calls);
        Assert.Contains("src/Other.cs", result.Error);
    }

    [Fact]
    public void DecisionHelpers()
    {
        Assert.True(FixDecision.IsPushableBranch("ai-agent/DFAI-1"));
        Assert.Equal("Pending", FixDecision.StatusAfterUnsuccessfulAttempt(1, 3));
        Assert.Equal("Failed", FixDecision.StatusAfterUnsuccessfulAttempt(3, 3));
    }
}
