using DFCommonLib.DataAccess;

namespace ManagerAgent.Repository;

internal sealed record PullRequestRecord(Uri RepositoryUrl, long Number, string Url);

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
}
