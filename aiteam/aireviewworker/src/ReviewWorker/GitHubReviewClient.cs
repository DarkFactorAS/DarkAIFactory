using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReviewWorker.Repository;

namespace ReviewWorker;

/// <summary>
/// Reads inline review comments through the GitHub GraphQL API (the REST API does not expose
/// resolved state). Threads are paged with cursors.
/// </summary>
internal sealed class GitHubReviewClient : IDisposable
{
    public const int PageSize = 100;
    private const string Query =
        """
        query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              state
              headRefOid
              reviewThreads(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  isResolved
                  isOutdated
                  comments(first: 1) {
                    nodes { databaseId body path line author { login } }
                  }
                }
              }
            }
          }
        }
        """;

    private readonly HttpClient _httpClient;
    private readonly PullRequestReader _pullRequests;
    private readonly ReviewCommentAttemptRepository _attempts;
    private readonly string _reviewerLogin;
    private readonly int _maxAttempts;

    public GitHubReviewClient(
        string token,
        string reviewerLogin,
        int maxAttempts,
        PullRequestReader pullRequests,
        ReviewCommentAttemptRepository attempts,
        HttpMessageHandler? handler = null,
        Uri? apiUrl = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = apiUrl ?? new Uri("https://api.github.com/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ai-review-worker");
        _reviewerLogin = reviewerLogin;
        _maxAttempts = maxAttempts;
        _pullRequests = pullRequests;
        _attempts = attempts;
    }

    /// <summary>Pull requests recorded in pull_requests for the repository that are still open on GitHub.</summary>
    public async Task<IReadOnlyList<TrackedPullRequest>> ListOpenPullRequestsAsync(
        Uri repositoryUrl, CancellationToken cancellationToken)
    {
        var open = new List<TrackedPullRequest>();
        foreach (var pullRequest in _pullRequests.List(repositoryUrl))
        {
            var page = await FetchThreadsPageAsync(pullRequest, null, cancellationToken);
            if (string.Equals(page.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                open.Add(pullRequest);
            }
        }

        return open;
    }

    /// <summary>
    /// Comments by the reviewer that are not resolved, not outdated and not already handled.
    /// Returns nothing when the pull request is not open.
    /// </summary>
    public async Task<IReadOnlyList<ReviewComment>> GetActionableCommentsAsync(
        TrackedPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var handled = _attempts.GetHandledCommentIds(pullRequest.Id, _maxAttempts);
        var result = new List<ReviewComment>();
        string? cursor = null;
        while (true)
        {
            var page = await FetchThreadsPageAsync(pullRequest, cursor, cancellationToken);
            if (!string.Equals(page.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            foreach (var thread in page.Threads)
            {
                if (thread.IsResolved || thread.IsOutdated || thread.Comment is not { } comment ||
                    !string.Equals(comment.Author, _reviewerLogin, StringComparison.OrdinalIgnoreCase) ||
                    handled.Contains(comment.Id))
                {
                    continue;
                }

                result.Add(new ReviewComment(
                    comment.Id, pullRequest.Id, pullRequest.Number, comment.Path, comment.Line,
                    comment.Body, comment.Author, page.HeadSha));
            }

            if (!page.HasNextPage || page.EndCursor is null)
            {
                return result;
            }

            cursor = page.EndCursor;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<ThreadsPage> FetchThreadsPageAsync(
        TrackedPullRequest pullRequest, string? cursor, CancellationToken cancellationToken)
    {
        var segments = pullRequest.RepositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            throw new InvalidDataException($"Cannot read owner and repository from \"{pullRequest.RepositoryUrl}\".");
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        var body = new JsonObject
        {
            ["query"] = Query,
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
        using var response = await _httpClient.SendAsync(request, cancellationToken);
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

        var node = root["data"]?["repository"]?["pullRequest"]
            ?? throw new InvalidDataException($"Pull request {pullRequest.Url} not found.");
        var threads = node["reviewThreads"];
        var list = new List<ThreadInfo>();
        foreach (var thread in threads?["nodes"]?.AsArray() ?? [])
        {
            var first = thread?["comments"]?["nodes"]?.AsArray().FirstOrDefault();
            CommentInfo? comment = first is null || first["databaseId"] is null
                ? null
                : new CommentInfo(
                    first["databaseId"]!.GetValue<long>(),
                    first["path"]?.GetValue<string>() ?? string.Empty,
                    first["line"]?.GetValue<int>(),
                    first["body"]?.GetValue<string>() ?? string.Empty,
                    first["author"]?["login"]?.GetValue<string>() ?? string.Empty);
            list.Add(new ThreadInfo(
                thread?["isResolved"]?.GetValue<bool>() ?? false,
                thread?["isOutdated"]?.GetValue<bool>() ?? false,
                comment));
        }

        return new ThreadsPage(
            node["state"]?.GetValue<string>() ?? string.Empty,
            node["headRefOid"]?.GetValue<string>() ?? string.Empty,
            list,
            threads?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false,
            threads?["pageInfo"]?["endCursor"]?.GetValue<string>());
    }

    private sealed record CommentInfo(long Id, string Path, int? Line, string Body, string Author);
    private sealed record ThreadInfo(bool IsResolved, bool IsOutdated, CommentInfo? Comment);
    private sealed record ThreadsPage(
        string State, string HeadSha, List<ThreadInfo> Threads, bool HasNextPage, string? EndCursor);
}
