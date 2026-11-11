using System.Text.RegularExpressions;
using AiTeam.Configuration;
using ClaudeCodingAgent.Repository;
using DFCommonLib.Logger;

namespace ClaudeCodingAgent;

/// <summary>GitHub operations the sanity check needs; abstracted so the logic can be tested without GitHub.</summary>
internal interface IPullRequestSanityGitHub
{
    Task<GitHubRepository.PullRequestInfo> GetPullRequestAsync(long number, CancellationToken cancellationToken);
    Task<bool> BranchExistsAsync(string branch, CancellationToken cancellationToken);
    Task CreateBranchFromDefaultBranchAsync(string branch, CancellationToken cancellationToken);
    Task<GitHubRepository.PullRequestInfo?> FindPullRequestAsync(string head, string baseBranch, CancellationToken cancellationToken);
    Task<string> CreatePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken cancellationToken);
    Task ClosePullRequestWithCommentAsync(long number, string comment, CancellationToken cancellationToken);
    Task AddPullRequestCommentAsync(long number, string comment, CancellationToken cancellationToken);

    /// <summary>Pushes a revert of a merged pull request on <paramref name="branch"/> and opens the revert pull request.</summary>
    Task<string> CreateRevertPullRequestAsync(long number, string branch, CancellationToken cancellationToken);

    /// <summary>Pushes <paramref name="branch"/> with the pull request's commits applied on top of <paramref name="targetBranch"/>.</summary>
    Task PushCherryPickBranchAsync(long number, string targetBranch, string branch, CancellationToken cancellationToken);
}

internal sealed class GitHubSanityOperations(AgentConfiguration configuration) : IPullRequestSanityGitHub
{
    public Task<GitHubRepository.PullRequestInfo> GetPullRequestAsync(long number, CancellationToken cancellationToken) =>
        GitHubRepository.GetPullRequestInfoAsync(configuration, number, cancellationToken);

    public Task<bool> BranchExistsAsync(string branch, CancellationToken cancellationToken) =>
        GitHubRepository.BranchExistsAsync(configuration, branch, cancellationToken);

    public Task CreateBranchFromDefaultBranchAsync(string branch, CancellationToken cancellationToken) =>
        GitHubRepository.CreateBranchFromDefaultBranchAsync(configuration, branch, cancellationToken);

    public Task<GitHubRepository.PullRequestInfo?> FindPullRequestAsync(
        string head, string baseBranch, CancellationToken cancellationToken) =>
        GitHubRepository.FindPullRequestAsync(configuration, head, baseBranch, cancellationToken);

    public Task<string> CreatePullRequestAsync(
        string head, string baseBranch, string title, string body, CancellationToken cancellationToken) =>
        GitHubRepository.CreatePullRequestWithBaseAsync(configuration, head, baseBranch, title, body, cancellationToken);

    public Task ClosePullRequestWithCommentAsync(long number, string comment, CancellationToken cancellationToken) =>
        GitHubRepository.ClosePullRequestWithCommentAsync(configuration, number, comment, cancellationToken);

    public Task AddPullRequestCommentAsync(long number, string comment, CancellationToken cancellationToken) =>
        GitHubRepository.AddPullRequestCommentAsync(configuration, number, comment, cancellationToken);

    public Task<string> CreateRevertPullRequestAsync(long number, string branch, CancellationToken cancellationToken) =>
        WithWorkspaceAsync(workspace => GitHubRepository.CreateRevertPullRequestAsync(
            configuration, workspace, number, branch, cancellationToken), cancellationToken);

    public Task PushCherryPickBranchAsync(
        long number, string targetBranch, string branch, CancellationToken cancellationToken) =>
        WithWorkspaceAsync(async workspace =>
        {
            await GitHubRepository.CherryPickPullRequestToNewBranchAsync(
                configuration, workspace, number, targetBranch, branch, cancellationToken);
            return branch;
        }, cancellationToken);

    private async Task<string> WithWorkspaceAsync(
        Func<string, Task<string>> action, CancellationToken cancellationToken)
    {
        var workspace = GitHubRepository.CreateWorkspacePath();
        try
        {
            await GitHubRepository.CloneRepositoryAsync(configuration, workspace, cancellationToken);
            return await action(workspace);
        }
        finally
        {
            GitHubRepository.DeleteWorkspace(workspace);
        }
    }
}

internal sealed record SanityCheckAction(string JiraKey, long PullRequestNumber, string Kind, string NewPullRequestUrl);

