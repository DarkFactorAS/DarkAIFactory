using System.Text;
using DFCommonLib.Logger;

namespace ReviewWorker;

internal enum FixOutcome { Skipped, NoChanges, Failed, Pushed }

/// <summary>Result of fixing a pull request's comments; counts are per comment.</summary>
internal sealed record FixResult(FixOutcome Outcome, string? Error = null, int PushedCount = 0, int FailedCount = 0);

internal interface IFixRunner
{
    Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken);
}

internal interface IReviewGit
{
    Task RefreshWorkspaceMetadataAsync(string workspacePath, CancellationToken cancellationToken);
    Task<string> GetRevisionAsync(string workspacePath, CancellationToken cancellationToken);
    /// <summary>Paths changed since the revision captured before running untrusted code.</summary>
    Task<IReadOnlyList<string>> ChangedFilesAsync(
        string workspacePath, string baselineRevision, CancellationToken cancellationToken);
    Task DiscardChangesAsync(string workspacePath, string baselineRevision, CancellationToken cancellationToken);
    Task CommitAsync(string workspacePath, string message, CancellationToken cancellationToken);
    /// <summary>Pushes HEAD to the named branch at the trusted repository URL. Must never force.</summary>
    Task PushAsync(
        string workspacePath, Uri repositoryUrl, string branch, string baselineRevision, CancellationToken cancellationToken);
}

internal interface IProjectCommandRunner
{
    /// <summary>Runs a shell command; returns null on success or a failure description.</summary>
    Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken);
}

internal interface IAttemptStore
{
    IReadOnlyDictionary<long, int> GetAttempts(long pullRequestId, IReadOnlyCollection<long> commentIds);
    /// <summary>
    /// Upserts the status of each comment and adds <paramref name="attemptChange"/> (+1 to count an attempt,
    /// -1 to give back an attempt that was interrupted by shutdown, 0 to leave the count) to its attempt count.
    /// </summary>
    void Record(long pullRequestId, IReadOnlyCollection<long> commentIds, string status, int attemptChange, string? error);
}

internal sealed record FixOptions(int MaxAttempts, string? BuildCommand = null, string? TestCommand = null);

/// <summary>Decisions of the fix flow that do not need I/O.</summary>
internal static class FixDecision
{
    private static readonly string[] ProtectedBranches = ["main", "master", "develop", "development", "trunk"];

