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
