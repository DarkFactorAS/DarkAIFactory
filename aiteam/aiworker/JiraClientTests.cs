using System.Net;
using System.Text;
using System.Text.Json;

namespace CodingAgent.Tests;

public sealed class JiraClientTests
{
    private static AgentConfiguration CreateConfiguration(string jql = "project = X") => new(
        new Uri("https://example.atlassian.net"),
        "bot@example.com",
        "token",
        "gh",
        "repo",
        new Uri("https://github.com/owner/repo"),
        jql,
        "picked",
        "failed",
        "In Progress",
        "In Review",
        TimeSpan.FromSeconds(30));

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            var (status, responseBody) = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private const string TransitionsJson = """
        {"transitions":[
          {"id":"11","name":"Start","to":{"name":"In Progress"}},
          {"id":"21","name":"Send to review","to":{"name":"In Review"}}]}
        """;

    [Theory]
    [InlineData("In Progress", "11")]
    [InlineData("in review", "21")]
    [InlineData("Start", "11")]
    public async Task TransitionToAsync_PostsMatchingTransition(string status, string expectedId)
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, TransitionsJson), (HttpStatusCode.NoContent, "{}"));
        using var client = new JiraClient(CreateConfiguration(), handler);

        await client.TransitionToAsync("DFAI-3", status, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Request.Method);
        var post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Request.Method);
        Assert.Equal("/rest/api/3/issue/DFAI-3/transitions", post.Request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal(expectedId, body.RootElement.GetProperty("transition").GetProperty("id").GetString());
    }

    [Fact]
    public async Task TransitionToAsync_ThrowsWhenNoMatch()
    {
        var handler = new SequenceHandler((HttpStatusCode.OK, TransitionsJson));
        using var client = new JiraClient(CreateConfiguration(), handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TransitionToAsync("DFAI-3", "Done", CancellationToken.None));

        Assert.Contains("In Progress", exception.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransitionToAsync_PrefersDestinationOverMatchingName()
    {
        const string transitions = """
            {"transitions":[
              {"id":"wrong","name":"In Progress","to":{"name":"Other"}},
              {"id":"right","name":"Start work","to":{"name":"In Progress"}}]}
            """;
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, transitions),
            (HttpStatusCode.NoContent, "{}"));
        using var client = new JiraClient(CreateConfiguration(), handler);

        await client.TransitionToAsync("DFAI-3", "In Progress", CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("right", body.RootElement.GetProperty("transition").GetProperty("id").GetString());
    }

    [Fact]
    public async Task TransitionToAsync_ThrowsOnHttpFailure()
    {
        using var client = new JiraClient(
            CreateConfiguration(),
            new SequenceHandler((HttpStatusCode.Forbidden, "{}")));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.TransitionToAsync("DFAI-3", "In Progress", CancellationToken.None));
    }

    [Fact]
    public async Task CreatePullRequestAsync_UsesDefaultBranchAndReturnsUrl()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"default_branch":"main"}"""),
            (HttpStatusCode.Created, """{"html_url":"https://github.com/owner/repo/pull/7"}"""));

        var url = await GitHubRepository.CreatePullRequestAsync(
            CreateConfiguration() with { RepositoryUrl = new Uri("https://github.com/owner/repo.git") },
            "ai-agent/DFAI-3-x",
            "DFAI-3",
            "Do thing",
            CancellationToken.None,
            handler);

        Assert.Equal("https://github.com/owner/repo/pull/7", url);
        Assert.Equal("/repos/owner/repo", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("/repos/owner/repo/pulls", handler.Requests[1].Request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("ai-agent/DFAI-3-x", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
        Assert.Equal("DFAI-3: Do thing", body.RootElement.GetProperty("title").GetString());
    }

    private sealed class StubHandler(HttpStatusCode status, string responseBody = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task GetIssueAsync_RefreshesRequirementsAndHandoffState()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """
            {"key":"TASK-1","fields":{"summary":"Changed title","description":{"type":"doc","content":[
              {"type":"paragraph","content":[{"type":"text","text":"Changed requirements"}]}]},
              "status":{"name":"Selected for Development"},"updated":"2026-11-02T13:00:00Z",
              "labels":["ai-analyzed","ai-agent-picked"]}}
            """);
        using var client = new JiraClient(CreateConfiguration(), handler);

        var issue = await client.GetIssueAsync("TASK-1", CancellationToken.None);

        Assert.Equal("Changed title", issue.Summary);
        Assert.Equal("Changed requirements", issue.Description);
        Assert.Equal("2026-11-02T13:00:00Z", issue.UpdatedAt);
        Assert.Equal(["ai-analyzed", "ai-agent-picked"], issue.Labels);
        Assert.Equal("/rest/api/3/issue/TASK-1", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Contains("updated,labels", handler.Request.RequestUri.Query);
    }

    [Fact]
    public async Task GetIssueAsync_DoesNotHideHttpErrors()
    {
        using var client = new JiraClient(CreateConfiguration(), new StubHandler(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetIssueAsync("TASK-1", CancellationToken.None));
    }

    [Fact]
    public async Task FindWorkAsync_ParsesAdfDescription()
    {
        const string json = """
        {"issues":[{"key":"DFAI-1","fields":{"summary":"Title","status":{"name":"Selected for Development"},"description":{"type":"doc","content":[
          {"type":"paragraph","content":[{"type":"text","text":"Line one"},{"type":"hardBreak"},{"type":"text","text":"Line two"}]},
          {"type":"bulletList","content":[
            {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"item"}]}]}]},
          {"type":"paragraph","content":[{"type":"inlineCard","attrs":{"url":"https://example.com/x"}}]}
        ]}}}]}
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        using var client = new JiraClient(CreateConfiguration(), handler);

        var issues = await client.FindWorkAsync(CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal("DFAI-1", issue.Key);
        Assert.Equal("Title", issue.Summary);
        Assert.Equal("Selected for Development", issue.StatusName);
        Assert.Contains("fields=summary,description,status", handler.Request!.RequestUri!.Query);
        var lines = issue.Description.Split(Environment.NewLine);
        Assert.Equal(new[] { "Line one", "Line two", "- item", "https://example.com/x" }, lines);
    }

    [Fact]
    public async Task FindWorkAsync_HandlesNullDescription()
    {
        var handler = new StubHandler(
            HttpStatusCode.OK,
            """{"issues":[{"key":"DFAI-2","fields":{"summary":"S","description":null}}]}""");
        using var client = new JiraClient(CreateConfiguration(), handler);

        var issue = Assert.Single(await client.FindWorkAsync(CancellationToken.None));

        Assert.Equal(string.Empty, issue.Description);
    }

    [Fact]
    public async Task FindWorkAsync_ThrowsWhenKeyMissing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"issues":[{"key":null,"fields":{}}]}""");
        using var client = new JiraClient(CreateConfiguration(), handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FindWorkAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindWorkAsync_SendsAuthAndEscapedQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"issues":[]}""");
        using var client = new JiraClient(CreateConfiguration("project = \"X\" & y"), handler);

        await client.FindWorkAsync(CancellationToken.None);

        var request = handler.Request!;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/rest/api/3/search/jql", request.RequestUri!.AbsolutePath);
        Assert.Contains(
            "jql=" + Uri.EscapeDataString("project = \"X\" & y"),
            request.RequestUri.Query);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("bot@example.com:token")),
            request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task FindWorkAsync_ThrowsOnFailureStatus()
    {
        using var client = new JiraClient(CreateConfiguration(), new StubHandler(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindWorkAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddLabelAsync_SendsUpdateBody()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent);
        using var client = new JiraClient(CreateConfiguration(), handler);

        await client.AddLabelAsync("DFAI-3", "picked", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("/rest/api/3/issue/DFAI-3", handler.Request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            "picked",
            body.RootElement.GetProperty("update").GetProperty("labels")[0].GetProperty("add").GetString());
    }

    [Fact]
    public async Task AddCommentAsync_SendsAdfBody()
    {
        var handler = new StubHandler(HttpStatusCode.Created);
        using var client = new JiraClient(CreateConfiguration(), handler);

        await client.AddCommentAsync("DFAI-3", "first\nsecond\n\nthird", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("/rest/api/3/issue/DFAI-3/comment", handler.Request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var doc = body.RootElement.GetProperty("body");
        Assert.Equal("doc", doc.GetProperty("type").GetString());
        var paragraphs = doc.GetProperty("content");
        Assert.Equal(2, paragraphs.GetArrayLength());
        Assert.Equal("hardBreak", paragraphs[0].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("third", paragraphs[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task WriteCalls_ThrowOnFailureStatus()
    {
        using var client = new JiraClient(CreateConfiguration(), new StubHandler(HttpStatusCode.Forbidden));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.AddLabelAsync("DFAI-3", "l", CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.AddCommentAsync("DFAI-3", "t", CancellationToken.None));
    }
}
