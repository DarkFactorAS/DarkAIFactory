using AiAgent.Core;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Jira;

/// <summary>
/// Moves an issue to the selected-for-development status once all its "is blocked by" issues are Done.
/// Only issues in the configured source statuses (default To Do / Backlog) are considered.
/// </summary>
internal sealed class BlockedIssueUnblocker(JiraCapabilityConfig configuration, IJiraClient jira)
{
    public async Task<IReadOnlyList<string>> UnblockAsync(string jiraProjectKey, CancellationToken cancellationToken)
    {
        if (configuration.UnblockSourceStatuses.Count == 0)
        {
            return [];
        }

        var statuses = string.Join(", ", configuration.UnblockSourceStatuses.Select(
            status => $"\"{JiraQuery.EscapeJql(status)}\""));
        var issues = await jira.SearchAsync(
            $"project = \"{JiraQuery.EscapeJql(jiraProjectKey)}\" AND status in ({statuses}) " +
            "AND issuetype != Epic ORDER BY created ASC",
            cancellationToken);
        var moved = new List<string>();
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issue.Blockers.Count == 0 ||
                issue.Labels.Contains(configuration.IgnoredLabel) ||
                issue.Labels.Contains(configuration.FailureLabel) ||
                JiraWorkflowRules.OpenBlockers(issue.Blockers, configuration.DoneStatus).Count > 0)
            {
                continue;
            }

            try
            {
                await jira.TransitionToAsync(issue.Key, configuration.SelectedStatus, cancellationToken);
                moved.Add(issue.Key);
                DFLogger.LogOutput(
                    DFLogLevel.INFO, "JiraUnblocker",
                    $"Moved {issue.Key} to {configuration.SelectedStatus}: all blocking issues are {configuration.DoneStatus}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR, "JiraUnblocker", $"Could not move {issue.Key}: {exception.Message}");
            }
        }

        return moved;
    }
}
