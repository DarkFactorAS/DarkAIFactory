using DFCommonLib.Logger;
using Microsoft.Extensions.DependencyInjection;
using ReviewWorker.Repository;

namespace ReviewWorker;

/// <summary>
/// Read-only scan run every polling cycle. Logs which tracked pull requests are open and how many
/// review comments are actionable, so it is visible what the worker would pick up.
/// </summary>
internal static class ReviewCycleScanner
{
    private const string Category = "Worker";

    public static async Task ScanAsync(
        long cycle,
        AgentConfiguration configuration,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var pullRequests = services.GetRequiredService<PullRequestReader>();
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
            services.GetRequiredService<ReviewCommentAttemptRepository>());

        var open = await client.ListOpenPullRequestsAsync(configuration.GitHubRepositoryUrl, cancellationToken);
        Log($"Cycle {cycle}: {open.Count} of {tracked.Count} tracked pull request(s) are open on GitHub.");

        var total = 0;
        foreach (var pullRequest in open)
        {
            var comments = await client.GetActionableCommentsAsync(pullRequest, cancellationToken);
            total += comments.Count;
            Log($"Cycle {cycle}: PR #{pullRequest.Number} ({pullRequest.JiraKey}) has {comments.Count} actionable " +
                $"comment(s) from '{configuration.ReviewerLogin}'.");
            foreach (var comment in comments)
            {
                Log($"Cycle {cycle}:   comment {comment.Id} at {comment.Path}:{comment.Line?.ToString() ?? "-"}.");
            }
        }

        Log($"Cycle {cycle}: {total} actionable comment(s) across {open.Count} open pull request(s).");
    }

    private static void Log(string message) => DFLogger.LogOutput(DFLogLevel.INFO, Category, message);
}
