using CodingAgent.Repository;
using DFCommonLib.Logger;

namespace CodingAgent;

internal sealed class PullRequestRecordRecovery(
    AgentConfiguration configuration,
    JiraClient jira,
    PullRequestRepository pullRequests)
{
    public const string PendingLabel = "ai-pr-tracking-pending";
    public const string PendingCommentPrefix = "Pull request tracking pending: ";

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var issues = await jira.FindPullRequestTrackingIssuesAsync(cancellationToken);
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
                        DFLogLevel.WARNING,
                        "Recovery",
                        $"Could not recover pull-request tracking for {issue.Key}: no tracking URL was found in Jira comments.");
                    continue;
                }

                foreach (var url in urls)
                {
                    var number = PullRequestRepository.ParsePullRequestNumber(url);
                    if (!BelongsToConfiguredRepository(url, number))
                    {
                        throw new InvalidDataException(
                            $"The tracking URL for {issue.Key} does not belong to the configured repository.");
                    }

                    pullRequests.Record(issue.Key, configuration.GitHubRepositoryUrl, url);
                }

                await jira.RemoveLabelAsync(issue.Key, PendingLabel, cancellationToken);
                DFLogger.LogOutput(DFLogLevel.INFO, "Recovery", $"Recovered pull-request tracking for {issue.Key} without rerunning implementation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, "Recovery", $"Could not recover pull-request tracking for {issue.Key}: {exception.Message}");
            }
        }
    }

    private bool BelongsToConfiguredRepository(string pullRequestUrl, long pullRequestNumber)
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

        var repositoryPath = configuration.GitHubRepositoryUrl.AbsolutePath.Trim('/');
        if (repositoryPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repositoryPath = repositoryPath[..^4];
        }

        return string.Equals(
            url.AbsolutePath.TrimEnd('/'),
            $"/{repositoryPath}/pull/{pullRequestNumber}",
            StringComparison.OrdinalIgnoreCase);
    }
}
