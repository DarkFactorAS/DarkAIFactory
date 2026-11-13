using AiAgent.Core;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Jira;

/// <summary>A claimed Jira issue. Disposing releases the cross-process claim.</summary>
public sealed class JiraPickup(JiraIssue issue, IDisposable claim) : IDisposable
{
    public JiraIssue Issue { get; } = issue;
    public bool InProgressTransitionAttempted { get; internal set; }
    public void Dispose() => claim.Dispose();
}

/// <summary>
/// Pickup, status changes and failure handling of Jira issues using the aiagent-* labels.
/// Used by the Builder capability; the Jira capability itself only runs intake, recovery, closing and unblocking.
/// </summary>
public sealed class JiraWorkflow(IJiraClient jira, JiraCapabilityConfig configuration)
{
    private const string Source = "JiraWorkflow";

    /// <summary>Issues of the project that are ready to be picked up (not picked, failed or ignored).</summary>
    public Task<IReadOnlyList<JiraIssue>> FindWorkAsync(string projectKey, CancellationToken cancellationToken)
    {
        var jql = string.IsNullOrWhiteSpace(configuration.JqlOverride)
            ? $"project = \"{JiraQuery.EscapeJql(projectKey)}\" " +
              $"AND status = \"{JiraQuery.EscapeJql(configuration.SelectedStatus)}\" " +
              "AND issuetype != Epic ORDER BY priority DESC, created ASC"
            : JiraQuery.ScopeToProject(configuration.JqlOverride, projectKey);
        foreach (var label in new[] { configuration.FailureLabel, configuration.PickupLabel, configuration.IgnoredLabel })
        {
            jql = JiraQuery.AddPickupLabelExclusion(jql, label);
        }

        return jira.SearchAsync(jql, cancellationToken);
    }

    /// <summary>
    /// Re-reads the candidate, claims it, labels it, assigns it and moves it to In Progress.
    /// Returns null when the issue must not be picked up now (changed, labelled, blocked or claimed elsewhere).
    /// </summary>
    public async Task<JiraPickup?> TryPickupAsync(
        AgentContext context, JiraIssue candidate, CancellationToken cancellationToken)
    {
        var issue = await jira.GetIssueAsync(candidate.Key, cancellationToken);
        if (issue.Labels.Contains(configuration.PickupLabel) ||
            issue.Labels.Contains(configuration.FailureLabel) ||
            issue.Labels.Contains(configuration.IgnoredLabel) ||
            !string.Equals(issue.StatusName, candidate.StatusName, StringComparison.Ordinal))
        {
            return null;
        }

        var openBlockers = JiraWorkflowRules.OpenBlockers(issue.Blockers, configuration.DoneStatus);
        if (openBlockers.Count > 0)
        {
            DFLogger.LogOutput(
                DFLogLevel.WARNING, Source,
                $"Skipping {issue.Key}: blocked by {JiraWorkflowRules.DescribeBlockers(openBlockers)}.");
            return null;
        }

        var claim = context.ClaimLock.TryAcquire(context.AgentId, issue.Key);
        if (claim is null)
        {
            DFLogger.LogOutput(DFLogLevel.INFO, Source, $"Another agent is already processing {issue.Key}.");
            return null;
        }

        var pickup = new JiraPickup(issue, claim);
        try
        {
            await jira.AddLabelAsync(issue.Key, configuration.PickupLabel, cancellationToken);
            if (configuration.AssigneeAccountId.Length > 0)
            {
                await TryAsync(
                    () => jira.AssignAsync(issue.Key, configuration.AssigneeAccountId, cancellationToken),
                    issue.Key, "assign this issue", cancellationToken);
            }

            pickup.InProgressTransitionAttempted = true;
            await TryAsync(
                () => jira.TransitionToAsync(issue.Key, configuration.InProgressStatus, cancellationToken),
                issue.Key, $"move this issue to \"{configuration.InProgressStatus}\"", cancellationToken);
            return pickup;
        }
        catch
        {
            pickup.Dispose();
            throw;
        }
    }

