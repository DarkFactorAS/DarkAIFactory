using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.Repositories;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Review;

/// <summary>GitHub operations needed to carry out a human-requested merge.</summary>
public interface IMergeRequestGitHub
{
    Task<IReadOnlyList<StoredAgentPullRequest>> ListOpenPullRequestsAsync(
        IEnumerable<StoredAgentPullRequest> pullRequests, CancellationToken cancellationToken);

    Task<PullRequestMergeInfo> GetMergeInfoAsync(
        StoredAgentPullRequest pullRequest, string approvalPhrase, CancellationToken cancellationToken);

    Task MergeAsync(
        StoredAgentPullRequest pullRequest, string headSha, string mergeMethod, CancellationToken cancellationToken);
}

public enum MergeRequestOutcome
{
    /// <summary>Every open pull request was merged.</summary>
    Merged,

    /// <summary>Waiting for checks or for GitHub; the request stays and is retried in a later cycle.</summary>
    Pending,

    /// <summary>The request failed; <see cref="MergeRequestResult.Reason"/> says why.</summary>
    Failed
}

public sealed record MergeRequestResult(MergeRequestOutcome Outcome, string? Reason = null, bool Pushed = false);

/// <summary>
/// Carries out a merge requested from the dashboard: brings the head branch up to date with its base (resolving
/// conflicts through the runner), then merges the pull request once it is not a draft, GitHub reports
/// <c>clean</c> and every check is green. It never force-pushes and never bypasses branch protection. It does not
/// require AIAGENT_AUTO_MERGE and ignores the parent-branch and agent-code exclusions of the automatic merge.
/// </summary>
public sealed class MergeRequestHandler(
    ReviewOptions options,
    IMergeRequestGitHub github,
    IGitRunner gitRunner,
    IFixRunner mergeRunner)
{
    private const string Category = "Review";

    public async Task<MergeRequestResult> HandleAsync(
        string issueKey, IReadOnlyList<StoredAgentPullRequest> issuePullRequests, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredAgentPullRequest> open;
        try
        {
            open = await github.ListOpenPullRequestsAsync(issuePullRequests, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Pending($"{issueKey}: could not list the pull requests: {exception.Message}");
        }

        if (open.Count == 0)
        {
            return Failed("There is no open pull request to merge.");
        }

        var anyPending = false;
        var anyPushed = false;
        foreach (var pullRequest in open)
        {
            var result = await HandleOneAsync(issueKey, pullRequest, cancellationToken);
            anyPushed |= result.Pushed;
            if (result.Outcome == MergeRequestOutcome.Failed)
            {
                return result with { Pushed = anyPushed };
            }

            anyPending |= result.Outcome == MergeRequestOutcome.Pending;
        }

        return new MergeRequestResult(
            anyPending ? MergeRequestOutcome.Pending : MergeRequestOutcome.Merged, null, anyPushed);
    }

    private async Task<MergeRequestResult> HandleOneAsync(
        string issueKey, StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var prefix = $"PR #{pullRequest.PullRequest.PullRequestNumber} ({issueKey})";
        PullRequestMergeInfo info;
        try
        {
            info = await github.GetMergeInfoAsync(pullRequest, options.ApprovalPhrase, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Pending($"{prefix}: could not read the pull request state: {exception.Message}");
        }

        if (info.IsDraft)
        {
            return Failed($"{prefix} is a draft.");
        }

        if (info.BehindBy > 0)
        {
            return await UpdateBranchAsync(prefix, issueKey, pullRequest, info, cancellationToken);
        }

        if (info.ChecksProblem is not null)
        {
            return info.ChecksPending
                ? Pending($"{prefix}: waiting for checks: {info.ChecksProblem}.")
                : Failed($"{prefix}: checks are not green: {info.ChecksProblem}.");
        }

        if (string.Equals(info.MergeableState, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return Pending($"{prefix}: GitHub is still computing the mergeable state.");
        }

        if (!string.Equals(info.MergeableState, "clean", StringComparison.OrdinalIgnoreCase))
        {
            return Failed($"{prefix}: mergeable state is '{info.MergeableState}', not 'clean' (conflicts, required reviews or branch protection).");
        }

        try
        {
            await github.MergeAsync(pullRequest, info.HeadSha, options.MergeMethod, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed($"{prefix}: GitHub refused the merge: {exception.Message}");
        }

        Log($"{prefix} was merged on request ({options.MergeMethod}).");
        return new MergeRequestResult(MergeRequestOutcome.Merged);
    }

    /// <summary>Merges the base into the head and pushes (no force); a later cycle merges once checks finish.</summary>
    private async Task<MergeRequestResult> UpdateBranchAsync(
        string prefix, string issueKey, StoredAgentPullRequest pullRequest, PullRequestMergeInfo info,
        CancellationToken cancellationToken)
    {
        if (!ReviewGit.IsPushableBranch(info.HeadBranch))
        {
            return Failed($"{prefix}: refusing to update branch '{info.HeadBranch}'.");
        }

        if (!ReviewGit.IsSafeBranchName(info.BaseBranch))
        {
            return Failed($"{prefix}: the base branch name '{info.BaseBranch}' is not valid.");
        }

        var git = new ReviewGit(gitRunner);
        var workspace = Path.Combine(
            options.WorkspaceRoot ?? Runner.RunnerPolicy.WorkspaceRoot, Guid.NewGuid().ToString("N"), "checkout");
        var quarantine = false;
        try
        {
            await git.CloneAsync(new Uri(pullRequest.PullRequest.RepositoryUrl), info.HeadBranch, workspace, cancellationToken);
            var revision = await git.GetRevisionAsync(workspace, cancellationToken);
            if (!string.Equals(revision, info.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return Pending($"{prefix}: branch is at {revision}, expected {info.HeadSha}; retrying next cycle.");
            }

            await git.FetchBaseAsync(workspace, info.BaseBranch, cancellationToken);
            if (await git.ContainsBaseAsync(workspace, info.BaseBranch, cancellationToken))
            {
                // Up to date: nothing to push; checks decide in the next cycle.
                return Pending($"{prefix}: the head already contains '{info.BaseBranch}'; nothing to push.");
            }

            var conflicts = await git.MergeBaseAsync(workspace, info.BaseBranch, cancellationToken);
            if (conflicts.Count > 0)
            {
                Log($"{prefix}: merging '{info.BaseBranch}' conflicts in {conflicts.Count} file(s); asking the runner to resolve them.");
                var prompt = MergeConflictPromptBuilder.Build(issueKey, info.BaseBranch, info.HeadBranch, conflicts);
                var baseline = await SnapshotWorkspaceAsync(workspace, cancellationToken);
                try
                {
                    await mergeRunner.RunAsync(workspace, prompt, cancellationToken);
                }
                catch (TimeoutException exception)
                {
                    quarantine = true;
                    return Failed($"{prefix}: conflict resolution failed: {exception.Message}");
                }
                catch (Exception exception) when (exception is InvalidOperationException)
                {
                    return Failed($"{prefix}: conflict resolution or validation failed: {exception.Message}");
                }

                var remaining = FindConflictMarkers(workspace, conflicts);
                if (remaining.Count > 0)
                {
                    return Failed(
                        $"{prefix}: conflict markers remain in {string.Join(", ", remaining.Take(10))}; the conflict was not resolved.");
                }

                var outOfScope = FindChangesOutsideConflicts(
                    baseline, await SnapshotWorkspaceAsync(workspace, cancellationToken), conflicts);
                if (outOfScope.Count > 0)
                {
                    return Failed(
                        $"{prefix}: the runner changed files outside the conflicted set: {string.Join(", ", outOfScope.Take(10))}; nothing was pushed.");
                }
            }

            await git.CommitMergeAsync(
                workspace, $"{issueKey}: merge '{info.BaseBranch}' into '{info.HeadBranch}'", cancellationToken);
            try
            {
                await git.PushAsync(workspace, info.HeadBranch, info.HeadSha, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException)
            {
                return Failed($"{prefix}: push failed: {exception.Message}");
            }

            Log($"{prefix}: merged '{info.BaseBranch}' into the head and pushed; waiting for checks.");
            return new MergeRequestResult(MergeRequestOutcome.Pending, null, true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return Failed($"{prefix}: updating the branch failed: {exception.Message}");
        }
        finally
        {
            if (quarantine || cancellationToken.IsCancellationRequested)
            {
                Log($"{prefix}: quarantining checkout '{workspace}'.");
            }
            else
            {
                TryDelete(Path.GetDirectoryName(workspace)!);
                TryDelete(Builder.BuilderCapability.GitMetadataDirectoryFor(workspace));
            }
        }
    }

    /// <summary>
    /// Content hashes of every path git would stage with <c>add --all</c> (tracked plus untracked, not ignored).
    /// Missing tracked files map to an empty hash so deletions are detected.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> SnapshotWorkspaceAsync(
        string workspace, CancellationToken cancellationToken)
    {
        var listed = await gitRunner.RunAsync(
            workspace, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], false, cancellationToken);
        var root = Path.GetFullPath(workspace);
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (snapshot.ContainsKey(file))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(root, file));
            snapshot[file] = File.Exists(full)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(full, cancellationToken)))
                : string.Empty;
        }

        return snapshot;
    }

    /// <summary>Paths added, removed or modified between two snapshots that are not among the conflicted files.</summary>
    internal static IReadOnlyList<string> FindChangesOutsideConflicts(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after,
        IReadOnlyList<string> conflicts)
    {
        var allowed = new HashSet<string>(conflicts, StringComparer.Ordinal);
        return before.Keys.Concat(after.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(path => !allowed.Contains(path))
            .Where(path => !before.TryGetValue(path, out var old) || !after.TryGetValue(path, out var current) ||
                           !string.Equals(old, current, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Files (of the originally conflicted ones) that still contain conflict marker lines.</summary>
    internal static IReadOnlyList<string> FindConflictMarkers(string workspace, IReadOnlyList<string> files)
    {
        var root = Path.GetFullPath(workspace);
        var marked = new List<string>();
        foreach (var file in files)
        {
            var full = Path.GetFullPath(Path.Combine(root, file));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                marked.Add(file);
                continue;
            }

            if (!File.Exists(full))
            {
                continue;
            }

            var hasMarker = File.ReadLines(full).Any(line =>
                line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line == "<<<<<<<" ||
                line == "=======" ||
                line.StartsWith(">>>>>>> ", StringComparison.Ordinal) || line == ">>>>>>>");
            if (hasMarker)
            {
                marked.Add(file);
            }
        }

        return marked;
    }

    private MergeRequestResult Pending(string message)
    {
        Log(message);
        return new MergeRequestResult(MergeRequestOutcome.Pending, message);
    }

    private MergeRequestResult Failed(string reason) => new(MergeRequestOutcome.Failed, Redact(reason));

    private string Redact(string text)
    {
        if (!string.IsNullOrEmpty(options.RepositoryToken))
        {
            text = text.Replace(options.RepositoryToken, "***", StringComparison.Ordinal);
        }

        return text.Length > 1500 ? text[..1500] : text;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            AgentLog.LogOutput(DFLogLevel.WARNING, Category, $"Could not delete directory '{path}': {exception.Message}");
        }
    }

    private static void Log(string message) => AgentLog.LogOutput(DFLogLevel.INFO, Category, message);
}
