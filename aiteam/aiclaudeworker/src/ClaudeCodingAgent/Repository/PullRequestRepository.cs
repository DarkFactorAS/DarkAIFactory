using DFCommonLib.DataAccess;

namespace ClaudeCodingAgent.Repository;

internal sealed class PullRequestRepository(IDbConnectionFactory connectionFactory)
{
    public static long ParsePullRequestNumber(string pullRequestUrl)
    {
        var lastSegment = pullRequestUrl.TrimEnd('/').Split('/')[^1];
        return long.TryParse(lastSegment, out var number) && number > 0
            ? number
            : throw new InvalidDataException($"Cannot read the pull request number from \"{pullRequestUrl}\".");
    }

    // Idempotent: pr_url is unique, so recording the same pull request twice is a no-op.
    public void Record(string jiraKey, Uri repositoryUrl, string pullRequestUrl)
    {
        using var command = connectionFactory.CreateCommand(
            """
            INSERT INTO pull_requests (jira_key, repository_url, pr_number, pr_url)
            VALUES (@jira_key, @repository_url, @pr_number, @pr_url)
            ON DUPLICATE KEY UPDATE pr_url = pr_url
            """);
        command.AddParameter("@jira_key", jiraKey);
        command.AddParameter("@repository_url", repositoryUrl.AbsoluteUri);
        command.AddParameter("@pr_number", ParsePullRequestNumber(pullRequestUrl));
        command.AddParameter("@pr_url", pullRequestUrl);
        command.ExecuteNonQuery();
    }
}
