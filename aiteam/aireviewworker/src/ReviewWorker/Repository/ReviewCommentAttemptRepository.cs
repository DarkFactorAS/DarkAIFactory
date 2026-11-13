using DFCommonLib.DataAccess;

namespace ReviewWorker.Repository;

internal sealed class ReviewCommentAttemptRepository(IDbConnectionFactory connectionFactory) : IAttemptStore
{
    /// <summary>
    /// Comments that are finished (Resolved/Failed) or have used up their attempts, with their status
    /// (comment id to status).
    /// </summary>
    public IReadOnlyDictionary<long, string> GetHandledComments(long pullRequestId, int maxAttempts)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT github_comment_id, status FROM review_comment_attempts WHERE pull_request_id = @pull_request_id " +
            "AND (status IN ('Resolved', 'Failed') OR attempts >= @max_attempts)");
        command.AddParameter("@pull_request_id", pullRequestId);
        command.AddParameter("@max_attempts", maxAttempts);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, string>();
        while (reader.Read())
        {
            result[reader.GetInt64(0)] = reader.GetString(1);
        }

        return result;
    }

    /// <summary>Attempts used so far per comment id (comments without a row are absent).</summary>
    public IReadOnlyDictionary<long, int> GetAttempts(long pullRequestId, IReadOnlyCollection<long> commentIds)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT github_comment_id, attempts FROM review_comment_attempts WHERE pull_request_id = @pull_request_id");
        command.AddParameter("@pull_request_id", pullRequestId);
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

    /// <summary>Upserts one row per comment, adding <paramref name="attemptChange"/> to the attempt count (never below 0).</summary>
    public void Record(
        long pullRequestId, IReadOnlyCollection<long> commentIds, string status, int attemptChange, string? error)
    {
        foreach (var commentId in commentIds)
        {
            using var command = connectionFactory.CreateCommand(
                "INSERT INTO review_comment_attempts (pull_request_id, github_comment_id, attempts, status, last_error) " +
                "VALUES (@pull_request_id, @github_comment_id, GREATEST(@increment, 0), @status, @last_error) " +
                "ON DUPLICATE KEY UPDATE attempts = GREATEST(attempts + @increment, 0), status = @status, " +
                "last_error = @last_error");
            command.AddParameter("@pull_request_id", pullRequestId);
            command.AddParameter("@github_comment_id", commentId);
            command.AddParameter("@increment", attemptChange);
            command.AddParameter("@status", status);
            command.AddParameter("@last_error", error is null ? DBNull.Value : error.Length > 2_000 ? error[..2_000] : error);
            command.ExecuteNonQuery();
        }
    }
}
