using System.Text.RegularExpressions;
using ManagerAgent.Repository;
using DFCommonLib.Logger;

namespace ManagerAgent;

/// <summary>
/// Restores missing <c>pull_requests</c> rows. An open GitHub pull request is tracked again when its Jira
/// issue belongs to the project, is in the In Review status, and the table has no row for that pull request.
/// The Jira key is read from the agent branch name (<c>ai-agent/KEY-123...</c>), or else from a title that
/// starts with the key. Recording is idempotent, so the check can run every poll.
/// </summary>
internal sealed class PullRequestTrackingRecovery(
    AgentConfiguration configuration,
    string jiraProjectKey,
    PullRequestRepository pullRequests,
    Func<string, CancellationToken, Task<JiraIssue>> getIssue,
    Func<CancellationToken, Task<IReadOnlyList<GitHubRepository.PullRequestInfo>>> listOpenPullRequests)
{
    private const string Category = "TrackingRecovery";

    public async Task<IReadOnlyList<GitHubRepository.PullRequestInfo>> RunAsync(CancellationToken cancellationToken)
    {
        var tracked = pullRequests.ListTracked(configuration.GitHubRepositoryUrl)
            .Select(pullRequest => pullRequest.Number)
            .ToHashSet();
        var recorded = new List<GitHubRepository.PullRequestInfo>();
        foreach (var pullRequest in await listOpenPullRequests(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pullRequest.State != GitHubRepository.PullRequestState.Open || tracked.Contains(pullRequest.Number))
            {
                continue;
            }

            var jiraKey = FindJiraKey(pullRequest, jiraProjectKey);
            if (jiraKey is null)
            {
                continue;
            }

            try
            {
                var issue = await getIssue(jiraKey, cancellationToken);
                if (!string.Equals(issue.StatusName.Trim(), configuration.InReviewStatus.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pullRequests.Record(issue.Key, configuration.GitHubRepositoryUrl, pullRequest.Url);
                tracked.Add(pullRequest.Number);
                recorded.Add(pullRequest);
                DFLogger.LogOutput(
                    DFLogLevel.INFO,
                    Category,
                    $"Recorded missing pull request #{pullRequest.Number} for {issue.Key} ({configuration.InReviewStatus}).");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    Category,
                    $"Could not check pull request #{pullRequest.Number} for {jiraKey}: {exception.Message}");
            }
        }

        return recorded;
    }

    internal static string? FindJiraKey(GitHubRepository.PullRequestInfo pullRequest, string jiraProjectKey)
    {
        var key = Regex.Escape(jiraProjectKey.Trim());
        var branch = Regex.Match(
            pullRequest.HeadBranch, $@"^ai-agent/({key}-\d+)(?:-|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = branch.Success
            ? branch
            : Regex.Match(pullRequest.Title, $@"^\s*\[?({key}-\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