    /// <summary>Moves the issue to In Review after a pull request was delivered.</summary>
    public Task MarkInReviewAsync(string issueKey, CancellationToken cancellationToken) =>
        jira.TransitionToAsync(issueKey, configuration.InReviewStatus, cancellationToken);

    /// <summary>
    /// Records a pull request that could not be stored so <see cref="PullRequestTrackingRecovery"/> can store it later.
    /// </summary>
    public async Task SchedulePullRequestRecoveryAsync(
        string issueKey, string pullRequestUrl, CancellationToken cancellationToken)
    {
        await jira.AddCommentAsync(
            issueKey, $"{PullRequestTrackingRecovery.PendingCommentPrefix}{pullRequestUrl}", cancellationToken);
        await jira.AddLabelAsync(issueKey, PullRequestTrackingRecovery.PendingLabel, cancellationToken);
    }

    /// <summary>
    /// Restores the original status when nothing was pushed, labels the issue failed and explains how to recover.
    /// The pickup label stays so the issue is not processed twice automatically. Never throws.
    /// </summary>
    public async Task ReportFailureAsync(
        JiraPickup pickup, Exception exception, string? pushedBranchName, string? pullRequestUrl,
        CancellationToken cancellationToken)
    {
        var issue = pickup.Issue;
        var statusRestored = false;
        if (pushedBranchName is null && pickup.InProgressTransitionAttempted && issue.StatusName.Length > 0 &&
            !string.Equals(issue.StatusName, configuration.InProgressStatus, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await jira.TransitionToAsync(issue.Key, issue.StatusName, cancellationToken);
                statusRestored = true;
            }
            catch (Exception restoreException) when (!cancellationToken.IsCancellationRequested)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR, Source,
                    $"Could not restore the status of {issue.Key} to {issue.StatusName}: {restoreException.Message}");
            }
        }

        try
        {
            await jira.AddLabelAsync(issue.Key, configuration.FailureLabel, cancellationToken);
            var details = pushedBranchName is not null
                ? pullRequestUrl is null
                    ? $"The changes were already pushed to `{pushedBranchName}`. Create or locate a pull request for it and move the issue to \"{configuration.InReviewStatus}\". Do not rerun the implementation."
                    : $"The pull request is {pullRequestUrl}. Move the issue to \"{configuration.InReviewStatus}\" without rerunning the implementation."
                : statusRestored
                    ? $"The issue status was restored to \"{issue.StatusName}\"."
                    : "The issue status was not restored automatically; verify it before retrying.";
            var guidance = pushedBranchName is not null
                ? $"The `{configuration.PickupLabel}` label remains in place to prevent duplicate work. Do not remove it to rerun implementation."
                : $"The `{configuration.PickupLabel}` and `{configuration.FailureLabel}` labels remain in place. Remove both after resolving the failure to allow the issue to be picked up again.";
            await jira.AddCommentAsync(
                issue.Key,
                $"The agent could not complete this task: {Limit(exception.Message)}\n\n{guidance}\n\n{details}",
                cancellationToken);
        }
        catch (Exception notificationException) when (!cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(
                DFLogLevel.ERROR, Source,
                $"Could not report the failure for {issue.Key} to Jira: {notificationException.Message}.");
        }
    }

    private async Task TryAsync(Func<Task> action, string issueKey, string what, CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Source, $"Could not {what} for {issueKey}: {exception.Message}");
            try
            {
                await jira.AddCommentAsync(
                    issueKey, $"The agent could not {what}: {Limit(exception.Message)}\n\nWork continues.", cancellationToken);
            }
            catch (Exception notificationException) when (!cancellationToken.IsCancellationRequested)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR, Source, $"Could not post the warning for {issueKey}: {notificationException.Message}");
            }
        }
    }

    private static string Limit(string text) => text.Length <= 1000 ? text : text[..1000] + "…";
}
