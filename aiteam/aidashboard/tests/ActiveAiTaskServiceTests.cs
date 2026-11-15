using System.Net;
using System.Text;
using AiTeam.Configuration;

namespace AiDashboard.Tests;

public sealed class ActiveAiTaskServiceTests
{
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static ActiveAiTaskService Create(string body, out StubHandler handler)
    {
        handler = new StubHandler(body);
        var settings = new IntegrationSettings();
        settings.Jira.JiraBaseUrl = "https://example.atlassian.net";
        return new ActiveAiTaskService(settings, new HttpClient(handler));
    }

    private static string Issue(string key, string status, string updated, string label, string description = "\"d\"") =>
        "{\"key\":\"" + key + "\",\"fields\":{\"summary\":\"S " + key + "\",\"description\":" + description +
        ",\"status\":{\"name\":\"" + status + "\"},\"updated\":\"" + updated + "\",\"labels\":[\"" + label + "\"]}}";

    [Fact]
    public void BuildJqlAddsStatusClauseOnlyWhenConfigured()
    {
        Assert.DoesNotContain("status in", ActiveAiTaskService.BuildJql("DFAI", "Done"));
        var jql = ActiveAiTaskService.BuildJql("DFAI", "Done", ["In Progress", "Q\"x"]);
        Assert.Contains("AND status in (\"In Progress\", \"Q\\\"x\") ORDER BY", jql);
    }

    [Fact]
    public void BuildJqlHasNoLabelFilterAndFiltersDoneAndOrdersByUpdatedAndKey()
    {
        var jql = ActiveAiTaskService.BuildJql("DFAI", "Done");

        Assert.DoesNotContain("labels", jql);
        Assert.Contains("status != \"Done\"", jql);
        Assert.EndsWith("ORDER BY updated DESC, key ASC", jql);
    }

    [Fact]
    public async Task ReturnsAtMostFiveMostRecentlyUpdatedMatchingIssues()
    {
        var body = "{\"issues\":[" + string.Join(",",
            Issue("A-1", "In Progress", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-2", "In Progress", "2026-01-04T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-3", "In Progress", "2026-01-03T10:00:00.000+0000", "ai-agent-picked"),
            Issue("A-4", "In Progress", "2026-01-02T10:00:00.000+0000", "ai-manager-planned"),
            Issue("A-5", "In Progress", "2026-01-05T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-6", "In Progress", "2026-01-06T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-7", "In Progress", "2026-01-07T10:00:00.000+0000", "ai-analyzed")) + "]}";
        using var service = Create(body, out var handler);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(new[] { "A-7", "A-6", "A-5", "A-2", "A-3" }, result.Select(task => task.Key));
        Assert.Contains($"maxResults={ActiveAiTaskService.MaxTasks}", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task ExcludesDoneIssuesKeepsUnlabelledAndReturnsFields()
    {
        var adf = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}]}";
        var body = "{\"issues\":[" + string.Join(",",
            Issue("A-1", "Done", "2026-01-05T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-2", "To Do", "2026-01-05T10:00:00.000+0000", "other"),
            Issue("A-3", "In Review", "2026-01-01T10:00:00.000+0000", "ai-analyzed", adf)) + "]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(new[] { "A-2", "A-3" }, result.Select(t => t.Key));
        var task = result.Single(t => t.Key == "A-3");
        Assert.Equal(("A-3", "S A-3", "In Review", "Hello"), (task.Key, task.Summary, task.Status, task.Description));
        Assert.Equal(new[] { "ai-analyzed" }, task.Labels);
    }

    [Fact]
    public async Task IssueWithoutLabelsFieldIsReturnedWithEmptyLabels()
    {
        var body = "{\"issues\":[{\"key\":\"A-1\",\"fields\":{\"summary\":\"s\",\"status\":{\"name\":\"To Do\"},\"updated\":\"2026-01-01T10:00:00.000+0000\"}}]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        var task = Assert.Single(result);
        Assert.Equal("A-1", task.Key);
        Assert.Empty(task.Labels);
    }

    [Fact]
    public async Task MoreThanFiveTiedIssuesReturnTheFiveLowestKeys()
    {
        var body = "{\"issues\":[" + string.Join(",",
            Issue("A-9", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-4", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-1", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-3", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-8", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-2", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed")) + "]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(new[] { "A-1", "A-2", "A-3", "A-4", "A-8" }, result.Select(task => task.Key));
    }

    [Fact]
    public async Task ReadsUrlsFromAdfSmartLinks()
    {
        var adf = "{\"type\":\"doc\",\"content\":[" +
                  "{\"type\":\"paragraph\",\"content\":[{\"type\":\"inlineCard\",\"attrs\":{\"url\":\"https://example.com/inline\"}}]}," +
                  "{\"type\":\"blockCard\",\"attrs\":{\"url\":\"https://example.com/block\"}}," +
                  "{\"type\":\"embedCard\",\"attrs\":{\"url\":\"https://example.com/embed\"}}]}";
        var body = "{\"issues\":[" +
                   Issue("A-1", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed", adf) + "]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(
            $"https://example.com/inline{Environment.NewLine}https://example.com/block{Environment.NewLine}https://example.com/embed",
            Assert.Single(result).Description);
    }

    [Fact]
    public async Task RequestDoesNotFilterOnLabelsEvenWhenAiLabelsConfigured()
    {
        var handler = new StubHandler("{\"issues\":[]}");
        var settings = new IntegrationSettings();
        settings.Jira.JiraBaseUrl = "https://example.atlassian.net";
        settings.Dashboard.AiLabels = ["runtime-label"];
        using var service = new ActiveAiTaskService(settings, new HttpClient(handler));

        await service.GetActiveAsync(CancellationToken.None);

        Assert.DoesNotContain(Uri.EscapeDataString("labels in"), handler.RequestUri!.Query);
        Assert.DoesNotContain("runtime-label", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task EmptyStatusListReturnsAllNonDoneStatuses()
    {
        var body = "{\"issues\":[" +
                   Issue("A-1", "In Progress", "2026-01-02T00:00:00.000+0000", "x") + "," +
                   Issue("A-2", "To Do", "2026-01-01T00:00:00.000+0000", "y") + "," +
                   Issue("A-3", "Done", "2026-01-03T00:00:00.000+0000", "y") + "]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(new[] { "A-1", "A-2" }, result.Select(t => t.Key));
    }

    [Fact]
    public async Task FiltersByConfiguredStatusesCaseInsensitively()
    {
        var body = "{\"issues\":[" +
                   Issue("A-1", "In Progress", "2026-01-02T00:00:00.000+0000", "ai-analyzed") + "," +
                   Issue("A-2", "To Do", "2026-01-01T00:00:00.000+0000", "ai-analyzed") + "]}";
        var handler = new StubHandler(body);
        var settings = new IntegrationSettings();
        settings.Jira.JiraBaseUrl = "https://example.atlassian.net";
        settings.Dashboard.ActiveStatuses = ["in progress"];
        var service = new ActiveAiTaskService(settings, new HttpClient(handler));

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal("A-1", Assert.Single(result).Key);
        Assert.Contains(Uri.EscapeDataString("status in (\"in progress\")"), handler.RequestUri!.Query);
    }
}
