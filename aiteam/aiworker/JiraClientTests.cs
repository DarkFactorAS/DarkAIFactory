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
        TimeSpan.FromSeconds(30));

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
    public async Task FindWorkAsync_ParsesAdfDescription()
    {
        const string json = """
        {"issues":[{"key":"DFAI-1","fields":{"summary":"Title","description":{"type":"doc","content":[
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
