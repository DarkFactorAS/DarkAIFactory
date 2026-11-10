using DFCommonLib.Logger;

namespace ReviewWorker;

internal enum FixOutcome { Skipped, NoChanges, Failed, Pushed }

internal sealed record FixResult(FixOutcome Outcome, string? Error = null);

internal interface IFixRunner
{
    Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken);
}

internal interface IReviewGit
{
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
    void Record(long pullRequestId, IReadOnlyCollection<long> commentIds, string status, bool countAttempt, string? error);
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
/// Runs one fix attempt for a pull request: prompt, Copilot, bounds check, build/test, commit, push.
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
            return await FailAsync(pullRequest, comments.Select(c => c.Id).ToList(), new Dictionary<long, int>(),
                $"Refusing to push to branch '{headBranch}'.", terminal: true);
        }

        var used = attempts.GetAttempts(pullRequest.Id, comments.Select(c => c.Id).ToList());
        var eligible = comments.Where(c => used.GetValueOrDefault(c.Id) < options.MaxAttempts).ToList();
        if (eligible.Count == 0)
        {
            return new FixResult(FixOutcome.Skipped);
        }

        var promptBatch = ReviewPromptBuilder.BuildBatch(eligible);
        eligible = promptBatch.Comments;
        var ids = eligible.Select(c => c.Id).ToList();
        var baselineRevision = await git.GetRevisionAsync(workspacePath, cancellationToken);
        // Persist the attempt before running so a crash still counts against the limit.
        attempts.Record(pullRequest.Id, ids, "InProgress", countAttempt: true, error: null);

        try
        {
            await runner.RunAsync(workspacePath, promptBatch.Prompt, cancellationToken);

            var changed = await git.ChangedFilesAsync(workspacePath, baselineRevision, cancellationToken);
            if (changed.Count == 0)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Category, $"PR #{pullRequest.Number}: Copilot made no changes; nothing to push.");
                RecordUnsuccessfulAttempts(pullRequest, ids, used, "Copilot made no changes.");
                return new FixResult(FixOutcome.NoChanges);
            }

            var outOfBounds = FixDecision.OutOfBounds(changed, ReviewPromptBuilder.CitedPaths(eligible));
            if (outOfBounds.Count > 0)
            {
                await git.DiscardChangesAsync(workspacePath, baselineRevision, cancellationToken);
                return await FailAsync(pullRequest, ids, used,
                    $"Changes outside the cited files were discarded: {string.Join(", ", outOfBounds)}.");
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
                $"{pullRequest.JiraKey}: Address review comments ({ids.Count})", cancellationToken);
            await git.PushAsync(workspacePath, pullRequest.RepositoryUrl, headBranch, baselineRevision, cancellationToken);
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
                attempts.Record(pullRequest.Id, resolvedIds, "Resolved", countAttempt: false, error: null);
            }

            if (pendingIds.Count > 0)
            {
                attempts.Record(pullRequest.Id, pendingIds, "Pending", countAttempt: false,
                    "No changes were made to the cited files.");
            }

            DFLogger.LogOutput(DFLogLevel.INFO, Category, $"PR #{pullRequest.Number}: pushed fixes to '{headBranch}'.");
            return new FixResult(FixOutcome.Pushed);
        }
        catch (OperationCanceledException)
        {
            attempts.Record(pullRequest.Id, ids, "Pending", countAttempt: false, error: "Cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            return await FailAsync(pullRequest, ids, used, exception.Message);
        }
    }

    private Task<FixResult> FailAsync(
        TrackedPullRequest pullRequest, IReadOnlyCollection<long> ids, IReadOnlyDictionary<long, int> attemptsUsed, string error,
        bool terminal = false)
    {
        DFLogger.LogOutput(DFLogLevel.ERROR, Category, $"PR #{pullRequest.Number}: {error}");
        if (terminal)
        {
            attempts.Record(pullRequest.Id, ids, "Failed", countAttempt: false, error);
        }
        else
        {
            RecordUnsuccessfulAttempts(pullRequest, ids, attemptsUsed, error);
        }

        return Task.FromResult(new FixResult(FixOutcome.Failed, error));
    }

    private void RecordUnsuccessfulAttempts(
        TrackedPullRequest pullRequest,
        IReadOnlyCollection<long> ids,
        IReadOnlyDictionary<long, int> attemptsUsed,
        string error)
    {
        foreach (var group in ids.GroupBy(id =>
                     FixDecision.StatusAfterUnsuccessfulAttempt(attemptsUsed.GetValueOrDefault(id) + 1, options.MaxAttempts)))
        {
            attempts.Record(pullRequest.Id, group.ToList(), group.Key, countAttempt: false, error);
        }
    }
}
