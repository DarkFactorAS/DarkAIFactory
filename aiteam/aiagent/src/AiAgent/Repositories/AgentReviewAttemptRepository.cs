using AiAgent.Capabilities.Review;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>agent_review_attempts.</summary>
public sealed class AgentReviewAttemptRepository(IDbConnectionFactory connectionFactory) : IReviewAttemptStore
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

    /// <summary>Comment ids that are finished (Resolved/Failed) or have used up their attempts.</summary>
    public IReadOnlySet<long> GetHandledCommentIds(long pullRequestId, int maxAttempts)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `github_comment_id` FROM `agent_review_attempts` WHERE `pull_request_id` = @pr_id " +
            "AND (`status` IN ('Resolved', 'Failed') OR `attempts` >= @max_attempts)");
        command.AddParameter("@pr_id", pullRequestId);
        command.AddParameter("@max_attempts", maxAttempts);
        using var reader = command.ExecuteReader();
        var result = new HashSet<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>Attempts used so far per comment id (comments without a row are absent).</summary>
    public IReadOnlyDictionary<long, int> GetAttempts(long pullRequestId, IReadOnlyCollection<long> commentIds)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `github_comment_id`, `attempts` FROM `agent_review_attempts` WHERE `pull_request_id` = @pr_id");
        command.AddParameter("@pr_id", pullRequestId);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, int>();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (commentIds.Contains(id))
            {
                result[id] = reader.GetInt32(1);
            }
        }

        return result;
    }

    /// <summary>Upserts one row per comment, optionally incrementing the persisted attempt count.</summary>
    public void Record(
        long agentId, long pullRequestId, IReadOnlyCollection<long> commentIds, string status, bool countAttempt,
        string? error)
    {
        foreach (var commentId in commentIds)
        {
            using var command = connectionFactory.CreateCommand(
                "INSERT INTO `agent_review_attempts` " +
                "(`agent_id`, `pull_request_id`, `github_comment_id`, `attempts`, `status`, `last_error`) " +
                "VALUES (@agent_id, @pr_id, @comment_id, @increment, @status, @last_error) " +
                "ON DUPLICATE KEY UPDATE `attempts` = `attempts` + @increment, `status` = @status, `last_error` = @last_error");
            command.AddParameter("@agent_id", agentId);
            command.AddParameter("@pr_id", pullRequestId);
            command.AddParameter("@comment_id", commentId);
            command.AddParameter("@increment", countAttempt ? 1 : 0);
            command.AddParameter("@status", status);
            command.AddParameter("@last_error", error is null ? DBNull.Value : error.Length > 2_000 ? error[..2_000] : error);
            command.ExecuteNonQuery();
        }
    }
}
