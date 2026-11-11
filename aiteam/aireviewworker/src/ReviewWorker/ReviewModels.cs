namespace ReviewWorker;

/// <summary>An open pull request recorded in the pull_requests table.</summary>
internal sealed record TrackedPullRequest(long Id, string JiraKey, Uri RepositoryUrl, long Number, string Url);

/// <summary>An unresolved, current inline review comment left by the configured reviewer.</summary>
internal sealed record ReviewComment(
    long Id,
    long PullRequestId,
    long PullRequestNumber,
    string Path,
    int? Line,
    string Body,
    string Author,
    string HeadSha);

/// <summary>
/// Result of reading a pull request's unresolved, current review comments: the actionable ones,
/// the total count, how many of the reviewer's were already handled, and comment counts per other author.
/// </summary>
internal sealed record ReviewCommentScan(
    IReadOnlyList<ReviewComment> Actionable,
    int OpenComments,
    int HandledComments,
    IReadOnlyDictionary<string, int> OtherAuthors)
{
    public static ReviewCommentScan Empty { get; } =
        new([], 0, 0, new Dictionary<string, int>());
}