    public static bool IsPushableBranch(string? branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        branch.Length <= 200 &&
        !branch.StartsWith('-') && !branch.StartsWith('/') && !branch.EndsWith('/') &&
        !branch.Contains("..", StringComparison.Ordinal) &&
        branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/') &&
        !ProtectedBranches.Contains(branch, StringComparer.OrdinalIgnoreCase);

    /// <summary>Status after an attempt that did not push: Pending to retry, Failed once attempts are used up.</summary>
    public static string StatusAfterUnsuccessfulAttempt(int attemptsUsed, int maxAttempts) =>
        attemptsUsed >= maxAttempts ? "Failed" : "Pending";

    /// <summary>Changed files outside the cited set.</summary>
    public static IReadOnlyList<string> OutOfBounds(IEnumerable<string> changed, IEnumerable<string> cited)
    {
        var allowed = new HashSet<string>(cited, StringComparer.Ordinal);
        return changed.Where(path => !allowed.Contains(path)).ToList();
    }
}

/// <summary>
/// Fixes a pull request's review comments one at a time. For each comment: prompt, Claude Code,
/// bounds check (only the comment's file), build/test, then a separate commit and push.
/// Pushes only to the PR head branch, never forced, and never when nothing changed.
/// </summary>
internal sealed class ReviewFixFlow(
    IFixRunner runner,
    IReviewGit git,
    IProjectCommandRunner commands,
    IAttemptStore attempts,
    FixOptions options)
{
    private const string Category = "ReviewFix";

    public async Task<FixResult> RunAsync(
        TrackedPullRequest pullRequest,
        string headBranch,
        IReadOnlyList<ReviewComment> comments,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        if (!FixDecision.IsPushableBranch(headBranch))
        {
            var error = $"Refusing to push to branch '{headBranch}'.";
            DFLogger.LogOutput(DFLogLevel.ERROR, Category, $"PR #{pullRequest.Number}: {error}");
            attempts.Record(pullRequest.Id, comments.Select(c => c.Id).ToList(), "Failed", 0, error);
            return new FixResult(FixOutcome.Failed, error, FailedCount: comments.Count);
        }

        var used = attempts.GetAttempts(pullRequest.Id, comments.Select(c => c.Id).ToList());
        var eligible = comments.Where(c => used.GetValueOrDefault(c.Id) < options.MaxAttempts).ToList();
        if (eligible.Count == 0)
        {
            return new FixResult(FixOutcome.Skipped);
        }

        var pushed = 0;
        var failed = 0;
        string? lastError = null;
        for (var index = 0; index < eligible.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var comment = eligible[index];
            DFLogger.LogOutput(DFLogLevel.INFO, Category,
                $"PR #{pullRequest.Number}: fixing comment {comment.Id} ({index + 1} of {eligible.Count}) " +
                $"at {comment.Path}:{comment.Line?.ToString() ?? "-"}.");
            var result = await FixCommentAsync(
                pullRequest, headBranch, comment, used.GetValueOrDefault(comment.Id), workspacePath, cancellationToken);
            switch (result.Outcome)
            {
                case FixOutcome.Pushed:
                    pushed++;
                    break;
                case FixOutcome.Failed:
                    failed++;
                    lastError = result.Error;
                    break;
            }

            if (result.StopRemaining)
            {
                DFLogger.LogOutput(DFLogLevel.WARNING, Category,
                    $"PR #{pullRequest.Number}: stopping; {eligible.Count - index - 1} comment(s) left for the next cycle.");
                break;
            }
        }

        var outcome = pushed > 0 ? FixOutcome.Pushed : failed > 0 ? FixOutcome.Failed : FixOutcome.NoChanges;
        return new FixResult(outcome, lastError, pushed, failed);
    }

    private async Task<CommentResult> FixCommentAsync(
        TrackedPullRequest pullRequest,
        string headBranch,
        ReviewComment comment,
        int attemptsUsed,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        long[] ids = [comment.Id];
        var prompt = ReviewPromptBuilder.Build([comment]);
        if (Encoding.UTF8.GetByteCount(prompt) > ReviewPromptBuilder.MaxPromptLengthBytes)
        {
            return Fail(pullRequest, ids, attemptsUsed, "The review comment exceeds the maximum prompt size.", terminal: true);
        }

        await git.RefreshWorkspaceMetadataAsync(workspacePath, cancellationToken);
        var baselineRevision = await git.GetRevisionAsync(workspacePath, cancellationToken);
        // Persist the attempt before running so a crash still counts against the limit.
        attempts.Record(pullRequest.Id, ids, "InProgress", 1, null);
        var committed = false;
        try
        {
            await runner.RunAsync(workspacePath, prompt, cancellationToken);

            var cited = ReviewPromptBuilder.CitedPaths([comment]);
            var changed = await git.ChangedFilesAsync(workspacePath, baselineRevision, cancellationToken);
            if (changed.Count == 0)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Category,
                    $"PR #{pullRequest.Number}: comment {comment.Id}: Claude Code made no changes; nothing to push.");
                RecordUnsuccessfulAttempt(pullRequest, ids, attemptsUsed, "Claude Code made no changes.");
                return new CommentResult(FixOutcome.NoChanges);
            }

            var outOfBounds = FixDecision.OutOfBounds(changed, cited);
            if (outOfBounds.Count > 0)
            {
                return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                    $"Changes outside the cited file were discarded: {string.Join(", ", outOfBounds)}.", cancellationToken);
            }

