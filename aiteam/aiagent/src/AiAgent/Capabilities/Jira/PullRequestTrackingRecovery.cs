using AiAgent.Core;
using AiAgent.Repositories;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Jira;

/// <summary>
/// Recovers pull requests that were created but could not be recorded in agent_pull_requests: the pending
/// label and comment written by <see cref="JiraWorkflow"/> are turned into rows without rerunning the work.
/// </summary>
internal sealed class PullRequestTrackingRecovery(
    long agentId,
    IJiraClient jira,
    IAgentPullRequestRepository pullRequests,
    Func<string, Uri?> resolveRepository)
{
    public const string PendingLabel = "aiagent-pr-tracking-pending";
    public const string PendingCommentPrefix = "Pull request tracking pending: ";

    public async Task RecoverAsync(string jiraProjectKey, CancellationToken cancellationToken)
    {
        var issues = await jira.SearchAsync(
            $"project = \"{JiraQuery.EscapeJql(jiraProjectKey)}\" " +
            $"AND labels in (\"{PendingLabel}\") ORDER BY updated ASC",
            cancellationToken);
        var repository = resolveRepository(jiraProjectKey);
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var comments = await jira.GetCommentsAsync(issue.Key, cancellationToken);
                var urls = comments
                    .SelectMany(comment => comment.Split('\n'))
                    .Where(line => line.StartsWith(PendingCommentPrefix, StringComparison.Ordinal))
                    .Select(line => line[PendingCommentPrefix.Length..].Trim())
                    .Where(url => url.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (urls.Length == 0)
                {
                    DFLogger.LogOutput(
                        DFLogLevel.WARNING, "JiraRecovery",
                        $"Could not recover pull-request tracking for {issue.Key}: no tracking URL was found in Jira comments.");
                    continue;
                }

                foreach (var url in urls)
                {
                    var number = ParsePullRequestNumber(url);
                    if (repository is null || !BelongsToRepository(repository, url, number))
                    {
                        throw new InvalidDataException(
                            $"The tracking URL for {issue.Key} does not belong to the configured repository.");
                    }

                    pullRequests.Record(new AgentPullRequestRecord(agentId, issue.Key, repository.AbsoluteUri, number, url));
                }

                await jira.RemoveLabelAsync(issue.Key, PendingLabel, cancellationToken);
                DFLogger.LogOutput(
                    DFLogLevel.INFO, "JiraRecovery",
                    $"Recovered pull-request tracking for {issue.Key} without rerunning implementation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR, "JiraRecovery",
                    $"Could not recover pull-request tracking for {issue.Key}: {exception.Message}");
            }
        }
    }

    internal static long ParsePullRequestNumber(string pullRequestUrl)
    {
        var lastSegment = pullRequestUrl.TrimEnd('/').Split('/')[^1];
        return long.TryParse(lastSegment, out var number) && number > 0
            ? number
            : throw new InvalidDataException($"Cannot read the pull request number from \"{pullRequestUrl}\".");
    }

    internal static bool BelongsToRepository(Uri repositoryUrl, string pullRequestUrl, long pullRequestNumber)
    {
        if (!Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !url.IsDefaultPort ||
            !string.IsNullOrEmpty(url.UserInfo) ||
            !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment))
        {
            return false;
        }

        var repositoryPath = repositoryUrl.AbsolutePath.Trim('/');
        if (repositoryPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repositoryPath = repositoryPath[..^4];
        }

        return string.Equals(
            url.AbsolutePath.TrimEnd('/'), $"/{repositoryPath}/pull/{pullRequestNumber}", StringComparison.OrdinalIgnoreCase);
    }
}
