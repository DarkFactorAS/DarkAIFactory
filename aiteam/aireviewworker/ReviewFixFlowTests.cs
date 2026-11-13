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
        /// <summary>Changed files per comment id, picked from the last prompt; falls back to <see cref="Changed"/>.</summary>
        public Dictionary<long, List<string>> ChangedByComment { get; } = [];
        public Dictionary<long, int> Used { get; } = [];
        public List<(long[] Ids, string Status, int Change, string? Error)> Records { get; } = [];
        public Exception? RunnerError { get; set; }
        public Dictionary<long, Exception> RunnerErrorByComment { get; } = [];
        public Exception? PushError { get; set; }
        public string? CommandFailure { get; set; }
        public string? Pushed { get; private set; }
        public Queue<IReadOnlyList<string>>? ChangedSequence { get; set; }
        public List<string> Baselines { get; } = [];
        public List<string> CommitMessages { get; } = [];
        public List<string> Prompts { get; } = [];

        private long? CurrentComment => ChangedByComment.Keys.Concat(RunnerErrorByComment.Keys)
            .Cast<long?>().FirstOrDefault(id => Prompts.Count > 0 && Prompts[^1].Contains($"[comment {id}]"));

        public Task<string> GetRevisionAsync(string w, CancellationToken c) => Task.FromResult("baseline");
        public Task RunAsync(string w, string prompt, CancellationToken c)
        {
            Calls.Add("agent");
            Prompts.Add(prompt);
            var error = CurrentComment is { } id && RunnerErrorByComment.TryGetValue(id, out var e) ? e : RunnerError;
            return error is null ? Task.CompletedTask : Task.FromException(error);
        }
        public Task<IReadOnlyList<string>> ChangedFilesAsync(string w, string baseline, CancellationToken c)
        {
            Baselines.Add(baseline);
            if (ChangedSequence is { Count: > 0 })
            {
                return Task.FromResult(ChangedSequence.Dequeue());
            }

            return Task.FromResult<IReadOnlyList<string>>(
                CurrentComment is { } id && ChangedByComment.TryGetValue(id, out var changed) ? changed : Changed);
        }
        public Task DiscardChangesAsync(string w, string baseline, CancellationToken c)
        {
            Baselines.Add(baseline);
            Calls.Add("discard");
            return Task.CompletedTask;
        }
        public Task CommitAsync(string w, string m, CancellationToken c)
        {
            Calls.Add("commit");
            CommitMessages.Add(m);
            return Task.CompletedTask;
        }
        public Task PushAsync(string w, Uri u, string b, string baseline, CancellationToken c)
        {
            Baselines.Add(baseline);
            Calls.Add("push");
            if (PushError is not null)
            {
                return Task.FromException(PushError);
            }

            Pushed = b;
            return Task.CompletedTask;
        }
        Task<string?> IProjectCommandRunner.RunAsync(string command, string w, CancellationToken c)
        {
            Calls.Add(command);
            return Task.FromResult(CommandFailure);
        }
        public IReadOnlyDictionary<long, int> GetAttempts(long id, IReadOnlyCollection<long> ids) => Used;
        public void Record(long id, IReadOnlyCollection<long> ids, string status, int change, string? error) =>
            Records.Add((ids.ToArray(), status, change, error));
    }

    private static Task<FixResult> Run(
        Fakes f, FixOptions? options = null, string branch = "feature/x", CancellationToken cancellationToken = default,
        params ReviewComment[] comments) =>
        new ReviewFixFlow(f, f, f, f, options ?? new FixOptions(3)).RunAsync(
            Pr, branch, comments.Length == 0 ? [Comment(10)] : comments, "/tmp/ws", cancellationToken);

    [Fact]
    public async Task PushesToHeadBranchAfterBuildAndTest()
    {
        var f = new Fakes();
        var result = await Run(f, new FixOptions(3, "build", "test"));
        Assert.Equal(FixOutcome.Pushed, result.Outcome);
        Assert.Equal(1, result.PushedCount);
        Assert.Equal(["agent", "build", "test", "commit", "push"], f.Calls);
        Assert.All(f.Baselines, baseline => Assert.Equal("baseline", baseline));
        Assert.Equal("feature/x", f.Pushed);
        Assert.Equal(("InProgress", 1, null), (f.Records[0].Status, f.Records[0].Change, f.Records[0].Error));
        Assert.Equal(("Resolved", 0), (f.Records[^1].Status, f.Records[^1].Change));
    }

    [Fact]
    public async Task FixesEachCommentInItsOwnRunCommitAndPush()
    {
        var f = new Fakes();
        f.ChangedByComment[10] = ["src/A.cs"];
        f.ChangedByComment[11] = ["src/B.cs"];

        var result = await Run(f, comments:
            [Comment(10, "src/A.cs", "Null check is missing."), Comment(11, "src/B.cs", "Dispose the client.")]);

        Assert.Equal(FixOutcome.Pushed, result.Outcome);
        Assert.Equal(2, result.PushedCount);
        Assert.Equal(["agent", "commit", "push", "agent", "commit", "push"], f.Calls);
        Assert.Equal(
            ["DFAI-1: Null check is missing (src/A.cs:3)", "DFAI-1: Dispose the client (src/B.cs:3)"],
            f.CommitMessages.Select(message => message.Split('\n')[0]));
        Assert.Contains("[comment 10]", f.Prompts[0]);
        Assert.DoesNotContain("[comment 11]", f.Prompts[0]);
        Assert.Contains("[comment 11]", f.Prompts[1]);
        Assert.DoesNotContain("src/A.cs", f.Prompts[1]);
        Assert.Contains(f.Records, record => record.Status == "Resolved" && record.Ids.SequenceEqual([10L]));
        Assert.Contains(f.Records, record => record.Status == "Resolved" && record.Ids.SequenceEqual([11L]));
    }

    [Fact]
    public async Task CommentsOnTheSameFileAreFixedAndPushedSeparately()
    {
        var f = new Fakes { Changed = ["src/A.cs"] };

        await Run(f, comments: [Comment(10, "src/A.cs"), Comment(11, "src/A.cs")]);

        Assert.Equal(2, f.Calls.Count(call => call == "push"));
        Assert.Equal(2, f.Records.Count(record => record.Status == "Resolved"));
    }

    [Fact]
    public async Task ChangeToAnotherCommentsFileIsOutOfBounds()
    {
        var f = new Fakes();
        f.ChangedByComment[11] = ["src/A.cs"];

        var result = await Run(f, comments: [Comment(11, "src/B.cs")]);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["agent", "discard"], f.Calls);
        Assert.Contains("src/A.cs", result.Error);
    }

    [Fact]
    public async Task NoChangesMeansNoPushAndAttemptIsPersisted()
    {
        var f = new Fakes { Changed = [] };
        var result = await Run(f);
        Assert.Equal(FixOutcome.NoChanges, result.Outcome);
        Assert.DoesNotContain("push", f.Calls);
        Assert.DoesNotContain("commit", f.Calls);
        Assert.Contains(f.Records, r => r.Status == "InProgress" && r.Change == 1);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsuccessfulAttemptsUseEachCommentsOwnAttemptCount(bool noChanges)
    {
        var f = noChanges
            ? new Fakes { Changed = [] }
            : new Fakes { RunnerError = new InvalidOperationException("boom") };
        f.Used[10] = 2;

        await Run(f, comments: [Comment(10), Comment(11)]);

        Assert.Contains(f.Records, record => record.Status == "Failed" && record.Ids.SequenceEqual([10L]));
        Assert.Contains(f.Records, record => record.Status == "Pending" && record.Ids.SequenceEqual([11L]));
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
        Assert.Contains("discard", f.Calls);
    }

    [Fact]
    public async Task TimeoutIsACountedFailedAttemptAndTheNextCommentStillRuns()
    {
        var f = new Fakes();
        f.RunnerErrorByComment[10] = new TimeoutException("Claude Code did not finish within 30 minute(s).");
        f.ChangedByComment[11] = ["src/A.cs"];

        var result = await Run(f, comments: [Comment(10), Comment(11)]);

        Assert.Equal(FixOutcome.Pushed, result.Outcome);
        Assert.Equal((1, 1), (result.PushedCount, result.FailedCount));
        var timeout = Assert.Single(f.Records, record => record.Ids.SequenceEqual([10L]) && record.Status != "InProgress");
        Assert.Equal(("Pending", 0), (timeout.Status, timeout.Change));
        Assert.Contains("did not finish", timeout.Error);
        Assert.DoesNotContain(f.Records, record => record.Change < 0);
    }

    [Fact]
    public async Task AlreadyCancelledWorkerRunsAndRecordsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var f = new Fakes();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Run(f, cancellationToken: cancellation.Token, comments: [Comment(10)]));

        Assert.Empty(f.Calls);
        Assert.Empty(f.Records);
    }

    [Fact]
    public async Task ShutdownDuringRunRecordsCancelledWithoutCountingTheAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var f = new CancellingFakes(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ReviewFixFlow(f, f, f, f, new FixOptions(3)).RunAsync(
                Pr, "feature/x", [Comment(10), Comment(11)], "/tmp/ws", cancellation.Token));

        Assert.Equal(("InProgress", 1), (f.Records[0].Status, f.Records[0].Change));
        Assert.Equal(("Pending", -1), (f.Records[1].Status, f.Records[1].Change));
        Assert.Equal(2, f.Records.Count);
    }

    private sealed class CancellingFakes(CancellationTokenSource cancellation)
        : IFixRunner, IReviewGit, IProjectCommandRunner, IAttemptStore
    {
        public List<(long[] Ids, string Status, int Change, string? Error)> Records { get; } = [];
        public Task RunAsync(string w, string prompt, CancellationToken c)
        {
            cancellation.Cancel();
            return Task.FromCanceled(c);
        }
        public Task<string> GetRevisionAsync(string w, CancellationToken c) => Task.FromResult("baseline");
        public Task<IReadOnlyList<string>> ChangedFilesAsync(string w, string b, CancellationToken c) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task DiscardChangesAsync(string w, string b, CancellationToken c) => Task.CompletedTask;
        public Task CommitAsync(string w, string m, CancellationToken c) => Task.CompletedTask;
        public Task PushAsync(string w, Uri u, string b, string baseline, CancellationToken c) => Task.CompletedTask;
        Task<string?> IProjectCommandRunner.RunAsync(string command, string w, CancellationToken c) =>
            Task.FromResult<string?>(null);
        public IReadOnlyDictionary<long, int> GetAttempts(long id, IReadOnlyCollection<long> ids) =>
            new Dictionary<long, int>();
        public void Record(long id, IReadOnlyCollection<long> ids, string status, int change, string? error) =>
            Records.Add((ids.ToArray(), status, change, error));
    }

    [Fact]
    public async Task PushFailureDiscardsTheCommitAndStopsRemainingComments()
    {
        var f = new Fakes { PushError = new InvalidOperationException("rejected: non-fast-forward") };

        var result = await Run(f, comments: [Comment(10), Comment(11)]);

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["agent", "commit", "push", "discard"], f.Calls);
        Assert.DoesNotContain(f.Records, record => record.Ids.Contains(11L));
    }

    [Fact]
    public async Task FailingTestDiscardsAndDoesNotPush()
    {
        var f = new Fakes { CommandFailure = "exit code 1" };
        var result = await Run(f, new FixOptions(3, null, "test"));
        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["agent", "test", "discard"], f.Calls);
        Assert.Contains("test", f.Records[^1].Error);
    }

    [Fact]
    public async Task RechecksChangedFilesAfterValidationBeforeCommit()
    {
        var f = new Fakes
        {
            ChangedSequence = new Queue<IReadOnlyList<string>>(
                [["src/A.cs"], ["src/A.cs", "src/Generated.cs"]])
        };

        var result = await Run(f, new FixOptions(3, TestCommand: "test"));

        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["agent", "test", "discard"], f.Calls);
        Assert.Contains("src/Generated.cs", result.Error);
    }

    [Fact]
    public async Task ChangesOutsideCitedFilesAreDiscarded()
    {
        var f = new Fakes { Changed = ["src/A.cs", "src/Other.cs"] };
        var result = await Run(f);
        Assert.Equal(FixOutcome.Failed, result.Outcome);
        Assert.Equal(["agent", "discard"], f.Calls);
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
