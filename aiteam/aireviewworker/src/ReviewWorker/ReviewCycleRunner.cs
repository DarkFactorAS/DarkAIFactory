using DFCommonLib.Logger;
using Microsoft.Extensions.DependencyInjection;
using ReviewWorker.Repository;

namespace ReviewWorker;

/// <summary>
/// One polling cycle: finds the open tracked pull requests, collects actionable review comments
/// and runs one fix attempt per pull request. Logs each step.
/// </summary>
internal static class ReviewCycleRunner
{
    private const string Category = "Worker";

    public static async Task RunAsync(
        long cycle,
        AgentConfiguration configuration,
        FixOptions fixOptions,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var pullRequests = services.GetRequiredService<PullRequestReader>();
        var attempts = services.GetRequiredService<ReviewCommentAttemptRepository>();
        var tracked = pullRequests.List(configuration.GitHubRepositoryUrl);
        Log($"Cycle {cycle}: {tracked.Count} tracked pull request(s) in pull_requests for " +
            $"repository_url '{configuration.GitHubRepositoryUrl.AbsoluteUri}'.");
        if (tracked.Count == 0)
        {
            return;
        }

        using var client = new GitHubReviewClient(
            configuration.GitHubRepositoryToken,
            configuration.ReviewerLogin,
            configuration.MaxAttemptsPerPullRequest,
            pullRequests,
            attempts);

        var open = await client.ListOpenPullRequestsAsync(configuration.GitHubRepositoryUrl, cancellationToken);
        Log($"Cycle {cycle}: {open.Count} of {tracked.Count} tracked pull request(s) are open on GitHub.");

        var total = 0;
        foreach (var pullRequest in open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = await client.ScanCommentsAsync(pullRequest, cancellationToken);
            var comments = scan.Actionable;
            total += comments.Count;
            LogScan(cycle, pullRequest, configuration.ReviewerLogin, scan);
            if (comments.Count == 0)
            {
                continue;
            }

            foreach (var comment in comments)
            {
                Log($"Cycle {cycle}:   comment {comment.Id} at {comment.Path}:{comment.Line?.ToString() ?? "-"}.");
            }

            await FixPullRequestAsync(cycle, configuration, fixOptions, client, attempts, pullRequest, comments, cancellationToken);
        }

        Log($"Cycle {cycle}: {total} actionable comment(s) across {open.Count} open pull request(s).");
    }

    private static async Task FixPullRequestAsync(
        long cycle,
        AgentConfiguration configuration,
        FixOptions fixOptions,
        GitHubReviewClient client,
        ReviewCommentAttemptRepository attempts,
        TrackedPullRequest pullRequest,
        IReadOnlyList<ReviewComment> comments,
        CancellationToken cancellationToken)
    {
        var (branch, headSha) = await client.GetHeadAsync(pullRequest, cancellationToken);
        if (comments.Any(comment => !string.Equals(comment.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: head moved while reading comments; retrying next cycle.");
            return;
        }

        var git = new ProcessGit(configuration.GitHubRepositoryToken);
        var workspacePath = ProcessGit.CreateWorkspacePath();
        try
        {
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: cloning branch '{branch}' at {Short(headSha)}.");
            await git.CloneAsync(pullRequest.RepositoryUrl, branch, workspacePath, cancellationToken);
            var revision = await git.GetRevisionAsync(workspacePath, cancellationToken);
            if (!string.Equals(revision, headSha, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Cycle {cycle}: PR #{pullRequest.Number}: branch is at {Short(revision)}, " +
                    $"expected {Short(headSha)}; retrying next cycle.");
                return;
            }

            using var runner = new SandboxRunnerClient(configuration.AgentGitHubToken, configuration.AgentModel);
            var flow = new ReviewFixFlow(runner, git, runner, attempts, fixOptions);
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: running fix attempt for {comments.Count} comment(s).");
            var result = await flow.RunAsync(pullRequest, branch, comments, workspacePath, cancellationToken);
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: fix attempt finished with {result.Outcome}" +
                (result.Error is null ? "." : $": {result.Error}"));
        }
        finally
        {
            try
            {
                ProcessGit.DeleteWorkspace(workspacePath);
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(DFLogLevel.WARNING, Category,
                    $"Cycle {cycle}: could not delete workspace '{workspacePath}': {exception.Message}");
            }
        }
    }

    private static void LogScan(long cycle, TrackedPullRequest pullRequest, string reviewerLogin, ReviewCommentScan scan)
    {
        var prefix = $"Cycle {cycle}: PR #{pullRequest.Number} ({pullRequest.JiraKey})";
        if (scan.OpenComments == 0)
        {
            Log($"{prefix} has no unresolved review comments.");
            return;
        }

        Log($"{prefix} has {scan.OpenComments} unresolved review comment(s); {scan.Actionable.Count} actionable " +
            $"from '{reviewerLogin}'" +
            (scan.HandledComments > 0 ? $", {scan.HandledComments} from '{reviewerLogin}' already handled." : "."));
        if (scan.OtherAuthors.Count > 0)
        {
            var authors = string.Join(", ", scan.OtherAuthors.Select(author => $"{author.Key} ({author.Value})"));
            Log($"{prefix}: ignoring comment(s) not from '{reviewerLogin}'. Reviewers found: {authors}.");
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static void Log(string message) => DFLogger.LogOutput(DFLogLevel.INFO, Category, message);
}
