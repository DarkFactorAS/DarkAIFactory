using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

public interface IAgentPullRequestRepository
{
    void Record(AgentPullRequestRecord pullRequest);

    IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId);
}

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

    public IReadOnlyList<AgentPullRequestRecord> GetForAgent(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `agent_id`, `jira_key`, `repository_url`, `pr_number`, `pr_url` " +
            "FROM `agent_pull_requests` WHERE `agent_id` = @agent_id ORDER BY `id`");
        command.AddParameter("@agent_id", agentId);
        using var reader = command.ExecuteReader();
        var result = new List<AgentPullRequestRecord>();
        while (reader.Read())
        {
            result.Add(new AgentPullRequestRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
        }

        return result;
    }
}
