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

    private static WorkflowLabels Labels(string agent) => new(
        agent,
        new Dictionary<string, string>
        {
            ["PickupLabel"] = agent == "aimanager" ? "ai-manager-planned" : "ai-agent-picked",
            ["AnalyzedLabel"] = "ai-analyzed"
        });

    private static ActiveAiTaskService Create(string body, out StubHandler handler)
    {
        handler = new StubHandler(body);
        var settings = new IntegrationSettings();
        settings.Jira.JiraBaseUrl = "https://example.atlassian.net";
        return new ActiveAiTaskService(settings, Labels, handler);
    }

    private static string Issue(string key, string status, string updated, string label, string description = "\"d\"") =>
        "{\"key\":\"" + key + "\",\"fields\":{\"summary\":\"S " + key + "\",\"description\":" + description +
        ",\"status\":{\"name\":\"" + status + "\"},\"updated\":\"" + updated + "\",\"labels\":[\"" + label + "\"]}}";

    [Fact]
    public void BuildJqlFiltersLabelsAndDoneAndOrdersByUpdated()
    {
        var jql = ActiveAiTaskService.BuildJql(["ai-analyzed"], "DFAI", "Done");

        Assert.Contains("labels in (\"ai-analyzed\")", jql);
        Assert.Contains("status != \"Done\"", jql);
        Assert.EndsWith("ORDER BY updated DESC", jql);
    }

    [Fact]
    public async Task ReturnsAtMostThreeMostRecentlyUpdatedMatchingIssues()
    {
        var body = "{\"issues\":[" + string.Join(",",
            Issue("A-1", "In Progress", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-2", "In Progress", "2026-01-04T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-3", "In Progress", "2026-01-03T10:00:00.000+0000", "ai-agent-picked"),
            Issue("A-4", "In Progress", "2026-01-02T10:00:00.000+0000", "ai-manager-planned")) + "]}";
        using var service = Create(body, out var handler);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(new[] { "A-2", "A-3", "A-4" }, result.Select(task => task.Key));
        Assert.Contains("maxResults=3", handler.RequestUri!.Query);
    }

    [Fact]
    public async Task ExcludesDoneAndUnlabelledIssuesAndReturnsFields()
    {
        var adf = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}]}";
        var body = "{\"issues\":[" + string.Join(",",
            Issue("A-1", "Done", "2026-01-05T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-2", "To Do", "2026-01-05T10:00:00.000+0000", "other"),
            Issue("A-3", "In Review", "2026-01-01T10:00:00.000+0000", "ai-analyzed", adf)) + "]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        var task = Assert.Single(result);
        Assert.Equal(new ActiveAiTask("A-3", "S A-3", "In Review", "Hello"), task);
    }

    [Fact]
    public async Task TiesAreBrokenByKey()
    {
        var body = "{\"issues\":[" + string.Join(",",
            Issue("A-9", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed"),
            Issue("A-1", "To Do", "2026-01-01T10:00:00.000+0000", "ai-analyzed")) + "]}";
        using var service = Create(body, out _);

        var result = await service.GetActiveAsync(CancellationToken.None);

        Assert.Equal(new[] { "A-1", "A-9" }, result.Select(task => task.Key));
    }
}
