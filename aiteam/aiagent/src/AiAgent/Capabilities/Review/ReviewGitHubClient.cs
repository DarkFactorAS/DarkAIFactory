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
public sealed class ReviewGitHubClient : IReviewThreadResolver, IMergeRequestGitHub, IDisposable
{
    public const int PageSize = 100;

    /// <summary>GitHub's pull-request files endpoint returns at most this many files.</summary>
    public const int MaxListedFiles = 3000;

    /// <summary>The compare endpoint returns at most this many files; a full list may be truncated.</summary>
    private const int MaxCompareFiles = 300;

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
    private const string ReplyToThreadMutation =
        "mutation($threadId: ID!, $body: String!) { addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $threadId, body: $body}) { comment { id } } }";

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

    /// <summary>Posts a reply in a review thread (needs Pull requests: write).</summary>
    public async Task ReplyToThreadAsync(
        StoredAgentPullRequest pullRequest, string threadId, string body, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["query"] = ReplyToThreadMutation,
            ["variables"] = new JsonObject { ["threadId"] = threadId, ["body"] = body }
        };
        await SendGraphQlAsync(request, pullRequest.PullRequest.PullRequestUrl, cancellationToken);
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
        StoredAgentPullRequest pullRequest, string approvalPhrase, CancellationToken cancellationToken)
    {
        var path = RestPath(pullRequest);
        var node = await SendRestAsync(HttpMethod.Get, path, null, cancellationToken)
            ?? throw new InvalidDataException($"Pull request {pullRequest.PullRequest.PullRequestUrl} not found.");

        // The files endpoint lists at most MaxListedFiles files, so a larger pull request cannot be fully
        // checked; fail closed instead of evaluating a truncated file list.
        var changedFiles = node["changed_files"]?.GetValue<int>()
            ?? throw new InvalidDataException($"Pull request {pullRequest.PullRequest.PullRequestUrl} does not report changed_files.");
        if (changedFiles > MaxListedFiles)
        {
            throw new InvalidDataException(
                $"Pull request {pullRequest.PullRequest.PullRequestUrl} changes {changedFiles} files; GitHub lists at most {MaxListedFiles}, so not every changed file can be checked.");
        }

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

        var headSha = node["head"]?["sha"]?.GetValue<string>() ?? string.Empty;
        var baseBranch = node["base"]?["ref"]?.GetValue<string>() ?? string.Empty;
        var repoPath = path[..path.IndexOf("/pulls/", StringComparison.Ordinal)];
        var headRef = node["head"]?["ref"]?.GetValue<string>() ?? string.Empty;

        var compare = await SendRestAsync(
            HttpMethod.Get,
            $"{repoPath}/compare/{Uri.EscapeDataString(baseBranch)}...{Uri.EscapeDataString(headSha)}", null, cancellationToken);
        var behindBy = compare?["behind_by"]?.GetValue<int>() ?? 0;

        var checks = await GetChecksProblemAsync(repoPath, headSha, cancellationToken);
        return new PullRequestMergeInfo(
            node["draft"]?.GetValue<bool>() ?? false,
            node["mergeable_state"]?.GetValue<string>() ?? "unknown",
            headSha,
            headRef,
            files)
        {
            BaseBranch = baseBranch,
            BehindBy = behindBy,
            ChecksProblem = checks.Problem,
            ChecksPending = checks.Pending,
            ReviewerApproved = await HasReviewerApprovalAsync(path, repoPath, baseBranch, headSha, approvalPhrase, cancellationToken)
        };
    }

    /// <summary>
    /// True when the reviewer's approval phrase applies to the current head of the pull request. Cheaper than
    /// <see cref="GetMergeInfoAsync"/>: it does not load files, checks or the compare data.
    /// </summary>
    public async Task<bool> HasReviewerApprovalAsync(
        StoredAgentPullRequest pullRequest, string approvalPhrase, CancellationToken cancellationToken)
    {
        var path = RestPath(pullRequest);
        var node = await SendRestAsync(HttpMethod.Get, path, null, cancellationToken)
            ?? throw new InvalidDataException($"Pull request {pullRequest.PullRequest.PullRequestUrl} not found.");
        var headSha = node["head"]?["sha"]?.GetValue<string>() ?? string.Empty;
        var baseBranch = node["base"]?["ref"]?.GetValue<string>() ?? string.Empty;
        var repoPath = path[..path.IndexOf("/pulls/", StringComparison.Ordinal)];
        return await HasReviewerApprovalAsync(path, repoPath, baseBranch, headSha, approvalPhrase, cancellationToken);
    }

    /// <summary>Asks GitHub to merge the base branch into the head branch (no force push), pinned to the head.</summary>
    public async Task UpdateBranchAsync(
        StoredAgentPullRequest pullRequest, string headSha, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["expected_head_sha"] = headSha };
        await SendRestAsync(HttpMethod.Put, $"{RestPath(pullRequest)}/update-branch", body, cancellationToken);
    }

    /// <summary>
    /// Null when every check run is completed with success, skipped or neutral and the combined commit status is
    /// not failing or pending; otherwise a description.
    /// </summary>
    private async Task<(string? Problem, bool Pending)> GetChecksProblemAsync(string repoPath, string headSha, CancellationToken cancellationToken)
    {
        var runs = await SendRestAsync(HttpMethod.Get, $"{repoPath}/commits/{headSha}/check-runs?per_page={PageSize}", null, cancellationToken);
        var total = runs?["total_count"]?.GetValue<int>() ?? 0;
        var list = runs?["check_runs"]?.AsArray() ?? [];
        if (total > list.Count)
        {
            return ("too many check runs to verify", false);
        }

        foreach (var run in list)
        {
            var name = run?["name"]?.GetValue<string>() ?? "check";
            if (!string.Equals(run?["status"]?.GetValue<string>(), "completed", StringComparison.OrdinalIgnoreCase))
            {
                return ($"check '{name}' is not finished", true);
            }

            var conclusion = run?["conclusion"]?.GetValue<string>() ?? string.Empty;
            if (conclusion is not ("success" or "skipped" or "neutral"))
            {
                return ($"check '{name}' concluded '{conclusion}'", false);
            }
        }

        var status = await SendRestAsync(HttpMethod.Get, $"{repoPath}/commits/{headSha}/status", null, cancellationToken);
        var state = status?["state"]?.GetValue<string>() ?? "success";
        var statusCount = status?["total_count"]?.GetValue<int>() ?? 0;
        return statusCount > 0 && !string.Equals(state, "success", StringComparison.OrdinalIgnoreCase)
            ? ($"commit status is '{state}'", string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase))
            : (null, false);
    }

    /// <summary>
    /// REST endpoints report GitHub App logins with a "[bot]" suffix (for example
    /// "copilot-pull-request-reviewer[bot]") while GraphQL omits it, so strip the suffix before comparing.
    /// </summary>
    private static string? NormalizeBotLogin(string? login)
    {
        const string botSuffix = "[bot]";
        return login is not null && login.EndsWith(botSuffix, StringComparison.OrdinalIgnoreCase)
            ? login[..^botSuffix.Length]
            : login;
    }

    /// <summary>
    /// True when a submitted review by the reviewer contains the approval phrase and was made on the latest commit
    /// that is not a verified base-only merge, or on a later commit. Conversation comments never count: GitHub does
    /// not bind them to a head commit, and commit timestamps are author-controlled, so a commit created before the
    /// comment but pushed afterwards could otherwise pass unreviewed. A merge commit preserves an earlier
    /// approval only when its second parent is on the base branch and every file in it matches one of its parents,
    /// so feature merges and merges with manual edits or conflict resolutions invalidate the approval.
    /// Fails closed (false) when the retrieved commit history does not end at <paramref name="headSha"/>, for
    /// example because GitHub caps the pull request commit list at 250 entries.
    /// </summary>
    private async Task<bool> HasReviewerApprovalAsync(
        string pullPath, string repoPath, string baseBranch, string headSha, string phrase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(headSha))
        {
            return false;
        }

        var commits = new List<JsonNode>();
        for (var page = 1; page <= 3; page++)
        {
            var list = (await SendRestAsync(HttpMethod.Get, $"{pullPath}/commits?per_page={PageSize}&page={page}", null, cancellationToken))
                ?.AsArray() ?? [];
            commits.AddRange(list.Where(item => item is not null)!);
            if (list.Count < PageSize)
            {
                break;
            }
        }

        if (commits.Count == 0
            || !string.Equals(commits[^1]["sha"]?.GetValue<string>(), headSha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lastIndex = 0;
        for (var index = commits.Count - 1; index >= 0; index--)
        {
            if (!await IsVerifiedBaseOnlyMergeAsync(repoPath, baseBranch, commits[index], cancellationToken))
            {
                lastIndex = index;
                break;
            }
        }

        var shas = commits.Select(commit => commit["sha"]?.GetValue<string>() ?? string.Empty).ToList();

        bool IsReviewer(JsonNode? item) =>
            string.Equals(
                NormalizeBotLogin(item?["user"]?["login"]?.GetValue<string>()),
                NormalizeBotLogin(_reviewerLogin),
                StringComparison.OrdinalIgnoreCase);

        // Read every page of reviews so a qualifying approval beyond the first page is not missed.
        var reviews = new List<JsonNode?>();
        for (var page = 1; ; page++)
        {
            var list = (await SendRestAsync(HttpMethod.Get, $"{pullPath}/reviews?per_page={PageSize}&page={page}", null, cancellationToken))
                ?.AsArray() ?? [];
            reviews.AddRange(list);
            if (list.Count < PageSize)
            {
                break;
            }
        }

        // Only the newest submitted review by the reviewer on the latest non-merge commit (or later) decides: a
        // later review without the phrase, such as CHANGES_REQUESTED, withdraws an earlier approval. Pending reviews
        // are not submitted and are skipped. The decisive review must be APPROVED or COMMENTED and contain the
        // phrase; dismissed reviews keep their body and commit ID but must never enable a merge.
        var latest = reviews
            .Where(IsReviewer)
            .Where(review => !string.Equals(review?["state"]?.GetValue<string>(), "PENDING", StringComparison.OrdinalIgnoreCase))
            .Where(review => shas.IndexOf(review?["commit_id"]?.GetValue<string>() ?? string.Empty) >= lastIndex)
            .LastOrDefault();
        if (latest is null)
        {
            return false;
        }

        var state = latest["state"]?.GetValue<string>();
        return (string.Equals(state, "APPROVED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "COMMENTED", StringComparison.OrdinalIgnoreCase))
            && (latest["body"]?.GetValue<string>() ?? string.Empty).Contains(phrase, StringComparison.Ordinal);
    }

    /// <summary>
    /// True only when <paramref name="commit"/> is a two-parent merge whose second parent is on the base branch and
    /// whose content adds nothing of its own: every file differs from at most one parent, so it equals either the
    /// previous head or the base version. Fails closed on truncated comparisons or GitHub errors.
    /// </summary>
    private async Task<bool> IsVerifiedBaseOnlyMergeAsync(
        string repoPath, string baseBranch, JsonNode commit, CancellationToken cancellationToken)
    {
        var parents = commit["parents"]?.AsArray();
        var sha = commit["sha"]?.GetValue<string>();
        if (parents is not { Count: 2 } || string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(baseBranch))
        {
            return false;
        }

        var headParent = parents[0]?["sha"]?.GetValue<string>();
        var baseParent = parents[1]?["sha"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(headParent) || string.IsNullOrWhiteSpace(baseParent))
        {
            return false;
        }

        try
        {
            var onBase = await SendRestAsync(
                HttpMethod.Get,
                $"{repoPath}/compare/{Uri.EscapeDataString(baseBranch)}...{Uri.EscapeDataString(baseParent)}", null, cancellationToken);
            if (onBase?["status"]?.GetValue<string>() is not ("behind" or "identical"))
            {
                return false;
            }

            var fromHead = await ChangedFilesAsync(repoPath, headParent, sha, cancellationToken);
            var fromBase = await ChangedFilesAsync(repoPath, baseParent, sha, cancellationToken);
            return fromHead is not null && fromBase is not null && !fromHead.Overlaps(fromBase);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Files changed from ancestor <paramref name="fromSha"/> to <paramref name="toSha"/> (including rename
    /// sources), or null when the comparison is not a plain descendant or the file list may be truncated.
    /// </summary>
    private async Task<HashSet<string>?> ChangedFilesAsync(
        string repoPath, string fromSha, string toSha, CancellationToken cancellationToken)
    {
        var compare = await SendRestAsync(
            HttpMethod.Get,
            $"{repoPath}/compare/{Uri.EscapeDataString(fromSha)}...{Uri.EscapeDataString(toSha)}", null, cancellationToken);
        var mergeBase = compare?["merge_base_commit"]?["sha"]?.GetValue<string>();
        var files = compare?["files"]?.AsArray();
        if (files is null
            || files.Count >= MaxCompareFiles
            || !string.Equals(mergeBase, fromSha, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            names.Add(file?["filename"]?.GetValue<string>() ?? string.Empty);
            if (file?["previous_filename"]?.GetValue<string>() is { Length: > 0 } previous)
            {
                names.Add(previous);
            }
        }

        return names;
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
