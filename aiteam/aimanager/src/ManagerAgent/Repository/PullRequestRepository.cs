using DFCommonLib.DataAccess;

namespace ManagerAgent.Repository;

internal sealed record PullRequestRecord(Uri RepositoryUrl, long Number, string Url);

internal sealed record TrackedPullRequest(string JiraKey, Uri RepositoryUrl, long Number, string Url);

internal sealed class PullRequestRepository(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<PullRequestRecord> FindByJiraKey(string jiraKey)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT repository_url, pr_number, pr_url FROM pull_requests WHERE jira_key = @jira_key ORDER BY id");
        command.AddParameter("@jira_key", jiraKey);
        using var reader = command.ExecuteReader();
        var pullRequests = new List<PullRequestRecord>();
        while (reader.Read())
        {
            pullRequests.Add(new PullRequestRecord(
                new Uri(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2)));
        }

        return pullRequests;
    }

    public static long ParsePullRequestNumber(string pullRequestUrl)
    {
        var lastSegment = pullRequestUrl.TrimEnd('/').Split('/')[^1];
        return long.TryParse(lastSegment, out var number) && number > 0
            ? number
            : throw new InvalidDataException($"Cannot read the pull request number from \"{pullRequestUrl}\".");
    }

    // Rows may refer to closed or merged pull requests; callers must check the current GitHub state.
    public IReadOnlyList<TrackedPullRequest> ListTracked(Uri repositoryUrl)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT jira_key, repository_url, pr_number, pr_url FROM pull_requests " +
            "WHERE repository_url = @repository_url ORDER BY id");
        command.AddParameter("@repository_url", repositoryUrl.AbsoluteUri);
        using var reader = command.ExecuteReader();
        var result = new List<TrackedPullRequest>();
        while (reader.Read())
        {
            result.Add(new TrackedPullRequest(
                reader.GetString(0), new Uri(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3)));
        }

        return result;
    }

    public bool DeleteByNumber(Uri repositoryUrl, long pullRequestNumber)
    {
        using var command = connectionFactory.CreateCommand(
            "DELETE FROM pull_requests WHERE repository_url = @repository_url AND pr_number = @pr_number");
        command.AddParameter("@repository_url", repositoryUrl.AbsoluteUri);
        command.AddParameter("@pr_number", pullRequestNumber);
        return command.ExecuteNonQuery() > 0;
    }

    // Swaps the tracked row for a replacement pull request, keeping the row id and Jira key.
    public bool Replace(Uri repositoryUrl, long oldPullRequestNumber, string newPullRequestUrl)
    {
        using var command = connectionFactory.CreateCommand(
            "UPDATE pull_requests SET pr_number = @new_pr_number, pr_url = @new_pr_url " +
            "WHERE repository_url = @repository_url AND pr_number = @old_pr_number");
        command.AddParameter("@new_pr_number", ParsePullRequestNumber(newPullRequestUrl));
        command.AddParameter("@new_pr_url", newPullRequestUrl);
        command.AddParameter("@repository_url", repositoryUrl.AbsoluteUri);
        command.AddParameter("@old_pr_number", oldPullRequestNumber);
        return command.ExecuteNonQuery() > 0;
    }
}
