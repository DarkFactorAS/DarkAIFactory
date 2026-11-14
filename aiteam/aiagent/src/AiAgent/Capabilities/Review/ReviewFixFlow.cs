using System.Text;
using AiAgent.Repositories;
using AiAgent.Runner;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Review;

/// <summary>Decisions of the fix flow that do not need I/O.</summary>
public static class FixDecision
{
    /// <summary>Status after an attempt that did not push: Pending to retry, Failed once attempts are used up.</summary>
    public static string StatusAfterUnsuccessfulAttempt(int attemptsUsed, int maxAttempts) =>
        attemptsUsed >= maxAttempts ? "Failed" : "Pending";

    /// <summary>Changed files outside the cited set.</summary>
    public static IReadOnlyList<string> OutOfBounds(IEnumerable<string> changed, IEnumerable<string> cited)
    {
        var allowed = new HashSet<string>(cited, StringComparer.Ordinal);
        return changed.Where(path =>
            path.StartsWith("UserSettings/", StringComparison.Ordinal) || !allowed.Contains(path)).ToList();
    }
}

/// <summary>
/// Fixes a pull request's review comments one at a time. For each comment: prompt with only that comment, the
/// runner, bounds check (only the comment's file), build/test, then a separate commit and push whose message
/// says what was fixed and where. Pushes only to the PR head branch, never forced, and never when nothing changed.
/// After the run, review threads whose comments were all pushed are resolved on GitHub.
/// </summary>
public sealed class ReviewFixFlow(
    long agentId,
    IFixRunner runner,
    ReviewGit git,
    IProjectCommandRunner commands,
    IReviewAttemptStore attempts,
    IReviewThreadResolver resolver,
    int maxAttempts,
    string? buildCommand,
    string? testCommand)
{
    private const string Category = "ReviewFix";

    public async Task<FixResult> RunAsync(
        StoredAgentPullRequest pullRequest,
        string headBranch,
        IReadOnlyList<ReviewComment> comments,
        string workspacePath,
        CancellationToken cancellationToken,
        IReadOnlyList<ReviewThreadMember>? threadMembers = null)
    {
        var number = pullRequest.PullRequest.PullRequestNumber;
        if (!ReviewGit.IsPushableBranch(headBranch))
        {
            var error = $"Refusing to push to branch '{headBranch}'.";
            DFLogger.LogOutput(DFLogLevel.ERROR, Category, $"PR #{number}: {error}");
            attempts.Record(agentId, pullRequest.Id, comments.Select(c => c.Id).ToList(), "Failed",
                countAttempt: false, error);
            return new FixResult(FixOutcome.Failed, error, FailedCount: comments.Count);
        }

        var used = attempts.GetAttempts(pullRequest.Id, comments.Select(c => c.Id).ToList());
        var eligible = comments.Where(c => used.GetValueOrDefault(c.Id) < maxAttempts).ToList();
        if (eligible.Count == 0)
        {
            return new FixResult(FixOutcome.Skipped);
        }

        var pushed = 0;
        var failed = 0;
        var quarantineWorkspace = false;
        string? lastError = null;
        var pushedIds = new HashSet<long>();
        try
        {
            for (var index = 0; index < eligible.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var comment = eligible[index];
                DFLogger.LogOutput(DFLogLevel.INFO, Category,
                    $"PR #{number}: fixing comment {comment.Id} ({index + 1} of {eligible.Count}) " +
                    $"at {comment.Path}:{comment.Line?.ToString() ?? "-"}.");
                var result = await FixCommentAsync(
                    pullRequest, headBranch, comment, used.GetValueOrDefault(comment.Id), workspacePath, cancellationToken);
                switch (result.Outcome)
                {
                    case FixOutcome.Pushed:
                        pushed++;
                        pushedIds.Add(comment.Id);
                        break;
                    case FixOutcome.Failed:
                        failed++;
                        lastError = result.Error;
                        break;
                }
                quarantineWorkspace |= result.QuarantineWorkspace;

                if (result.StopRemaining)
                {
                    DFLogger.LogOutput(DFLogLevel.WARNING, Category,
                        $"PR #{number}: stopping; {eligible.Count - index - 1} comment(s) left for the next cycle.");
                    break;
                }
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await ResolveCompletedThreadsAsync(
                    pullRequest, comments, pushedIds, used, threadMembers, cancellationToken);
            }
        }

        var outcome = pushed > 0 ? FixOutcome.Pushed : failed > 0 ? FixOutcome.Failed : FixOutcome.NoChanges;
        return new FixResult(outcome, lastError, pushed, failed, quarantineWorkspace);
    }

    private async Task<CommentResult> FixCommentAsync(
        StoredAgentPullRequest pullRequest,
        string headBranch,
        ReviewComment comment,
        int attemptsUsed,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var number = pullRequest.PullRequest.PullRequestNumber;
        long[] ids = [comment.Id];
        var prompt = ReviewPromptBuilder.Build([comment]);
        if (Encoding.UTF8.GetByteCount(prompt) > ReviewPromptBuilder.MaxPromptLengthBytes)
        {
            return Fail(pullRequest, ids, attemptsUsed, "The review comment exceeds the maximum prompt size.", terminal: true);
        }

        var baselineRevision = await git.GetRevisionAsync(workspacePath, cancellationToken);
        // Mark the comment in progress; the attempt is counted only once the runner has accepted the job,
        // so capacity rejections (RunnerBusyException) never use up a comment's attempt limit.
        attempts.Record(agentId, pullRequest.Id, ids, "InProgress", countAttempt: false, error: null);
        var committed = false;
        try
        {
            try
            {
                await runner.RunAsync(workspacePath, prompt, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Runner cancelled by agent shutdown.", exception, cancellationToken);
                }

                attempts.Record(agentId, pullRequest.Id, ids, "InProgress", countAttempt: true, error: null);
                return Fail(
                    pullRequest, ids, attemptsUsed, exception.Message, stopRemaining: true, quarantineWorkspace: true);
            }
            catch (RunnerBusyException)
            {
                attempts.Record(agentId, pullRequest.Id, ids, "Pending", countAttempt: false,
                    error: "The runner is busy; retrying in the next cycle.");
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Other runner failures are real, counted attempts.
                attempts.Record(agentId, pullRequest.Id, ids, "InProgress", countAttempt: true, error: null);
                throw;
            }

            attempts.Record(agentId, pullRequest.Id, ids, "InProgress", countAttempt: true, error: null);

            var cited = ReviewPromptBuilder.CitedPaths([comment]);
            var changed = await git.ChangedFilesAsync(workspacePath, baselineRevision, cancellationToken);
            if (changed.Count == 0)
            {
                return NoChanges(pullRequest, comment, "the runner made no changes");
            }

            var outOfBounds = FixDecision.OutOfBounds(changed, cited);
            if (outOfBounds.Count > 0)
            {
                return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                    $"Changes outside the cited file were discarded: {string.Join(", ", outOfBounds)}.", cancellationToken);
            }

            foreach (var command in new[] { buildCommand, testCommand })
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
            if (changed.Count == 0)
            {
                return NoChanges(pullRequest, comment, "no changes remained after build and test");
            }

            outOfBounds = FixDecision.OutOfBounds(changed, cited);
            if (outOfBounds.Count > 0)
            {
                return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                    $"Changes outside the cited file were discarded: {string.Join(", ", outOfBounds)}.", cancellationToken);
            }

            await git.CommitAsync(workspacePath,
                ReviewCommitMessage.Build(pullRequest.PullRequest.JiraKey, comment), cancellationToken);
            committed = true;
            await git.PushAsync(workspacePath, headBranch, baselineRevision, cancellationToken);
            attempts.Record(agentId, pullRequest.Id, ids, "Resolved", countAttempt: false, error: null);
            DFLogger.LogOutput(DFLogLevel.INFO, Category,
                $"PR #{number}: pushed fix for comment {comment.Id} to '{headBranch}'.");
        }
        catch (RunnerBusyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Agent shutdown: the attempt was not counted, or only once the runner had finished; leave it pending.
            attempts.Record(agentId, pullRequest.Id, ids, "Pending", countAttempt: false, error: "Cancelled by agent shutdown.");
            throw;
        }
        catch (Exception exception)
        {
            // A failed push leaves a local commit; stop so later comments do not push it.
            return await DiscardAndFailAsync(pullRequest, ids, attemptsUsed, workspacePath, baselineRevision,
                exception.Message, cancellationToken, stopRemaining: committed);
        }

        return new CommentResult(FixOutcome.Pushed);
    }

    /// <summary>Nothing to fix is not an error: the comment is Resolved (not retried) and its thread stays open.</summary>
    private CommentResult NoChanges(StoredAgentPullRequest pullRequest, ReviewComment comment, string reason)
    {
        DFLogger.LogOutput(DFLogLevel.INFO, Category,
            $"PR #{pullRequest.PullRequest.PullRequestNumber}: comment {comment.Id}: {reason}; nothing to push. " +
            "The review thread is left open for a human.");
        attempts.Record(agentId, pullRequest.Id, [comment.Id], "Resolved", countAttempt: false, error: null);
        return new CommentResult(FixOutcome.NoChanges);
    }

    /// <summary>
    /// Resolves each review thread whose outstanding comments were all pushed in this run. A thread with any
    /// sibling comment that failed, made no changes, was skipped for exhausted attempts, or was not reached
    /// stays open. A comment that made no changes is marked Resolved in the database (it is not retried), but
    /// its thread is deliberately left open on GitHub for a human.
    /// A thread containing a retried comment also stays open: an earlier cycle may have handled a sibling (for
    /// example one that made no changes) that later polls no longer return, so this run cannot see it.
    /// </summary>
    private async Task ResolveCompletedThreadsAsync(
        StoredAgentPullRequest pullRequest,
        IReadOnlyList<ReviewComment> comments,
        IReadOnlySet<long> pushedIds,
        IReadOnlyDictionary<long, int> priorAttempts,
        IReadOnlyList<ReviewThreadMember>? threadMembers,
        CancellationToken cancellationToken)
    {
        if (pushedIds.Count == 0)
        {
            return;
        }

        var number = pullRequest.PullRequest.PullRequestNumber;
        var threads = threadMembers is null
            ? comments
                .Where(c => !string.IsNullOrEmpty(c.ThreadId))
                .GroupBy(c => c.ThreadId, StringComparer.Ordinal)
                .Select(thread => (
                    ThreadId: thread.Key,
                    Members: thread.Select(c => (c.Id, Status: "Actionable")).ToList()))
            : threadMembers
                .Where(c => !string.IsNullOrEmpty(c.ThreadId))
                .GroupBy(c => c.ThreadId, StringComparer.Ordinal)
                .Select(thread => (
                    ThreadId: thread.Key,
                    Members: thread.Select(c => (c.Id, c.Status)).ToList()));
        foreach (var thread in threads)
        {
            if (!thread.Members.Any(c => pushedIds.Contains(c.Id)))
            {
                continue;
            }

            var unfinished = thread.Members
                .Where(c => c.Status != "Actionable" || !pushedIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToList();
            if (unfinished.Count > 0)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Category,
                    $"PR #{number}: leaving review thread open; comment(s) {string.Join(", ", unfinished)} not fixed yet.");
                continue;
            }

            var retried = thread.Members
                .Where(c => priorAttempts.GetValueOrDefault(c.Id) > 0)
                .Select(c => c.Id)
                .ToList();
            if (retried.Count > 0)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Category,
                    $"PR #{number}: leaving review thread open for a human; comment(s) " +
                    $"{string.Join(", ", retried)} were retried and earlier siblings may not have been fixed.");
                continue;
            }

            // A failure to resolve must not turn a pushed fix into a failure.
            await ResolveThreadAsync(
                pullRequest, thread.ThreadId, thread.Members.Select(c => c.Id).ToList(), cancellationToken);
        }
    }

    private async Task ResolveThreadAsync(
        StoredAgentPullRequest pullRequest, string threadId, IReadOnlyList<long> commentIds, CancellationToken cancellationToken)
    {
        var number = pullRequest.PullRequest.PullRequestNumber;
        var ids = string.Join(", ", commentIds);
        try
        {
            await resolver.ResolveThreadAsync(pullRequest, threadId, cancellationToken);
            DFLogger.LogOutput(DFLogLevel.INFO, Category, $"PR #{number}: resolved review thread for comment(s) {ids}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, Category,
                $"PR #{number}: could not resolve review thread for comment(s) {ids}: {exception.Message}");
        }
    }

    private async Task<CommentResult> DiscardAndFailAsync(
        StoredAgentPullRequest pullRequest,
        IReadOnlyCollection<long> ids,
        int attemptsUsed,
        string workspacePath,
        string baselineRevision,
        string error,
        CancellationToken cancellationToken,
        bool stopRemaining = false,
        bool quarantineWorkspace = false)
    {
        try
        {
            await git.DiscardChangesAsync(workspacePath, baselineRevision, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            attempts.Record(agentId, pullRequest.Id, ids, "Pending", countAttempt: false, error: "Cancelled by agent shutdown.");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The checkout is in an unknown state; do not build later fixes on it.
            error = $"{error} Could not reset the checkout: {exception.Message}";
            stopRemaining = true;
        }

        return Fail(
            pullRequest, ids, attemptsUsed, error, stopRemaining: stopRemaining, quarantineWorkspace: quarantineWorkspace);
    }

    private CommentResult Fail(
        StoredAgentPullRequest pullRequest, IReadOnlyCollection<long> ids, int attemptsUsed, string error,
        bool terminal = false, bool stopRemaining = false, bool quarantineWorkspace = false)
    {
        DFLogger.LogOutput(DFLogLevel.ERROR, Category,
            $"PR #{pullRequest.PullRequest.PullRequestNumber}: comment {string.Join(", ", ids)}: {error}");
        var status = terminal ? "Failed" : FixDecision.StatusAfterUnsuccessfulAttempt(attemptsUsed + 1, maxAttempts);
        attempts.Record(agentId, pullRequest.Id, ids, status, countAttempt: false, error);
        return new CommentResult(FixOutcome.Failed, error, stopRemaining, quarantineWorkspace);
    }

    private sealed record CommentResult(
        FixOutcome Outcome, string? Error = null, bool StopRemaining = false, bool QuarantineWorkspace = false);
}
