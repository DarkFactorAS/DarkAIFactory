using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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
    private const string UnresolvedThreadsQuery =
        """
        query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              state
              reviewThreads(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes { isResolved }
              }
            }
          }
        }
        """;

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
                var unresolved = await CountUnresolvedThreadsAsync(
                    configuration.GitHubRepositoryToken, pullRequest, cancellationToken);
                if (unresolved is { } count && IsReadyToMerge(count, 0))
                {
                    LogReadyToMerge(cycle, pullRequest);
                }

                continue;
            }

            foreach (var comment in comments)
            {
                Log($"Cycle {cycle}:   comment {comment.Id} at {comment.Path}:{comment.Line?.ToString() ?? "-"}.");
            }

            var failedCount = await FixPullRequestAsync(cycle, configuration, fixOptions, client, attempts, pullRequest, comments, cancellationToken);
            if (failedCount == 0)
            {
                var unresolved = await CountUnresolvedThreadsAsync(
                    configuration.GitHubRepositoryToken, pullRequest, cancellationToken);
                if (unresolved is { } count && IsReadyToMerge(count, 0))
                {
                    LogReadyToMerge(cycle, pullRequest);
                }
            }
        }

        Log($"Cycle {cycle}: {total} actionable comment(s) across {open.Count} open pull request(s).");
    }

    /// <summary>A pull request is ready to merge when no review thread is unresolved and no fix failed.</summary>
    internal static bool IsReadyToMerge(int openComments, int failedCount) =>
        openComments == 0 && failedCount == 0;

    /// <summary>
    /// Counts every unresolved review thread, including outdated ones. Readiness must not ignore
    /// outdated threads, even though they are not actionable for automatic fixes.
    /// Returns null when the pull request is no longer open, since a closed or merged pull request
    /// must never be reported as ready to merge.
    /// </summary>
    private static async Task<int?> CountUnresolvedThreadsAsync(
        string token, TrackedPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var segments = pullRequest.RepositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            throw new InvalidDataException($"Cannot read owner and repository from \"{pullRequest.RepositoryUrl}\".");
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ai-review-worker");

        var unresolved = 0;
        string? cursor = null;
        while (true)
        {
            var body = new JsonObject
            {
                ["query"] = UnresolvedThreadsQuery,
                ["variables"] = new JsonObject
                {
                    ["owner"] = segments[0],
                    ["name"] = name,
                    ["number"] = pullRequest.Number,
                    ["cursor"] = cursor
                }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub GraphQL request failed with {(int)response.StatusCode} for pull request {pullRequest.Url}.");
            }

            var root = JsonNode.Parse(text) ?? throw new InvalidDataException("Empty GitHub response.");
            if (root["errors"] is JsonArray { Count: > 0 } errors)
            {
                throw new InvalidOperationException(
                    $"GitHub GraphQL returned errors for {pullRequest.Url}: {errors[0]?["message"]}");
            }

            var pullRequestNode = root["data"]?["repository"]?["pullRequest"]
                ?? throw new InvalidDataException($"Pull request {pullRequest.Url} not found.");
            var state = pullRequestNode["state"]?.GetValue<string>();
            if (!string.Equals(state, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var threads = pullRequestNode["reviewThreads"]
                ?? throw new InvalidDataException($"Pull request {pullRequest.Url} not found.");
            unresolved += (threads["nodes"]?.AsArray() ?? [])
                .Count(thread => !(thread?["isResolved"]?.GetValue<bool>() ?? false));
            var hasNextPage = threads["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false;
            cursor = threads["pageInfo"]?["endCursor"]?.GetValue<string>();
            if (!hasNextPage || cursor is null)
            {
                return unresolved;
            }
        }
    }

    private static void LogReadyToMerge(long cycle, TrackedPullRequest pullRequest) =>
        Log($"Cycle {cycle}: PR #{pullRequest.Number} ({pullRequest.JiraKey}) is ready to be merged: " +
            "all review comments are resolved.");

    /// <summary>Returns the failed count, or null when the run did not complete (head moved, branch mismatch, abandoned).</summary>
    private static async Task<int?> FixPullRequestAsync(
        long cycle,
        AgentConfiguration configuration,
        FixOptions fixOptions,
        GitHubReviewClient client,
        ReviewCommentAttemptRepository attempts,
        TrackedPullRequest pullRequest,
        IReadOnlyList<ReviewComment> comments,
        CancellationToken cancellationToken)
    {
        int? completedFailedCount = null;
        var (branch, headSha) = await client.GetHeadAsync(pullRequest, cancellationToken);
        if (comments.Any(comment => !string.Equals(comment.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: head moved while reading comments; retrying next cycle.");
            return null;
        }

        var git = new ProcessGit(configuration.GitHubRepositoryToken);
        var workspacePath = ProcessGit.CreateWorkspacePath();
        var workspaceAbandoned = false;
        try
        {
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: cloning branch '{branch}' at {Short(headSha)}.");
            await git.CloneAsync(pullRequest.RepositoryUrl, branch, workspacePath, cancellationToken);
            var revision = await git.GetRevisionAsync(workspacePath, cancellationToken);
            if (!string.Equals(revision, headSha, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Cycle {cycle}: PR #{pullRequest.Number}: branch is at {Short(revision)}, " +
                    $"expected {Short(headSha)}; retrying next cycle.");
                return null;
            }

            using var runner = new SandboxRunnerClient(configuration.AgentModel, configuration.RunnerTimeout);
            var flow = new ReviewFixFlow(runner, git, runner, attempts, client, fixOptions);
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: fixing {comments.Count} comment(s) one at a time.");
            var result = await flow.RunAsync(pullRequest, branch, comments, workspacePath, cancellationToken);
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: fix run finished with {result.Outcome}; " +
                $"{result.PushedCount} pushed, {result.FailedCount} failed" +
                (result.Error is null ? "." : $". Last error: {result.Error}"));
            completedFailedCount = result.FailedCount;
        }
        catch (SandboxWorkspaceAbandonedException exception)
        {
            workspaceAbandoned = true;
            Log($"Cycle {cycle}: PR #{pullRequest.Number}: leaving workspace '{workspacePath}' untouched because " +
                $"sandbox process termination was not confirmed. {exception.Message}");
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            if (!workspaceAbandoned)
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

        return completedFailedCount;
    }

    private static void LogScan(long cycle, TrackedPullRequest pullRequest, string reviewerLogin, ReviewCommentScan scan)
    {
        var prefix = $"Cycle {cycle}: PR #{pullRequest.Number} ({pullRequest.JiraKey})";
        if (scan.OpenComments == 0)
        {
            Log($"{prefix} has no unresolved review comments.");
            return;
        }

        var message = $"{prefix} has {scan.OpenComments} unresolved review comment(s); {scan.Actionable.Count} actionable " +
            $"from '{reviewerLogin}'" +
            (scan.HandledComments > 0 ? $", {scan.HandledComments} from '{reviewerLogin}' already handled" : string.Empty) +
            (scan.FailedComments > 0
                ? $", {scan.FailedComments} from '{reviewerLogin}' marked failed (terminal failure or attempts exhausted; see review_comment_attempts)"
                : string.Empty) + ".";
        DFLogger.LogOutput(scan.FailedComments > 0 ? DFLogLevel.WARNING : DFLogLevel.INFO, Category, message);
        if (scan.OtherAuthors.Count > 0)
        {
            var authors = string.Join(", ", scan.OtherAuthors.Select(author => $"{author.Key} ({author.Value})"));
            Log($"{prefix}: ignoring comment(s) not from '{reviewerLogin}'. Reviewers found: {authors}.");
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static void Log(string message) => DFLogger.LogOutput(DFLogLevel.INFO, Category, message);
}
