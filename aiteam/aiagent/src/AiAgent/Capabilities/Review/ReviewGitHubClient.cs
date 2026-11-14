using System.Globalization;
using AiAgent.Repositories;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiAgent.Capabilities.Review;

/// <summary>
/// Reads inline review comments through the GitHub GraphQL API (the REST API does not expose
/// resolved state). Threads are paged with cursors.
/// </summary>
public sealed class ReviewGitHubClient : IReviewThreadResolver, IDisposable
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
    private const string ResolveThreadMutation =
        "mutation($threadId: ID!) { resolveReviewThread(input: {threadId: $threadId}) { thread { id isResolved } } }";

    private readonly HttpClient _httpClient;
    private readonly IReviewAttemptStore _attempts;
    private readonly string _reviewerLogin;
    private readonly int _maxAttempts;

    public ReviewGitHubClient(
        string token,
        string reviewerLogin,
        int maxAttempts,
        IReviewAttemptStore attempts,
        HttpMessageHandler? handler = null,
        Uri? apiUrl = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = apiUrl ?? new Uri("https://api.github.com/");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-AiAgent/1.0");
        _reviewerLogin = reviewerLogin;
        _maxAttempts = maxAttempts;
        _attempts = attempts;
    }

    /// <summary>The given agent pull requests that are still open on GitHub.</summary>
    public async Task<IReadOnlyList<StoredAgentPullRequest>> ListOpenPullRequestsAsync(
        IEnumerable<StoredAgentPullRequest> pullRequests, CancellationToken cancellationToken)
    {
        var open = new List<StoredAgentPullRequest>();
        foreach (var pullRequest in pullRequests)
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
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var page = await FetchThreadsPageAsync(pullRequest, null, cancellationToken);
        return (page.HeadBranch, page.HeadSha);
    }

    /// <summary>
    /// Comments by the reviewer that are not resolved, not outdated and not already handled.
    /// Returns nothing when the pull request is not open.
    /// </summary>
    public async Task<IReadOnlyList<ReviewComment>> GetActionableCommentsAsync(
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken) =>
        (await ScanCommentsAsync(pullRequest, cancellationToken)).Actionable;

    /// <summary>
    /// Actionable comments plus counts of the other unresolved, current comments, for diagnostics.
    /// Returns an empty scan when the pull request is not open.
    /// </summary>
    public async Task<ReviewCommentScan> ScanCommentsAsync(
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var handled = _attempts.GetHandledComments(pullRequest.Id, _maxAttempts);
        var result = new List<ReviewComment>();
        var threadMembers = new List<ReviewThreadMember>();
        var otherAuthors = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var openComments = 0;
        var handledComments = 0;
        var failedComments = 0;
        string? cursor = null;
        while (true)
        {
            var page = await FetchThreadsPageAsync(pullRequest, cursor, cancellationToken);
            if (!string.Equals(page.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return ReviewCommentScan.Empty;
            }

            foreach (var thread in page.Threads)
            {
                if (thread.IsResolved || thread.IsOutdated)
                {
                    continue;
                }

                AddActionableComments(thread.Comments, page.HeadSha, thread.Id);
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
                    AddActionableComments(commentPage.Comments, page.HeadSha, thread.Id);
                    hasNextCommentPage = commentPage.HasNextPage;
                    commentCursor = commentPage.EndCursor;
                }
            }

            if (!page.HasNextPage || page.EndCursor is null)
            {
                return new ReviewCommentScan(result, openComments, handledComments, failedComments, otherAuthors)
                {
                    ThreadMembers = threadMembers
                };
            }

            cursor = page.EndCursor;
        }

        void AddActionableComments(IEnumerable<CommentInfo> comments, string headSha, string threadId)
        {
            foreach (var comment in comments)
            {
                openComments++;
                var status = "Actionable";
                if (!string.Equals(comment.Author, _reviewerLogin, StringComparison.OrdinalIgnoreCase))
                {
                    var author = string.IsNullOrEmpty(comment.Author) ? "(unknown)" : comment.Author;
                    otherAuthors[author] = otherAuthors.GetValueOrDefault(author) + 1;
                    status = "OtherAuthor";
                }
                else if (handled.TryGetValue(comment.Id, out var handledStatus))
                {
                    if (string.Equals(handledStatus, "Resolved", StringComparison.OrdinalIgnoreCase))
                    {
                        handledComments++;
                        status = "Resolved";
                    }
                    else
                    {
                        failedComments++;
                        status = "Failed";
                    }
                }
                else
                {
                    result.Add(new ReviewComment(
                        comment.Id, pullRequest.Id, pullRequest.PullRequest.PullRequestNumber, comment.Path, comment.Line,
                        comment.Body, comment.Author, headSha, threadId));
                }

                threadMembers.Add(new ReviewThreadMember(comment.Id, threadId, status));
            }
        }
    }

    /// <summary>Resolves a review thread on GitHub (needs a token allowed to resolve conversations).</summary>
    public async Task ResolveThreadAsync(
        StoredAgentPullRequest pullRequest, string threadId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["query"] = ResolveThreadMutation,
            ["variables"] = new JsonObject { ["threadId"] = threadId }
        };
        await SendGraphQlAsync(body, pullRequest.PullRequest.PullRequestUrl, cancellationToken);
    }

    /// <summary>
    /// Counts every unresolved review thread, including outdated ones: readiness to merge must not ignore
    /// outdated threads even though they are not actionable for automatic fixes. Returns null when the pull
    /// request is no longer open, since a closed or merged pull request must never be reported as ready.
    /// </summary>
    public async Task<int?> CountUnresolvedThreadsAsync(
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var unresolved = 0;
        string? cursor = null;
        while (true)
        {
            var page = await FetchThreadsPageAsync(pullRequest, cursor, cancellationToken);
            if (!string.Equals(page.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            unresolved += page.Threads.Count(thread => !thread.IsResolved);
            if (!page.HasNextPage || page.EndCursor is null)
            {
                return unresolved;
            }

            cursor = page.EndCursor;
        }
    }

    /// <summary>Total and unresolved review threads; null when the pull request is no longer open.</summary>
    public async Task<(int Total, int Unresolved)?> CountThreadsAsync(
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        int total = 0, unresolved = 0;
        string? cursor = null;
        while (true)
        {
            var page = await FetchThreadsPageAsync(pullRequest, cursor, cancellationToken);
            if (!string.Equals(page.State, "OPEN", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            total += page.Threads.Count;
            unresolved += page.Threads.Count(thread => !thread.IsResolved);
            if (!page.HasNextPage || page.EndCursor is null)
            {
                return (total, unresolved);
            }

            cursor = page.EndCursor;
        }
    }

    /// <summary>Draft flag, mergeable state, head and every changed file of the pull request (REST).</summary>
    public async Task<PullRequestMergeInfo> GetMergeInfoAsync(
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var path = RestPath(pullRequest);
        var node = await SendRestAsync(HttpMethod.Get, path, null, cancellationToken)
            ?? throw new InvalidDataException($"Pull request {pullRequest.PullRequest.PullRequestUrl} not found.");
        var files = new List<string>();
        for (var page = 1; ; page++)
        {
            var list = (await SendRestAsync(HttpMethod.Get, $"{path}/files?per_page={PageSize}&page={page}", null, cancellationToken))
                ?.AsArray() ?? [];
            foreach (var file in list)
            {
                files.Add(file?["filename"]?.GetValue<string>() ?? string.Empty);
                if (file?["previous_filename"]?.GetValue<string>() is { Length: > 0 } previous)
                {
                    files.Add(previous);
                }
            }

            if (list.Count < PageSize)
            {
                break;
            }
        }

        return new PullRequestMergeInfo(
            node["draft"]?.GetValue<bool>() ?? false,
            node["mergeable_state"]?.GetValue<string>() ?? "unknown",
            node["head"]?["sha"]?.GetValue<string>() ?? string.Empty,
            node["head"]?["ref"]?.GetValue<string>() ?? string.Empty,
            files);
    }

    /// <summary>
    /// Merges the pull request, pinned to the expected head commit so it fails if commits were pushed since the
    /// check. GitHub enforces branch protection; it is never bypassed.
    /// </summary>
    public async Task MergeAsync(
        StoredAgentPullRequest pullRequest, string headSha, string mergeMethod, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["sha"] = headSha, ["merge_method"] = mergeMethod };
        await SendRestAsync(HttpMethod.Put, $"{RestPath(pullRequest)}/merge", body, cancellationToken);
    }

    private static string RestPath(StoredAgentPullRequest pullRequest)
    {
        var segments = new Uri(pullRequest.PullRequest.RepositoryUrl).AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            throw new InvalidDataException($"Cannot read owner and repository from \"{pullRequest.PullRequest.RepositoryUrl}\".");
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return $"repos/{segments[0]}/{name}/pulls/{pullRequest.PullRequest.PullRequestNumber}";
    }

    private async Task<JsonNode?> SendRestAsync(
        HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub request {method} {path} failed with {(int)response.StatusCode}.");
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<ThreadsPage> FetchThreadsPageAsync(
        StoredAgentPullRequest pullRequest, string? cursor, CancellationToken cancellationToken)
    {
        var segments = new Uri(pullRequest.PullRequest.RepositoryUrl).AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            throw new InvalidDataException($"Cannot read owner and repository from \"{pullRequest.PullRequest.RepositoryUrl}\".");
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        var body = new JsonObject
        {
            ["query"] = PullRequestQuery,
            ["variables"] = new JsonObject
            {
                ["owner"] = segments[0],
                ["name"] = name,
                ["number"] = pullRequest.PullRequest.PullRequestNumber,
                ["cursor"] = cursor
            }
        };
        var root = await SendGraphQlAsync(body, pullRequest.PullRequest.PullRequestUrl, cancellationToken);
        var node = root["data"]?["repository"]?["pullRequest"]
            ?? throw new InvalidDataException($"Pull request {pullRequest.PullRequest.PullRequestUrl} not found.");
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
        StoredAgentPullRequest pullRequest, string threadId, string cursor, CancellationToken cancellationToken)
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
        var root = await SendGraphQlAsync(body, pullRequest.PullRequest.PullRequestUrl, cancellationToken);
        var thread = root["data"]?["node"]
            ?? throw new InvalidDataException($"Review thread for pull request {pullRequest.PullRequest.PullRequestUrl} not found.");
        var comments = thread["comments"]
            ?? throw new InvalidDataException($"Review thread for pull request {pullRequest.PullRequest.PullRequestUrl} not found.");
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
