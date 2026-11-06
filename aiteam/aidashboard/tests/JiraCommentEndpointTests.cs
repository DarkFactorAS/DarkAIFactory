using AiDashboard.Tasks;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Http;

namespace AiDashboard.Tests;

public sealed class JiraCommentEndpointTests
{
    private sealed class FakeClient(bool fail = false, Exception? error = null) : IJiraCommentClient
    {
        public List<(string Key, string Text)> Calls { get; } = [];

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
                ? Task.FromResult<IReadOnlyList<ActiveAiTask>>([new(key, "Task", "Active", "")])
                : Task.FromException<IReadOnlyList<ActiveAiTask>>(error);
    }

    [Fact]
    public async Task AppendsTrimmedComment()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks(), new IntegrationSettings(),
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
            client, new ActiveTasks(), new IntegrationSettings(),
            key, new JiraCommentRequest(text), CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task JiraFailureReturns502()
    {
        var result = await JiraCommentEndpoint.HandleAsync(
            new FakeClient(fail: true), new ActiveTasks(), new IntegrationSettings(),
            "DFAI-1", new JiraCommentRequest("x"), CancellationToken.None);

        Assert.Equal(502, Status(result));
    }

    [Fact]
    public async Task JiraTimeoutReturns502()
    {
        var result = await JiraCommentEndpoint.HandleAsync(
            new FakeClient(error: new TaskCanceledException()), new ActiveTasks(), new IntegrationSettings(),
            "DFAI-1", new JiraCommentRequest("x"), CancellationToken.None);
        Assert.Equal(502, Status(result));
    }

    [Fact]
    public async Task AuthorizationLookupTimeoutReturns502WithoutPosting()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks(error: new OperationCanceledException()), new IntegrationSettings(),
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
            new ActiveTasks(), new IntegrationSettings(),
            "DFAI-1", new JiraCommentRequest("x"), cancellation.Token));
    }

    [Theory]
    [InlineData("DFAI-2", "DFAI")]
    [InlineData("OTHER-1", "DFAI")]
    public async Task RejectsIssuesOutsideDisplayedTasksOrConfiguredProject(string key, string project)
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks(key == "DFAI-2" ? "DFAI-1" : key),
            new IntegrationSettings { Jira = new JiraSettings { JiraProjectKey = project } },
            key, new JiraCommentRequest("x"), CancellationToken.None);
        Assert.Equal(403, Status(result));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AllowsDisplayedTaskInNonDefaultProject()
    {
        var client = new FakeClient();
        var result = await JiraCommentEndpoint.HandleAsync(
            client, new ActiveTasks("OTHER-1"),
            new IntegrationSettings { Jira = new JiraSettings { JiraProjectKey = "OTHER" } },
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
}
