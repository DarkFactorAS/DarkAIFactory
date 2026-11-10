using DFCommonLib.DataAccess;

namespace ReviewWorker.Repository;

internal sealed class ReviewCommentAttemptRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>Comment ids that are finished (Resolved/Failed) or have used up their attempts.</summary>
    public IReadOnlySet<long> GetHandledCommentIds(long pullRequestId, int maxAttempts)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT github_comment_id FROM review_comment_attempts WHERE pull_request_id = @pull_request_id " +
            "AND (status IN ('Resolved', 'Failed') OR attempts >= @max_attempts)");
        command.AddParameter("@pull_request_id", pullRequestId);
        command.AddParameter("@max_attempts", maxAttempts);
        using var reader = command.ExecuteReader();
        var result = new HashSet<long>();
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }
}
