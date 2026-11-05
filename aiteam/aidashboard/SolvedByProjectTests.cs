using System.Net;
using System.Text;
using AiDashboard.Stats;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AiDashboard.Tests;

public class SolvedByProjectTests
{
    private sealed class FakeClient(IReadOnlyList<string>? keys = null, Exception? error = null) : IJiraSolvedClient
    {
        public Task<IReadOnlyList<string>> GetSolvedIssueProjectKeysAsync(CancellationToken cancellationToken) =>
            error is null ? Task.FromResult(keys ?? []) : Task.FromException<IReadOnlyList<string>>(error);
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

    [Fact]
    public async Task Groups_and_counts_solved_issues_per_project()
    {
        var result = await SolvedByProjectEndpoint.HandleAsync(
            new FakeClient(["DFAI", "ABC", "DFAI", "DFAI"]), CancellationToken.None);

        var ok = Assert.IsType<Ok<List<SolvedByProject>>>(result);
        Assert.Equal(
            [new SolvedByProject("ABC", 1), new SolvedByProject("DFAI", 3)],
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
    public async Task Jira_client_queries_done_category_and_follows_pages()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("nextPageToken")
            ? Json("""{"issues":[{"fields":{"project":{"key":"ABC"}}}]}""")
            : Json("""{"issues":[{"fields":{"project":{"key":"DFAI"}}}],"nextPageToken":"t1"}"""));
        var settings = new IntegrationSettings
        {
            Jira = new JiraSettings { JiraBaseUrl = "https://example.atlassian.net", JiraEmail = "a@b.c", JiraApiToken = "x" }
        };
        using var client = new JiraSolvedClient(settings, handler);

        var keys = await client.GetSolvedIssueProjectKeysAsync(CancellationToken.None);

        Assert.Equal(["DFAI", "ABC"], keys);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(Uri.EscapeDataString("statusCategory = Done"), handler.Requests[0].Query);
        Assert.All(handler.Authorizations, scheme => Assert.Equal("Basic", scheme));
    }
}
