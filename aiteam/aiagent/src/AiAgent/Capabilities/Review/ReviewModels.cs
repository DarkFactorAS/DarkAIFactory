using AiAgent.Repositories;

namespace AiAgent.Capabilities.Review;

/// <summary>An unresolved, current inline review comment left by the configured reviewer.</summary>
public sealed record ReviewComment(
    long Id,
    long PullRequestId,
    long PullRequestNumber,
    string Path,
    int? Line,
    string Body,
    string Author,
    string HeadSha);

/// <summary>Unresolved comments of a pull request: the actionable ones plus diagnostics counts.</summary>
public sealed record ReviewCommentScan(
    IReadOnlyList<ReviewComment> Actionable,
    int OpenComments,
    int HandledComments,
    IReadOnlyDictionary<string, int> OtherAuthors)
{
    public static ReviewCommentScan Empty { get; } = new([], 0, 0, new Dictionary<string, int>());
}

/// <summary>Settings of the Review capability.</summary>
public sealed record ReviewOptions(
    string RepositoryToken,
    string ReviewerLogin,
    int MaxAttempts = 3,
    string? BuildCommand = null,
    string? TestCommand = null,
    string? CopilotToken = null,
    string? WorkspaceRoot = null);

public enum FixOutcome { Skipped, NoChanges, Failed, Pushed }

public sealed record FixResult(FixOutcome Outcome, string? Error = null);

public interface IFixRunner
{
    Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken);
}

public interface IProjectCommandRunner
{
    /// <summary>Runs a shell command; returns null on success or a failure description.</summary>
    Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken);
}

/// <summary>Persistence of per-comment attempts (agent_review_attempts).</summary>
public interface IReviewAttemptStore
{
    IReadOnlySet<long> GetHandledCommentIds(long pullRequestId, int maxAttempts);
    IReadOnlyDictionary<long, int> GetAttempts(long pullRequestId, IReadOnlyCollection<long> commentIds);
    void Record(
        long agentId, long pullRequestId, IReadOnlyCollection<long> commentIds, string status, bool countAttempt,
        string? error);
}
