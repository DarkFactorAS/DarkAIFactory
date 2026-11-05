using DFCommonLib.Logger;
using ManagerAgent.Repository;

namespace ManagerAgent;

internal sealed class PullRequestCloser(
    AgentConfiguration configuration,
    PullRequestRepository pullRequests,
    JiraClient jira,
    Func<PullRequestRecord, CancellationToken, Task<bool>> isMerged)
{
    // Moves In Review issues to Done once every stored pull request is merged.
    // Issues without a stored pull request are left untouched. Returns the keys moved to Done.
    public async Task<IReadOnlyList<string>> CloseMergedAsync(string jiraProjectKey, CancellationToken cancellationToken)
    {
        var issues = await jira.FindIssuesInStatusAsync(jiraProjectKey, configuration.InReviewStatus, cancellationToken);
        var closed = new List<string>();
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var records = pullRequests.FindByJiraKey(issue.Key);
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

                await jira.TransitionToAsync(issue.Key, configuration.DoneStatus, cancellationToken);
                closed.Add(issue.Key);
                DFLogger.LogOutput(DFLogLevel.INFO, "Closer", $"Moved {issue.Key} to {configuration.DoneStatus}: all pull requests are merged.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, "Closer", $"Could not check pull requests for {issue.Key}: {exception.Message}");
            }
        }

        return closed;
    }
}
