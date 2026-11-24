using AiAgent.Core;
using AiAgent.Repositories;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Jira;

/// <summary>Moves In Review issues to Done once every agent_pull_requests row of the issue is merged.</summary>
internal sealed class PullRequestCloser(
    JiraCapabilityConfig configuration,
    IJiraClient jira,
    IAgentPullRequestRepository pullRequests,
    Func<AgentPullRequestRecord, CancellationToken, Task<bool>> isMerged,
    AiAgent.Metrics.RecordRun? record = null)
{
    /// <summary>Issues without a stored pull request are left untouched. Returns the keys moved to Done.</summary>
    public async Task<IReadOnlyList<string>> CloseMergedAsync(
        string jiraProjectKey, CancellationToken cancellationToken)
    {
        var issues = await jira.SearchAsync(
            $"project = \"{JiraQuery.EscapeJql(jiraProjectKey)}\" " +
            $"AND status = \"{JiraQuery.EscapeJql(configuration.InReviewStatus)}\" ORDER BY created ASC",
            cancellationToken);
        if (issues.Count == 0)
        {
            return [];
        }

        var byKey = pullRequests.GetAll()
            .ToLookup(record => record.JiraKey, StringComparer.OrdinalIgnoreCase);
        var closed = new List<string>();
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issue.Labels.Contains(PullRequestTrackingRecovery.PendingLabel, StringComparer.OrdinalIgnoreCase))
            {
                // Tracking recovery has not yet persisted every pull request URL; the stored set may be incomplete.
                continue;
            }

            try
            {
                var records = byKey[issue.Key].ToList();
                if (records.Count == 0)
                {
                    continue;
                }

                var allMerged = true;
                foreach (var record in records)
                {
                    if (!await isMerged(record, cancellationToken))
                    {
                        allMerged = false;
                        break;
                    }
                }

                if (!allMerged)
                {
                    continue;
                }

                await (record ?? AiAgent.Metrics.NullAgentRunRecorder.Record)(issue.Key, async run =>
                {
                    await jira.TransitionToAsync(issue.Key, configuration.DoneStatus, cancellationToken);
                    run.Succeed();
                });
                closed.Add(issue.Key);
                AgentLog.LogOutput(
                    DFLogLevel.INFO, "JiraCloser",
                    $"Moved {issue.Key} to {configuration.DoneStatus}: all pull requests are merged.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                AgentLog.LogOutput(
                    DFLogLevel.ERROR, "JiraCloser", $"Could not check pull requests for {issue.Key}: {exception.Message}");
            }
        }

        return closed;
    }
}
