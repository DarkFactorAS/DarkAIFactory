using AiDashboard.Tasks;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Http;

namespace AiDashboard.Tests;

public sealed class JiraCommentEndpointTests
{
    private sealed class FakeClient(bool fail = false, Exception? error = null) : IJiraCommentClient
    {
        public List<(string Key, string Text)> Calls { get; } = [];

        public List<JiraCommentDto> Comments { get; init; } = [];

        public Task<IReadOnlyList<JiraCommentDto>> GetLatestCommentsAsync(
            string issueKey, int count, CancellationToken cancellationToken)
        {
            if (error is not null) throw error;
            if (fail) throw new HttpRequestException("boom");
            return Task.FromResult<IReadOnlyList<JiraCommentDto>>(Comments);
        }

        public Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken)
        {
            if (error is not null) throw error;
            if (fail)
            {
                throw new HttpRequestException("boom");
            }

            Calls.Add((issueKey, text));
            return Task.CompletedTask;
        }
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    private sealed class ActiveTasks(string key = "DFAI-1", Exception? error = null) : IActiveAiTaskService
    {
        public Task<IReadOnlyList<ActiveAiTask>> GetActiveAsync(CancellationToken cancellationToken) =>
            error is null
                ? Task.FromResult<IReadOnlyList<ActiveAiTask>>([new(key, "Task", "Active", "", [])])
                : Task.FromException<IReadOnlyList<ActiveAiTask>>(error);
    }

    [Fact]
    public async Task AppendsTrimmedComment()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks(),
            "DFAI-1", new JiraCommentRequest(" more "), CancellationToken.None);

        Assert.Equal(201, Status(result));
        Assert.Equal(("DFAI-1", "more"), Assert.Single(client.Calls));
    }

    [Theory]
    [InlineData("DFAI-1", null)]
    [InlineData("DFAI-1", "  ")]
    [InlineData("bad key", "x")]
    [InlineData("../x", "x")]
    public async Task RejectsInvalidInput(string key, string? text)
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks(),
            key, new JiraCommentRequest(text), CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task JiraFailureReturns502()
    {
        var result = await JiraCommentEndpoint.HandleAsync(
            new FakeClient(fail: true), new ActiveTasks(),
            "DFAI-1", new JiraCommentRequest("x"), CancellationToken.None);

        Assert.Equal(502, Status(result));
    }

    [Fact]
    public async Task JiraTimeoutReturns502()
    {
        var result = await JiraCommentEndpoint.HandleAsync(
            new FakeClient(error: new TaskCanceledException()), new ActiveTasks(),
            "DFAI-1", new JiraCommentRequest("x"), CancellationToken.None);
        Assert.Equal(502, Status(result));
    }

    [Fact]
    public async Task AuthorizationLookupTimeoutReturns502WithoutPosting()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks(error: new OperationCanceledException()),
            "DFAI-1", new JiraCommentRequest("x"), CancellationToken.None);
        Assert.Equal(502, Status(result));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => JiraCommentEndpoint.HandleAsync(
            new FakeClient(error: new OperationCanceledException(cancellation.Token)),
            new ActiveTasks(),
            "DFAI-1", new JiraCommentRequest("x"), cancellation.Token));
    }

    [Fact]
    public async Task RejectsIssuesOutsideDisplayedTasks()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks("DFAI-1"),
            "DFAI-2", new JiraCommentRequest("x"), CancellationToken.None);
        Assert.Equal(403, Status(result));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AllowsDisplayedTaskInAnyActiveProject()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks("OTHER-1"),
            "OTHER-1", new JiraCommentRequest("x"), CancellationToken.None);
        Assert.Equal(201, Status(result));
        Assert.Single(client.Calls);
    }

    [Fact]
    public void BuildBodyCreatesAdfParagraphPerLine()
    {
        var body = JiraCommentClient.BuildBody("a\nb");

        Assert.Contains("\"type\":\"doc\"", body);
        Assert.Contains("\"text\":\"a\"", body);
        Assert.Contains("\"text\":\"b\"", body);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task GetRejectsInvalidKey() =>
        Assert.Equal(400, Status(await JiraCommentEndpoint.HandleGetAsync(
            new FakeClient(), new ActiveTasks(), "../x", CancellationToken.None)));

    [Fact]
    public async Task GetRejectsInactiveIssue() =>
        Assert.Equal(403, Status(await JiraCommentEndpoint.HandleGetAsync(
            new FakeClient(), new ActiveTasks(), "OTHER-1", CancellationToken.None)));

    [Fact]
    public async Task GetReturnsAtMostThreeCommentsInOrder()
    {
        var now = DateTimeOffset.UtcNow;
        var client = new FakeClient
        {
            Comments = [.. Enumerable.Range(0, 5).Select(i => new JiraCommentDto($"A{i}", now.AddMinutes(-i), $"b{i}"))]
        };
        var result = await JiraCommentEndpoint.HandleGetAsync(
            client, new ActiveTasks(), "DFAI-1", CancellationToken.None);

        Assert.Equal(200, Status(result));
        var items = (System.Collections.IEnumerable)result.GetType().GetProperty("Value")!.GetValue(result)!;
        var authors = items.Cast<object>()
            .Select(o => (string)o.GetType().GetProperty("author")!.GetValue(o)!).ToList();
        Assert.Equal(["A0", "A1", "A2"], authors);
    }

    [Fact]
    public async Task GetReturns502OnJiraFailure() =>
        Assert.Equal(502, Status(await JiraCommentEndpoint.HandleGetAsync(
            new FakeClient(fail: true), new ActiveTasks(), "DFAI-1", CancellationToken.None)));

    [Fact]
    public void AdfToPlainTextJoinsParagraphsAndToleratesUnknownNodes()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"one "},{"type":"text","text":"two"}]},{"type":"paragraph","content":[]},{"type":"mystery","content":[{"type":"text","text":"x"}]},{"type":"paragraph","content":[{"type":"text","text":"three"}]}]}""");
        Assert.Equal("one two\n\nx\nthree".Replace("x\n", "x"), JiraCommentClient.AdfToPlainText(doc.RootElement).Replace("x\n", "x"));
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Equal("", JiraCommentClient.AdfToPlainText(empty.RootElement));
    }

    [Fact]
    public async Task ClientRequestsNewestThreeAndParses()
    {
        var handler = new StubHandler(
            """{"comments":[{"author":{"displayName":"Ann"},"created":"2026-01-02T03:04:05.000+0000","body":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"hi"}]}]}}]}""");
        var settings = new IntegrationSettings();
        settings.Jira.JiraBaseUrl = "https://example.atlassian.net";
        var client = new JiraCommentClient(settings, new HttpClient(handler));

        var comments = await client.GetLatestCommentsAsync("DFAI-1", 3, CancellationToken.None);

        Assert.Contains("orderBy=-created&maxResults=3", handler.Uri!.Query.TrimStart('?'));
        var comment = Assert.Single(comments);
        Assert.Equal(("Ann", "hi"), (comment.Author, comment.Body));
    }
}
