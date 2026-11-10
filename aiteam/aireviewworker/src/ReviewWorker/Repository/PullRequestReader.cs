using DFCommonLib.DataAccess;

namespace ReviewWorker.Repository;

internal sealed class PullRequestReader(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<TrackedPullRequest> List(Uri repositoryUrl)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT id, jira_key, repository_url, pr_number, pr_url FROM pull_requests " +
            "WHERE repository_url = @repository_url ORDER BY id");
        command.AddParameter("@repository_url", repositoryUrl.AbsoluteUri);
        using var reader = command.ExecuteReader();
        var result = new List<TrackedPullRequest>();
        while (reader.Read())
        {
            result.Add(new TrackedPullRequest(
                reader.GetInt64(0),
                reader.GetString(1),
                new Uri(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetString(4)));
        }

        return result;
    }
}