/// <summary>
/// Verifies that tracked sub-task pull requests target the parent task's integration branch
/// (<c>ai-agent/&lt;PARENT-KEY&gt;</c>) and repairs the ones that do not. Pull requests of issues that are not
/// sub-tasks are never touched. Every step is idempotent, so an interrupted run can simply be repeated.
/// </summary>
internal sealed class PullRequestSanityCheck(
    AgentConfiguration configuration,
    PullRequestRepository pullRequests,
    Func<string, CancellationToken, Task<JiraIssue>> getIssue,
    IPullRequestSanityGitHub gitHub)
{
    public const string RepairedByOpenPullRequest = "reopened";
    public const string RepairedByMergedPullRequest = "rolled-back";
    public const string RemovedClosedPullRequest = "removed";
    public const string WrongBranchComment = "subtask was created from the wrong branch";

    public async Task<IReadOnlyList<SanityCheckAction>> RunAsync(CancellationToken cancellationToken)
    {
        var actions = new List<SanityCheckAction>();
        var issues = new Dictionary<string, JiraIssue?>(StringComparer.OrdinalIgnoreCase);
        foreach (var tracked in pullRequests.ListTracked(configuration.GitHubRepositoryUrl))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!issues.TryGetValue(tracked.JiraKey, out var issue))
                {
                    issue = await getIssue(tracked.JiraKey, cancellationToken);
                    issues[tracked.JiraKey] = issue;
                }

                if (issue is null || !issue.IsSubTask || string.IsNullOrWhiteSpace(issue.ParentKey))
                {
                    continue;
                }

                var action = await CheckAsync(tracked, JiraWorkflowRules.ParentBranchName(issue.ParentKey), cancellationToken);
                if (action is not null)
                {
                    actions.Add(action);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    "SanityCheck",
                    $"Could not check pull request {tracked.Url} of {tracked.JiraKey}: {exception.Message}");
            }
        }

        return actions;
    }

    private async Task<SanityCheckAction?> CheckAsync(
        TrackedPullRequest tracked, string parentBranch, CancellationToken cancellationToken)
    {
        var info = await gitHub.GetPullRequestAsync(tracked.Number, cancellationToken);
        if (string.Equals(info.BaseBranch, parentBranch, StringComparison.Ordinal))
        {
            return null;
        }

        if (info.State == GitHubRepository.PullRequestState.Closed)
        {
            return await ReconcileClosedAsync(tracked, info, parentBranch, cancellationToken);
        }

        await EnsureParentBranchAsync(parentBranch, cancellationToken);
        return info.State == GitHubRepository.PullRequestState.Open
            ? await RepairOpenAsync(tracked, info, parentBranch, cancellationToken)
            : await RepairMergedAsync(tracked, info, parentBranch, cancellationToken);
    }

    // A previous run may have closed the original before the tracking update; move tracking to the replacement.
    // Without a live replacement the pull request was closed unmerged: comment once and stop tracking it.
    private async Task<SanityCheckAction?> ReconcileClosedAsync(
        TrackedPullRequest tracked,
        GitHubRepository.PullRequestInfo info,
        string parentBranch,
        CancellationToken cancellationToken)
    {
        var replacement =
            await gitHub.FindPullRequestAsync(info.HeadBranch, parentBranch, cancellationToken) ??
            await gitHub.FindPullRequestAsync($"ai-agent/cherry-pick-pr-{info.Number}", parentBranch, cancellationToken);
        if (replacement is null || replacement.State == GitHubRepository.PullRequestState.Closed)
        {
            // Comment first, then delete the row: the row is the only record that the cleanup is still pending,
            // and once it is gone the comment is never posted again.
            await gitHub.AddPullRequestCommentAsync(info.Number, WrongBranchComment, cancellationToken);
            pullRequests.DeleteByNumber(configuration.GitHubRepositoryUrl, info.Number);
            DFLogger.LogOutput(DFLogLevel.INFO, "SanityCheck", $"Removed closed unmerged pull request #{info.Number} of {tracked.JiraKey}.");
            return new SanityCheckAction(tracked.JiraKey, info.Number, RemovedClosedPullRequest, string.Empty);
        }

        pullRequests.Replace(configuration.GitHubRepositoryUrl, info.Number, replacement.Url);
        DFLogger.LogOutput(DFLogLevel.INFO, "SanityCheck", $"Recovered {tracked.JiraKey}: tracking moved from #{info.Number} to {replacement.Url}.");
        return new SanityCheckAction(tracked.JiraKey, info.Number, RepairedByOpenPullRequest, replacement.Url);
    }

    private async Task EnsureParentBranchAsync(string parentBranch, CancellationToken cancellationToken)
    {
        if (!await gitHub.BranchExistsAsync(parentBranch, cancellationToken))
        {
            await gitHub.CreateBranchFromDefaultBranchAsync(parentBranch, cancellationToken);
        }
    }

    // Open PR with a wrong base: open the replacement first, then close the old one, then update the table.
    private async Task<SanityCheckAction> RepairOpenAsync(
        TrackedPullRequest tracked,
        GitHubRepository.PullRequestInfo info,
        string parentBranch,
        CancellationToken cancellationToken)
    {
        var replacementUrl = await EnsurePullRequestAsync(
            info.HeadBranch,
            parentBranch,
            info.Title,
            $"Replaces #{info.Number}, which targeted `{info.BaseBranch}` instead of `{parentBranch}`.\n\nCreated automatically.",
            requireOpen: true,
            cancellationToken);
        await gitHub.ClosePullRequestWithCommentAsync(
            info.Number,
            $"Closing: this sub-task pull request must target `{parentBranch}` but targeted `{info.BaseBranch}`. " +
            $"Continued in {replacementUrl}.",
            cancellationToken);
        pullRequests.Replace(configuration.GitHubRepositoryUrl, info.Number, replacementUrl);
        DFLogger.LogOutput(DFLogLevel.INFO, "SanityCheck", $"Moved {tracked.JiraKey} pull request #{info.Number} to {replacementUrl}.");
        return new SanityCheckAction(tracked.JiraKey, info.Number, RepairedByOpenPullRequest, replacementUrl);
    }

    // Merged PR with a wrong base: roll it back where it landed, then re-apply it on the parent branch.
    private async Task<SanityCheckAction> RepairMergedAsync(
        TrackedPullRequest tracked,
        GitHubRepository.PullRequestInfo info,
        string parentBranch,
        CancellationToken cancellationToken)
    {
        var rollbackBranch = $"ai-agent/rollback-pr-{info.Number}";
        if (await gitHub.FindPullRequestAsync(rollbackBranch, info.BaseBranch, cancellationToken) is null)
        {
            if (await gitHub.BranchExistsAsync(rollbackBranch, cancellationToken))
            {
                // A previous run pushed the revert but stopped before opening the pull request.
                await gitHub.CreatePullRequestAsync(
                    rollbackBranch,
                    info.BaseBranch,
                    $"Revert \"{OneLine(info.Title)}\"",
                    $"Reverts pull request #{info.Number}.\n\nCreated automatically.",
                    cancellationToken);
            }
            else
            {
                await gitHub.CreateRevertPullRequestAsync(info.Number, rollbackBranch, cancellationToken);
            }
        }

        var cherryPickBranch = $"ai-agent/cherry-pick-pr-{info.Number}";
        if (!await gitHub.BranchExistsAsync(cherryPickBranch, cancellationToken))
        {
            await gitHub.PushCherryPickBranchAsync(info.Number, parentBranch, cherryPickBranch, cancellationToken);
        }

        var cherryPickUrl = await EnsurePullRequestAsync(
            cherryPickBranch,
            parentBranch,
            $"Cherry-pick \"{OneLine(info.Title)}\" (#{info.Number})",
            $"Cherry-picks pull request #{info.Number}, which was merged into `{info.BaseBranch}` instead of `{parentBranch}`. " +
            $"It is rolled back on `{info.BaseBranch}` by a separate pull request.\n\nCreated automatically.",
            requireOpen: false,
            cancellationToken);
        pullRequests.Replace(configuration.GitHubRepositoryUrl, info.Number, cherryPickUrl);
        DFLogger.LogOutput(DFLogLevel.INFO, "SanityCheck", $"Rolled back #{info.Number} of {tracked.JiraKey}; re-applied in {cherryPickUrl}.");
        return new SanityCheckAction(tracked.JiraKey, info.Number, RepairedByMergedPullRequest, cherryPickUrl);
    }

    // Reuses an existing pull request for the same head/base so reruns never create duplicates.
    private async Task<string> EnsurePullRequestAsync(
        string head,
        string baseBranch,
        string title,
        string body,
        bool requireOpen,
        CancellationToken cancellationToken)
    {
        var existing = await gitHub.FindPullRequestAsync(head, baseBranch, cancellationToken);
        if (existing is not null &&
            (!requireOpen || existing.State == GitHubRepository.PullRequestState.Open))
        {
            return existing.Url;
        }

        return await gitHub.CreatePullRequestAsync(head, baseBranch, title, body, cancellationToken);
    }

    private static string OneLine(string text) => Regex.Replace(text, @"[\r\n\t]+", " ").Trim();
}
