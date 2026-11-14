using System.Net;
using System.Text;
using AiDashboard.Stats;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AiDashboard.Tests;

public class SolvedByProjectTests
{
    private sealed class FakeClient(IReadOnlyList<SolvedIssue>? issues = null, Exception? error = null) : IJiraSolvedClient
    {
        public Task<IReadOnlyList<SolvedIssue>> GetSolvedIssuesAsync(CancellationToken cancellationToken) =>
            error is null ? Task.FromResult(issues ?? []) : Task.FromException<IReadOnlyList<SolvedIssue>>(error);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            Authorizations.Add(request.Headers.Authorization?.Scheme);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static WorkflowLabels LoadLabels(string agent) => new(
        agent,
        new Dictionary<string, string>
        {
            ["PickupLabel"] = agent == "aimanager" ? "custom-manager-planned" : "custom-worker-picked",
            ["AnalyzedLabel"] = "custom-analyzed"
        });

    [Fact]
    public async Task Groups_and_counts_solved_issues_per_project()
    {
        var result = await SolvedByProjectEndpoint.HandleAsync(
            new FakeClient([new("DFAI", true), new("ABC", false), new("DFAI", false), new("DFAI", true)]), CancellationToken.None);

        var ok = Assert.IsType<Ok<List<SolvedByProject>>>(result);
        Assert.Equal(
            [new SolvedByProject("ABC", 1, 0, 1), new SolvedByProject("DFAI", 3, 2, 1)],
            ok.Value);
    }

    [Fact]
    public async Task Returns_empty_list_when_nothing_solved()
    {
        var result = await SolvedByProjectEndpoint.HandleAsync(new FakeClient(), CancellationToken.None);

        Assert.Empty(Assert.IsType<Ok<List<SolvedByProject>>>(result).Value!);
    }

    [Fact]
    public async Task Client_failure_returns_502_without_leaking_details()
    {
        var result = await SolvedByProjectEndpoint.HandleAsync(
            new FakeClient(error: new HttpRequestException("secret-token")), CancellationToken.None);

        var json = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, json.StatusCode);
        var body = Assert.IsAssignableFrom<IValueHttpResult>(result).Value!.ToString();
        Assert.DoesNotContain("secret-token", body);
    }

    [Fact]
    public async Task Jira_client_classifies_configured_workflow_labels_but_not_ignored_marker()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("nextPageToken")
            ? Json("""{"issues":[{"fields":{"project":{"key":"ABC"},"labels":["custom-worker-picked"]}}]}""")
            : Json("""
                {"issues":[
                  {"fields":{"project":{"key":"DFAI"},"labels":["custom-analyzed","x"]}},
                  {"fields":{"project":{"key":"DFAI"},"labels":["ai-ignored"]}},
                  {"fields":{"project":{"key":"DFAI"}}}
                ],"nextPageToken":"t1"}
                """));
        var settings = new IntegrationSettings
        {
            Jira = new JiraSettings { JiraBaseUrl = "https://example.atlassian.net", JiraEmail = "a@b.c", JiraApiToken = "x" }
        };
        using var client = new JiraSolvedClient(settings, LoadLabels, handler);

        var issues = await client.GetSolvedIssuesAsync(CancellationToken.None);

        Assert.Equal(
            [new SolvedIssue("DFAI", true), new SolvedIssue("DFAI", false), new SolvedIssue("DFAI", false), new SolvedIssue("ABC", true)],
            issues);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(Uri.EscapeDataString("statusCategory = Done"), handler.Requests[0].Query);
        Assert.Contains("fields=project,labels", handler.Requests[0].Query);
        Assert.All(handler.Authorizations, scheme => Assert.Equal("Basic", scheme));
    }

    [Fact]
    public async Task Json_contract_contains_ai_counts()
    {
        var result = await SolvedByProjectEndpoint.HandleAsync(
            new FakeClient([new("DFAI", true)]), CancellationToken.None);

        var ok = Assert.IsType<Ok<List<SolvedByProject>>>(result);
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value, options);
        Assert.Equal("""[{"project":"DFAI","solvedCount":1,"aiCount":1,"nonAiCount":0}]""", json);
    }

    [Fact]
    public async Task Solved_client_uses_labels_replaced_at_runtime_and_falls_back_when_empty()
    {
        var handler = new StubHandler(_ => Json(
            """{"issues":[{"fields":{"project":{"key":"A"},"labels":["runtime"]}},{"fields":{"project":{"key":"A"},"labels":["custom-analyzed"]}}]}"""));
        var settings = new IntegrationSettings
        {
            Jira = new JiraSettings { JiraBaseUrl = "https://example.atlassian.net", JiraEmail = "a@b.c", JiraApiToken = "x" }
        };
        using var client = new JiraSolvedClient(settings, LoadLabels, handler);

        settings.Dashboard.AiLabels = ["runtime"];
        Assert.Equal([true, false], (await client.GetSolvedIssuesAsync(CancellationToken.None)).Select(i => i.IsAi));

        settings.Dashboard.AiLabels = [];
        Assert.Equal([false, true], (await client.GetSolvedIssuesAsync(CancellationToken.None)).Select(i => i.IsAi));
    }
}
