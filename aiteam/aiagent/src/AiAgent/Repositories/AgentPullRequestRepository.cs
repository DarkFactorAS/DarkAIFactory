using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

public interface IAgentPullRequestRepository
{
    void Record(AgentPullRequestRecord pullRequest);

    IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId);

    /// <summary>
    /// Returns the agent's pull requests together with their persisted <c>agent_pull_requests.id</c>,
    /// which is required as the <c>pull_request_id</c> foreign key when registering review attempts.
    /// </summary>
    IReadOnlyList<StoredAgentPullRequest> GetStoredForAgent(long agentId) =>
        throw new NotSupportedException($"{GetType().Name} does not expose persisted pull request IDs.");
}

/// <summary>A pull request row from agent_pull_requests including its database ID.</summary>
public sealed record StoredAgentPullRequest(long Id, AgentPullRequestRecord PullRequest);

/// <summary>agent_pull_requests.</summary>
public sealed class AgentPullRequestRepository(IDbConnectionFactory connectionFactory) : IAgentPullRequestRepository
{
    public void Record(AgentPullRequestRecord pullRequest)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_pull_requests` (`agent_id`, `jira_key`, `repository_url`, `pr_number`, `pr_url`) " +
            "VALUES (@agent_id, @jira_key, @repository_url, @pr_number, @pr_url) " +
            "ON DUPLICATE KEY UPDATE `pr_number` = VALUES(`pr_number`)");
        command.AddParameter("@agent_id", pullRequest.AgentId);
        command.AddParameter("@jira_key", pullRequest.JiraKey);
        command.AddParameter("@repository_url", pullRequest.RepositoryUrl);
        command.AddParameter("@pr_number", pullRequest.PullRequestNumber);
        command.AddParameter("@pr_url", pullRequest.PullRequestUrl);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId) =>
        GetStoredForAgent(agentId).Select(stored => stored.PullRequest).ToList();

    public IReadOnlyList<StoredAgentPullRequest> GetStoredForAgent(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `id`, `agent_id`, `jira_key`, `repository_url`, `pr_number`, `pr_url` " +
            "FROM `agent_pull_requests` WHERE `agent_id` = @agent_id ORDER BY `id`");
        command.AddParameter("@agent_id", agentId);
        using var reader = command.ExecuteReader();
        var result = new List<StoredAgentPullRequest>();
        while (reader.Read())
        {
            result.Add(new StoredAgentPullRequest(
                reader.GetInt64(0),
                new AgentPullRequestRecord(
                    reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5))));
        }

        return result;
    }
}
