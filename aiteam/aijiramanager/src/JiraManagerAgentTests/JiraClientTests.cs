using System.Net;
using System.Text;

namespace JiraManagerAgent.Tests;

public sealed class JiraClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static AgentConfiguration Configuration() => AgentConfiguration.FromValues(
        new Dictionary<string, string>
        {
            ["Jira.JiraBaseUrl"] = "https://example.atlassian.net",
            ["Jira.JiraEmail"] = "bot@example.com",
            ["Jira.JiraApiToken"] = "token"
        }, 30);

    [Fact]
    public async Task CreateIssueAsync_PostsFieldsAndReturnsKey()
    {
        var handler = new StubHandler((_, _) => Json("""{"key":"DFAI-77"}""", HttpStatusCode.Created));
        using var client = new JiraClient(Configuration(), handler);

        var key = await client.CreateIssueAsync("DFAI", "Title", "Line one\n\nLine two", CancellationToken.None);

        Assert.Equal("DFAI-77", key);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/rest/api/3/issue", request.Path);
        Assert.Contains("\"summary\":\"Title\"", request.Body);
        Assert.Contains("\"key\":\"DFAI\"", request.Body);
        Assert.Contains("\"name\":\"Task\"", request.Body);
    }

    [Fact]
    public async Task CreateIssueAsync_ThrowsOnHttpError()
    {
        var handler = new StubHandler((_, _) => Json("{}", HttpStatusCode.BadRequest));
        using var client = new JiraClient(Configuration(), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateIssueAsync("DFAI", "T", "D", CancellationToken.None));
    }

    [Fact]
    public async Task TransitionToAsync_PostsMatchingTransition()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Get
            ? Json("""{"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}}]}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = new JiraClient(Configuration(), handler);

        await client.TransitionToAsync("DFAI-1", "In Progress", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"id\":\"11\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TransitionToAsync_ThrowsWhenTransitionMissing()
    {
        var handler = new StubHandler((_, _) => Json("""{"transitions":[]}"""));
        using var client = new JiraClient(Configuration(), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TransitionToAsync("DFAI-1", "In Progress", CancellationToken.None));
    }
}
