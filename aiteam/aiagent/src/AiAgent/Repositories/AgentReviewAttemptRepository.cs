using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>agent_review_attempts.</summary>
public sealed class AgentReviewAttemptRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>Counts an attempt for a review comment and returns the new attempt count.</summary>
    public int RegisterAttempt(long agentId, long pullRequestId, long githubCommentId)
    {
        using (var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_review_attempts` (`agent_id`, `pull_request_id`, `github_comment_id`, `attempts`, `status`) " +
            "VALUES (@agent_id, @pr_id, @comment_id, 1, 'InProgress') " +
            "ON DUPLICATE KEY UPDATE `attempts` = `attempts` + 1, `status` = 'InProgress'"))
        {
            command.AddParameter("@agent_id", agentId);
            command.AddParameter("@pr_id", pullRequestId);
            command.AddParameter("@comment_id", githubCommentId);
            command.ExecuteNonQuery();
        }

        using var select = connectionFactory.CreateCommand(
            "SELECT `attempts` FROM `agent_review_attempts` WHERE `pull_request_id` = @pr_id AND `github_comment_id` = @comment_id");
        select.AddParameter("@pr_id", pullRequestId);
        select.AddParameter("@comment_id", githubCommentId);
        return Convert.ToInt32(select.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void SetStatus(long pullRequestId, long githubCommentId, string status, string? lastError = null)
    {
        if (status is not ("Pending" or "InProgress" or "Resolved" or "Failed"))
        {
            throw new ArgumentException("Unknown review attempt status.", nameof(status));
        }

        using var command = connectionFactory.CreateCommand(
            "UPDATE `agent_review_attempts` SET `status` = @status, `last_error` = @error " +
            "WHERE `pull_request_id` = @pr_id AND `github_comment_id` = @comment_id");
        command.AddParameter("@status", status);
        command.AddParameter("@error", (object?)lastError ?? DBNull.Value);
        command.AddParameter("@pr_id", pullRequestId);
        command.AddParameter("@comment_id", githubCommentId);
        command.ExecuteNonQuery();
    }
}
