using System.Net;
using System.Text;

namespace ManagerAgent.Tests;

public sealed class GitHubRepositoryHelperTests
{
    private static readonly AgentConfiguration Configuration = new(
        new Uri("https://example.atlassian.net"), "bot@example.com", "token", "gh", "repo",
        new Uri("https://github.com/owner/repo"), "project = X", "picked", "failed",
        "In Progress", "In Review", TimeSpan.FromSeconds(30));

    private sealed record Recorded(HttpMethod Method, string Path, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(token);
            Requests.Add(new Recorded(request.Method, request.RequestUri!.AbsolutePath, body));
            var (status, content) = respond(request);
            return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }

    [Theory]
    [InlineData("""{"state":"open","merged":false}""", "Open")]
    [InlineData("""{"state":"closed","merged":true}""", "Merged")]
    [InlineData("""{"state":"closed","merged":false}""", "Closed")]
    public async Task GetPullRequestState_MapsOpenMergedAndClosed(string json, string expected)
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, json));

        var state = await GitHubRepository.GetPullRequestStateAsync(Configuration, 5, CancellationToken.None, handler);

        Assert.Equal(Enum.Parse<GitHubRepository.PullRequestState>(expected), state);
        Assert.Equal("/repos/owner/repo/pulls/5", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task GetPullRequestState_ThrowsOnFailure()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.Forbidden, "{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubRepository.GetPullRequestStateAsync(Configuration, 5, CancellationToken.None, handler));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task BranchExists_ReportsExistence(HttpStatusCode status, bool expected)
    {
        var handler = new FakeHandler(_ => (status, "{}"));

        Assert.Equal(expected, await GitHubRepository.BranchExistsAsync(
            Configuration, "ai-agent/DFAI-1", CancellationToken.None, handler));
        Assert.Equal("/repos/owner/repo/git/ref/heads/ai-agent/DFAI-1", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public async Task BranchExists_ThrowsOnUnexpectedStatus()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.InternalServerError, "{}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubRepository.BranchExistsAsync(Configuration, "x", CancellationToken.None, handler));
    }

    [Fact]
    public async Task CreateBranchFromDefaultBranch_PostsRefAtDefaultBranchTip()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/repos/owner/repo" => (HttpStatusCode.OK, """{"default_branch":"main"}"""),
            "/repos/owner/repo/git/ref/heads/main" => (HttpStatusCode.OK, """{"object":{"sha":"abc123"}}"""),
            _ => (HttpStatusCode.Created, "{}")
        });

        var sha = await GitHubRepository.CreateBranchFromDefaultBranchAsync(
            Configuration, "ai-agent/DFAI-143", CancellationToken.None, handler);

        Assert.Equal("abc123", sha);
        var post = handler.Requests.Last();
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("/repos/owner/repo/git/refs", post.Path);
        Assert.Contains("\"ref\":\"refs/heads/ai-agent/DFAI-143\"", post.Body);
        Assert.Contains("\"sha\":\"abc123\"", post.Body);
    }

    [Fact]
    public async Task AddPullRequestComment_PostsToIssueComments()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.Created, "{}"));

        await GitHubRepository.AddPullRequestCommentAsync(Configuration, 9, "hello", CancellationToken.None, handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/repos/owner/repo/issues/9/comments", request.Path);
        Assert.Contains("\"body\":\"hello\"", request.Body);
    }

    [Fact]
    public async Task ClosePullRequestWithComment_CommentsThenCloses()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "{}"));

        await GitHubRepository.ClosePullRequestWithCommentAsync(Configuration, 9, "bye", CancellationToken.None, handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("/repos/owner/repo/issues/9/comments", handler.Requests[0].Path);
        Assert.Equal(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.Equal("/repos/owner/repo/pulls/9", handler.Requests[1].Path);
        Assert.Contains("\"state\":\"closed\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task CreatePullRequestWithBase_UsesExplicitBaseBranch()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.Created, """{"html_url":"https://github.com/owner/repo/pull/3"}"""));

        var url = await GitHubRepository.CreatePullRequestWithBaseAsync(
            Configuration, "feature", "ai-agent/DFAI-143", "Title", "Body", CancellationToken.None, handler);

        Assert.Equal("https://github.com/owner/repo/pull/3", url);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/repos/owner/repo/pulls", request.Path);
        Assert.Contains("\"base\":\"ai-agent/DFAI-143\"", request.Body);
        Assert.Contains("\"head\":\"feature\"", request.Body);
    }

    [Fact]
    public async Task CreatePullRequestWithBase_ThrowsWithDetailsOnFailure()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.UnprocessableEntity, "bad base"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubRepository.CreatePullRequestWithBaseAsync(
                Configuration, "a", "b", "t", "b", CancellationToken.None, handler));

        Assert.Contains("422", exception.Message);
        Assert.Contains("bad base", exception.Message);
    }

    [Fact]
    public async Task CreateRevertPullRequest_RejectsUnmergedPullRequest()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, """{"state":"open","merged":false}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GitHubRepository.CreateRevertPullRequestAsync(
                Configuration, Path.Combine(Path.GetTempPath(), "none", "checkout"), 4, "revert-4",
                CancellationToken.None, handler));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CherryPickToNewBranch_RequiresCommits() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            GitHubRepository.CherryPickToNewBranchAsync(
                Configuration, Path.Combine(Path.GetTempPath(), "none", "checkout"), "b", "origin/main", [],
                CancellationToken.None));
}
