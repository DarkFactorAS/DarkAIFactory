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
    /// <summary>Paths of files modified, added or deleted in the working tree.</summary>
    Task<IReadOnlyList<string>> ChangedFilesAsync(string workspacePath, CancellationToken cancellationToken);
    Task DiscardChangesAsync(string workspacePath, CancellationToken cancellationToken);
    Task CommitAsync(string workspacePath, string message, CancellationToken cancellationToken);
    /// <summary>Pushes HEAD to the named branch on origin. Must never force.</summary>
    Task PushAsync(string workspacePath, string branch, CancellationToken cancellationToken);
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
        var allowed = new HashSet<string>(cited.Select(Normalize), StringComparer.Ordinal);
        return changed.Where(path => !allowed.Contains(Normalize(path))).ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
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
            return await FailAsync(pullRequest, comments.Select(c => c.Id).ToList(), 0,
                $"Refusing to push to branch '{headBranch}'.", terminal: true);
        }

        var used = attempts.GetAttempts(pullRequest.Id, comments.Select(c => c.Id).ToList());
        var eligible = comments.Where(c => used.GetValueOrDefault(c.Id) < options.MaxAttempts).ToList();
        if (eligible.Count == 0)
        {
            return new FixResult(FixOutcome.Skipped);
        }

        var ids = eligible.Select(c => c.Id).ToList();
        var attemptNumber = eligible.Max(c => used.GetValueOrDefault(c.Id)) + 1;
        // Persist the attempt before running so a crash still counts against the limit.
        attempts.Record(pullRequest.Id, ids, "InProgress", countAttempt: true, error: null);

        try
        {
            await runner.RunAsync(workspacePath, ReviewPromptBuilder.Build(eligible), cancellationToken);

            var changed = await git.ChangedFilesAsync(workspacePath, cancellationToken);
            if (changed.Count == 0)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Category, $"PR #{pullRequest.Number}: Copilot made no changes; nothing to push.");
                attempts.Record(pullRequest.Id, ids,
                    FixDecision.StatusAfterUnsuccessfulAttempt(attemptNumber, options.MaxAttempts),
                    countAttempt: false, error: "Copilot made no changes.");
                return new FixResult(FixOutcome.NoChanges);
            }

            var outOfBounds = FixDecision.OutOfBounds(changed, ReviewPromptBuilder.CitedPaths(eligible));
            if (outOfBounds.Count > 0)
            {
                await git.DiscardChangesAsync(workspacePath, cancellationToken);
                return await FailAsync(pullRequest, ids, attemptNumber,
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
                    await git.DiscardChangesAsync(workspacePath, cancellationToken);
                    return await FailAsync(pullRequest, ids, attemptNumber, $"'{command}' failed: {failure}");
                }
            }

            await git.CommitAsync(workspacePath,
                $"{pullRequest.JiraKey}: Address review comments ({ids.Count})", cancellationToken);
            await git.PushAsync(workspacePath, headBranch, cancellationToken);
            attempts.Record(pullRequest.Id, ids, "Resolved", countAttempt: false, error: null);
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
            return await FailAsync(pullRequest, ids, attemptNumber, exception.Message);
        }
    }

    private Task<FixResult> FailAsync(
        TrackedPullRequest pullRequest, IReadOnlyCollection<long> ids, int attemptNumber, string error,
        bool terminal = false)
    {
        DFLogger.LogOutput(DFLogLevel.ERROR, Category, $"PR #{pullRequest.Number}: {error}");
        var status = terminal ? "Failed" : FixDecision.StatusAfterUnsuccessfulAttempt(attemptNumber, options.MaxAttempts);
        attempts.Record(pullRequest.Id, ids, status, countAttempt: false, error);
        return Task.FromResult(new FixResult(FixOutcome.Failed, error));
    }
}
