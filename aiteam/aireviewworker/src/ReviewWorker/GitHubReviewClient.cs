using System.Globalization;
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
    private const string PullRequestQuery =
        """
        query($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              state
              headRefName
              headRefOid
              reviewThreads(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  line
                  isResolved
                  isOutdated
                  comments(first: 100) {
                    pageInfo { hasNextPage endCursor }
                    nodes { fullDatabaseId body path author { login } }
                  }
                }
              }
            }
          }
        }
        """;
    private const string ThreadCommentsQuery =
        """
        query($threadId: ID!, $cursor: String) {
          node(id: $threadId) {
            ... on PullRequestReviewThread {
              line
              comments(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes { fullDatabaseId body path author { login } }
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

    /// <summary>Head branch name and commit of the pull request.</summary>
    public async Task<(string Branch, string Sha)> GetHeadAsync(
        TrackedPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var page = await FetchThreadsPageAsync(pullRequest, null, cancellationToken);
        return (page.HeadBranch, page.HeadSha);
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
                if (thread.IsResolved || thread.IsOutdated)
                {
                    continue;
                }

                AddActionableComments(thread.Comments, page.HeadSha);
                var hasNextCommentPage = thread.HasNextCommentPage;
                var commentCursor = thread.CommentEndCursor;
                while (hasNextCommentPage)
                {
                    if (string.IsNullOrEmpty(thread.Id) || commentCursor is null)
                    {
                        throw new InvalidDataException("GitHub returned an incomplete review-thread comment page.");
                    }

                    var commentPage = await FetchThreadCommentsPageAsync(
                        pullRequest, thread.Id, commentCursor, cancellationToken);
                    AddActionableComments(commentPage.Comments, page.HeadSha);
                    hasNextCommentPage = commentPage.HasNextPage;
                    commentCursor = commentPage.EndCursor;
                }
            }

            if (!page.HasNextPage || page.EndCursor is null)
            {
                return result;
            }

            cursor = page.EndCursor;
        }

        void AddActionableComments(IEnumerable<CommentInfo> comments, string headSha)
        {
            foreach (var comment in comments)
            {
                if (string.Equals(comment.Author, _reviewerLogin, StringComparison.OrdinalIgnoreCase) &&
                    !handled.Contains(comment.Id))
                {
                    result.Add(new ReviewComment(
                        comment.Id, pullRequest.Id, pullRequest.Number, comment.Path, comment.Line,
                        comment.Body, comment.Author, headSha));
                }
            }
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
            ["query"] = PullRequestQuery,
            ["variables"] = new JsonObject
            {
                ["owner"] = segments[0],
                ["name"] = name,
                ["number"] = pullRequest.Number,
                ["cursor"] = cursor
            }
        };
        var root = await SendGraphQlAsync(body, pullRequest.Url, cancellationToken);
        var node = root["data"]?["repository"]?["pullRequest"]
            ?? throw new InvalidDataException($"Pull request {pullRequest.Url} not found.");
        var threads = node["reviewThreads"];
        var list = new List<ThreadInfo>();
        foreach (var thread in threads?["nodes"]?.AsArray() ?? [])
        {
            var comments = thread?["comments"];
            var commentList = (comments?["nodes"]?.AsArray() ?? [])
                .Select(comment => ParseComment(comment, thread?["line"]?.GetValue<int>()))
                .OfType<CommentInfo>()
                .ToList();
            list.Add(new ThreadInfo(
                thread?["id"]?.GetValue<string>() ?? string.Empty,
                thread?["isResolved"]?.GetValue<bool>() ?? false,
                thread?["isOutdated"]?.GetValue<bool>() ?? false,
                commentList,
                comments?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false,
                comments?["pageInfo"]?["endCursor"]?.GetValue<string>()));
        }

        return new ThreadsPage(
            node["state"]?.GetValue<string>() ?? string.Empty,
            node["headRefOid"]?.GetValue<string>() ?? string.Empty,
            node["headRefName"]?.GetValue<string>() ?? string.Empty,
            list,
            threads?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false,
            threads?["pageInfo"]?["endCursor"]?.GetValue<string>());
    }

    private async Task<CommentsPage> FetchThreadCommentsPageAsync(
        TrackedPullRequest pullRequest, string threadId, string cursor, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["query"] = ThreadCommentsQuery,
            ["variables"] = new JsonObject
            {
                ["threadId"] = threadId,
                ["cursor"] = cursor
            }
        };
        var root = await SendGraphQlAsync(body, pullRequest.Url, cancellationToken);
        var thread = root["data"]?["node"]
            ?? throw new InvalidDataException($"Review thread for pull request {pullRequest.Url} not found.");
        var comments = thread["comments"]
            ?? throw new InvalidDataException($"Review thread for pull request {pullRequest.Url} not found.");
        var line = thread["line"]?.GetValue<int>();
        var commentList = (comments["nodes"]?.AsArray() ?? [])
            .Select(comment => ParseComment(comment, line))
            .OfType<CommentInfo>()
            .ToList();

        return new CommentsPage(
            commentList,
            comments["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false,
            comments["pageInfo"]?["endCursor"]?.GetValue<string>());
    }

    private async Task<JsonNode> SendGraphQlAsync(
        JsonObject body, string pullRequestUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub GraphQL request failed with {(int)response.StatusCode} for pull request {pullRequestUrl}.");
        }

        var root = JsonNode.Parse(text) ?? throw new InvalidDataException("Empty GitHub response.");
        if (root["errors"] is JsonArray { Count: > 0 } errors)
        {
            throw new InvalidOperationException(
                $"GitHub GraphQL returned errors for {pullRequestUrl}: {errors[0]?["message"]}");
        }

        return root;
    }

    private static CommentInfo? ParseComment(JsonNode? comment, int? line)
    {
        var fullDatabaseId = comment?["fullDatabaseId"]?.ToString();
        if (!long.TryParse(fullDatabaseId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        return new CommentInfo(
            id,
            comment?["path"]?.GetValue<string>() ?? string.Empty,
            line,
            comment?["body"]?.GetValue<string>() ?? string.Empty,
            comment?["author"]?["login"]?.GetValue<string>() ?? string.Empty);
    }

    private sealed record CommentInfo(long Id, string Path, int? Line, string Body, string Author);
    private sealed record ThreadInfo(
        string Id, bool IsResolved, bool IsOutdated, List<CommentInfo> Comments,
        bool HasNextCommentPage, string? CommentEndCursor);
    private sealed record CommentsPage(List<CommentInfo> Comments, bool HasNextPage, string? EndCursor);
    private sealed record ThreadsPage(
        string State, string HeadSha, string HeadBranch, List<ThreadInfo> Threads, bool HasNextPage, string? EndCursor);
}