            foreach (var command in new[] { options.BuildCommand, options.TestCommand })
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var failure = await commands.RunAsync(command, workspacePath, cancellationToken);
                if (failure is not null)
                {
                    return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                        $"'{command}' failed: {failure}", cancellationToken);
                }
            }

            changed = await git.ChangedFilesAsync(workspacePath, baselineRevision, cancellationToken);
            outOfBounds = FixDecision.OutOfBounds(changed, cited);
            if (outOfBounds.Count > 0)
            {
                return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                    $"Changes outside the cited file were discarded: {string.Join(", ", outOfBounds)}.", cancellationToken);
            }

            await git.CommitAsync(workspacePath,
                ReviewCommitMessage.Build(pullRequest.JiraKey, comment), cancellationToken);
            committed = true;
            await git.PushAsync(workspacePath, pullRequest.RepositoryUrl, headBranch, baselineRevision, cancellationToken);
            attempts.Record(pullRequest.Id, ids, "Resolved", 0, null);
            DFLogger.LogOutput(DFLogLevel.INFO, Category,
                $"PR #{pullRequest.Number}: pushed fix for comment {comment.Id} to '{headBranch}'.");
            return new CommentResult(FixOutcome.Pushed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Worker shutdown: give the attempt back so it does not count against the limit.
            attempts.Record(pullRequest.Id, ids, "Pending", -1, "Cancelled by worker shutdown.");
            throw;
        }
        catch (SandboxWorkspaceAbandonedException exception) when (!cancellationToken.IsCancellationRequested)
        {
            RecordUnsuccessfulAttempt(pullRequest, ids, attemptsUsed, exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            // Includes TimeoutException from the sandbox: a slow run is a real, counted attempt.
            // A failed push leaves a local commit; stop so later comments do not push it.
            return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                exception.Message, cancellationToken, stopRemaining: committed);
        }
    }

    private async Task<CommentResult> DiscardAndFailAsync(
        TrackedPullRequest pullRequest,
        IReadOnlyCollection<long> ids,
        int attemptsUsed,
        string workspacePath,
        string baselineRevision,
        string error,
        CancellationToken cancellationToken,
        bool stopRemaining = false)
    {
        try
        {
            await git.DiscardChangesAsync(workspacePath, baselineRevision, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            attempts.Record(pullRequest.Id, ids, "Pending", -1, "Cancelled by worker shutdown.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The checkout is in an unknown state; do not build later fixes on it.
            error = $"{error} Could not reset the checkout: {exception.Message}";
            stopRemaining = true;
        }

        return Fail(pullRequest, ids, attemptsUsed, error, stopRemaining: stopRemaining);
    }

    private CommentResult Fail(
        TrackedPullRequest pullRequest, IReadOnlyCollection<long> ids, int attemptsUsed, string error,
        bool terminal = false, bool stopRemaining = false)
    {
        DFLogger.LogOutput(DFLogLevel.ERROR, Category, $"PR #{pullRequest.Number}: comment {string.Join(", ", ids)}: {error}");
        if (terminal)
        {
            attempts.Record(pullRequest.Id, ids, "Failed", 0, error);
        }
        else
        {
            RecordUnsuccessfulAttempt(pullRequest, ids, attemptsUsed, error);
        }

        return new CommentResult(FixOutcome.Failed, error, stopRemaining);
    }

    private void RecordUnsuccessfulAttempt(
        TrackedPullRequest pullRequest, IReadOnlyCollection<long> ids, int attemptsUsed, string error) =>
        attempts.Record(pullRequest.Id, ids,
            FixDecision.StatusAfterUnsuccessfulAttempt(attemptsUsed + 1, options.MaxAttempts), 0, error);

    private sealed record CommentResult(FixOutcome Outcome, string? Error = null, bool StopRemaining = false);
}
