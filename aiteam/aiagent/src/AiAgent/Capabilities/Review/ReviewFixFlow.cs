using AiAgent.Repositories;
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
/// Runs one fix attempt for a pull request: prompt, the runner, bounds check, build/test, commit, push.
/// Pushes only to the PR head branch, never forced, and never when nothing changed.
/// </summary>
public sealed class ReviewFixFlow(
    long agentId,
    IFixRunner runner,
    ReviewGit git,
    IProjectCommandRunner commands,
    IReviewAttemptStore attempts,
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
        CancellationToken cancellationToken)
    {
        if (!ReviewGit.IsPushableBranch(headBranch))
        {
            return await FailAsync(pullRequest, comments.Select(c => c.Id).ToList(), new Dictionary<long, int>(),
                $"Refusing to push to branch '{headBranch}'.", terminal: true);
        }

        var used = attempts.GetAttempts(pullRequest.Id, comments.Select(c => c.Id).ToList());
        var eligible = comments.Where(c => used.GetValueOrDefault(c.Id) < maxAttempts).ToList();
        if (eligible.Count == 0)
        {
            return new FixResult(FixOutcome.Skipped);
        }

        var promptBatch = ReviewPromptBuilder.BuildBatch(eligible);
        eligible = promptBatch.Comments.ToList();
        var ids = eligible.Select(c => c.Id).ToList();
        var baselineRevision = await git.GetRevisionAsync(workspacePath, cancellationToken);
        // Persist the attempt before running so a crash still counts against the limit.
        attempts.Record(agentId, pullRequest.Id, ids, "InProgress", countAttempt: true, error: null);

        try
        {
            await runner.RunAsync(workspacePath, promptBatch.Prompt, cancellationToken);

            var changed = await git.ChangedFilesAsync(workspacePath, baselineRevision, cancellationToken);
            if (changed.Count == 0)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Category, $"PR #{pullRequest.PullRequest.PullRequestNumber}: the runner made no changes; nothing to push.");
                RecordUnsuccessfulAttempts(pullRequest, ids, used, "the runner made no changes.");
                return new FixResult(FixOutcome.NoChanges);
            }

            var outOfBounds = FixDecision.OutOfBounds(changed, ReviewPromptBuilder.CitedPaths(eligible));
            if (outOfBounds.Count > 0)
            {
                await git.DiscardChangesAsync(workspacePath, baselineRevision, cancellationToken);
                return await FailAsync(pullRequest, ids, used,
                    $"Changes outside the cited files were discarded: {string.Join(", ", outOfBounds)}.");
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
                    await git.DiscardChangesAsync(workspacePath, baselineRevision, cancellationToken);
                    return await FailAsync(pullRequest, ids, used, $"'{command}' failed: {failure}");
                }
            }

            changed = await git.ChangedFilesAsync(workspacePath, baselineRevision, cancellationToken);
            outOfBounds = FixDecision.OutOfBounds(changed, ReviewPromptBuilder.CitedPaths(eligible));
            if (outOfBounds.Count > 0)
            {
                await git.DiscardChangesAsync(workspacePath, baselineRevision, cancellationToken);
                return await FailAsync(pullRequest, ids, used,
                    $"Changes outside the cited files were discarded: {string.Join(", ", outOfBounds)}.");
            }

            await git.CommitAsync(workspacePath,
                $"{pullRequest.PullRequest.JiraKey}: Address review comments ({ids.Count})", cancellationToken);
            await git.PushAsync(workspacePath, headBranch, baselineRevision, cancellationToken);
            var changedPaths = new HashSet<string>(changed, StringComparer.Ordinal);
            var commentsPerPath = eligible.GroupBy(comment => comment.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var resolvedIds = eligible
                .Where(comment => changedPaths.Contains(comment.Path) && commentsPerPath[comment.Path] == 1)
                .Select(comment => comment.Id)
                .ToList();
            var resolvedSet = new HashSet<long>(resolvedIds);
            var pendingIds = eligible.Where(comment => !resolvedSet.Contains(comment.Id)).Select(comment => comment.Id).ToList();
            if (resolvedIds.Count > 0)
            {
                attempts.Record(agentId, pullRequest.Id, resolvedIds, "Resolved", countAttempt: false, error: null);
            }

            if (pendingIds.Count > 0)
            {
                attempts.Record(agentId, pullRequest.Id, pendingIds, "Pending", countAttempt: false,
                    "No changes were made to the cited files.");
            }

            DFLogger.LogOutput(DFLogLevel.INFO, Category, $"PR #{pullRequest.PullRequest.PullRequestNumber}: pushed fixes to '{headBranch}'.");
            return new FixResult(FixOutcome.Pushed);
        }
        catch (OperationCanceledException)
        {
            attempts.Record(agentId, pullRequest.Id, ids, "Pending", countAttempt: false, error: "Cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            return await FailAsync(pullRequest, ids, used, exception.Message);
        }
    }

    private Task<FixResult> FailAsync(
        StoredAgentPullRequest pullRequest, IReadOnlyCollection<long> ids, IReadOnlyDictionary<long, int> attemptsUsed, string error,
        bool terminal = false)
    {
        DFLogger.LogOutput(DFLogLevel.ERROR, Category, $"PR #{pullRequest.PullRequest.PullRequestNumber}: {error}");
        if (terminal)
        {
            attempts.Record(agentId, pullRequest.Id, ids, "Failed", countAttempt: false, error);
        }
        else
        {
            RecordUnsuccessfulAttempts(pullRequest, ids, attemptsUsed, error);
        }

        return Task.FromResult(new FixResult(FixOutcome.Failed, error));
    }

    private void RecordUnsuccessfulAttempts(
        StoredAgentPullRequest pullRequest,
        IReadOnlyCollection<long> ids,
        IReadOnlyDictionary<long, int> attemptsUsed,
        string error)
    {
        foreach (var group in ids.GroupBy(id =>
                     FixDecision.StatusAfterUnsuccessfulAttempt(attemptsUsed.GetValueOrDefault(id) + 1, maxAttempts)))
        {
            attempts.Record(agentId, pullRequest.Id, group.ToList(), group.Key, countAttempt: false, error);
        }
    }
}
