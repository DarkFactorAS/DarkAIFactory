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
    string HeadSha,
    string ThreadId = "");

public sealed record ReviewThreadMember(long Id, string ThreadId, string Status);

/// <summary>
/// Unresolved comments of a pull request: the actionable ones, the total count, how many of the reviewer's were
/// already handled (Resolved) or failed (Failed / attempts exhausted), and comment counts per other author.
/// </summary>
public sealed record ReviewCommentScan(
    IReadOnlyList<ReviewComment> Actionable,
    int OpenComments,
    int HandledComments,
    int FailedComments,
    IReadOnlyDictionary<string, int> OtherAuthors)
{
    public IReadOnlyList<ReviewThreadMember> ThreadMembers { get; init; } = [];

    public static ReviewCommentScan Empty { get; } = new([], 0, 0, 0, new Dictionary<string, int>());
}

/// <summary>Settings of the Review capability.</summary>
public sealed record ReviewOptions(
    string RepositoryToken,
    string ReviewerLogin,
    int MaxAttempts = 3,
    string? CopilotToken = null,
    string? WorkspaceRoot = null)
{
    /// <summary>Maximum duration of one runner call (LLM run plus build and test) for one review comment.</summary>
    public TimeSpan RunnerTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Merge a pull request automatically once every review thread is resolved. Off by default.</summary>
    public bool AutoMergeEnabled { get; init; }

    /// <summary>GitHub merge method: merge, squash or rebase.</summary>
    public string MergeMethod { get; init; } = "squash";

    /// <summary>Path prefixes of agent code; a pull request changing any of them is never merged automatically.</summary>
    public IReadOnlyList<string> AgentCodePaths { get; init; } = AutoMergePolicy.DefaultAgentCodePaths;
}

/// <summary>Facts about a pull request that decide whether it may be merged automatically.</summary>
public sealed record PullRequestMergeInfo(
    bool IsDraft, string MergeableState, string HeadSha, string HeadBranch, IReadOnlyList<string> ChangedFiles);

/// <summary>Rules deciding whether the Review capability may merge a pull request.</summary>
public static class AutoMergePolicy
{
    /// <summary>Head-branch prefix of a parent task's integration branch (see JiraWorkflowRules.ParentBranchName).</summary>
    public const string ParentBranchPrefix = "aiagent/parent/";

    public static readonly IReadOnlyList<string> DefaultAgentCodePaths =
    [
        "aiteam/", ".agents/", "AGENTS.md", "CLAUDE.md", ".github/"
    ];

    /// <summary>Returns null when the pull request may be merged, otherwise the reason it may not.</summary>
    public static string? BlockReason(
        PullRequestMergeInfo info, int totalThreads, int unresolvedThreads, int failedCount,
        IReadOnlyList<string> agentCodePaths)
    {
        if (info.IsDraft)
        {
            return "the pull request is a draft";
        }

        if (!string.Equals(info.MergeableState, "clean", StringComparison.OrdinalIgnoreCase))
        {
            return $"mergeable state is '{info.MergeableState}', not 'clean'";
        }

        if (totalThreads == 0)
        {
            return "the pull request has no review comments";
        }

        if (!ReviewCapability.IsReadyToMerge(unresolvedThreads, failedCount))
        {
            return "review comments are not all resolved";
        }

        if (info.HeadBranch.StartsWith(ParentBranchPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "it is the pull request of a parent task with sub-tasks";
        }

        var agentFile = info.ChangedFiles.FirstOrDefault(file => agentCodePaths.Any(prefix =>
            file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        return agentFile is null ? null : $"it changes agent code ('{agentFile}')";
    }
}

public enum FixOutcome { Skipped, NoChanges, Failed, Pushed }

/// <summary>Result of fixing a pull request's comments; counts are per comment.</summary>
public sealed record FixResult(
    FixOutcome Outcome, string? Error = null, int PushedCount = 0, int FailedCount = 0, bool QuarantineWorkspace = false);

public interface IFixRunner
{
    Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken);
}

public interface IProjectCommandRunner
{
    /// <summary>Runs a shell command; returns null on success or a failure description.</summary>
    Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken);
}

public interface IReviewThreadResolver
{
    /// <summary>Resolves the review thread on GitHub.</summary>
    Task ResolveThreadAsync(StoredAgentPullRequest pullRequest, string threadId, CancellationToken cancellationToken);
}

/// <summary>Persistence of per-comment attempts (agent_review_attempts).</summary>
public interface IReviewAttemptStore
{
    /// <summary>
    /// Comments that are finished (Resolved/Failed) or have used up their attempts, with their status
    /// (comment id to status).
    /// </summary>
    IReadOnlyDictionary<long, string> GetHandledComments(long pullRequestId, int maxAttempts);
    IReadOnlyDictionary<long, int> GetAttempts(long pullRequestId, IReadOnlyCollection<long> commentIds);
    void Record(
        long agentId, long pullRequestId, IReadOnlyCollection<long> commentIds, string status, bool countAttempt,
        string? error);
}
