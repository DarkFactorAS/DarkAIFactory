using System.Net;
using System.Text;
using System.Text.Json;
using AiTeam.Tests;
using CodingAgent.Repository;

namespace CodingAgent.Tests;

public sealed class PullRequestRecordRecoveryTests
{
    private static AgentConfiguration Configuration => new(
        new Uri("https://example.atlassian.net"),
        "bot@example.com",
        "token",
        "gh",
        "repo",
        new Uri("https://github.com/owner/repo"),
        "project = X",
        "picked",
        "failed",
        "In Progress",
        "In Review",
        TimeSpan.FromSeconds(30))
    {
        JiraProjectKey = "TASK"
    };

    private const string SearchResponse =
        """{"issues":[{"key":"TASK-1","fields":{"summary":"S","status":{"name":"In Review"},"labels":["ai-pr-tracking-pending"]}}]}""";

    private static string CommentsResponse(string url) =>
        JsonSerializer.Serialize(new
        {
            startAt = 0,
            maxResults = 100,
            total = 1,
            isLast = true,
            comments = new[]
            {
                new
                {
                    body = new
                    {
                        type = "doc",
                        content = new[]
                        {
                            new
                            {
                                type = "paragraph",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = PullRequestRecordRecovery.PendingCommentPrefix + url
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

    private sealed class RecoveryHandler(string url) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Query, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            Requests.Add((request.Method, path, query, body));
            var responseBody = path == "/rest/api/3/search/jql"
                ? SearchResponse
                : path.EndsWith("/comment", StringComparison.Ordinal)
                    ? CommentsResponse(url)
                    : "{}";
            return new HttpResponseMessage(path.EndsWith("/comment", StringComparison.Ordinal)
                    ? HttpStatusCode.OK
                    : path == "/rest/api/3/search/jql" ? HttpStatusCode.OK : HttpStatusCode.NoContent)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task RecoverAsync_RecordsFromJiraAndClearsPendingLabel()
    {
        var handler = new RecoveryHandler("https://github.com/owner/repo/pull/42");
        using var jira = new JiraClient(Configuration, handler);
        var database = new DatabaseStub();

        await new PullRequestRecordRecovery(Configuration, jira, new PullRequestRepository(database))
            .RecoverAsync(CancellationToken.None);

        var command = Assert.Single(database.Commands);
        Assert.Equal("TASK-1", command.Values["@jira_key"]);
        Assert.Equal(42L, command.Values["@pr_number"]);
        Assert.Contains(handler.Requests, request =>
            request.Path == "/rest/api/3/search/jql" &&
            request.Query.Contains("project = \"TASK\"", StringComparison.Ordinal) &&
            request.Query.Contains(PullRequestRecordRecovery.PendingLabel, StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request =>
            request.Method == HttpMethod.Put &&
            request.Body!.Contains("\"remove\":\"ai-pr-tracking-pending\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverAsync_LeavesPendingLabelWhenDatabaseWriteFails()
    {
        var handler = new RecoveryHandler("https://github.com/owner/repo/pull/42");
        using var jira = new JiraClient(Configuration, handler);
        var database = new DatabaseStub
        {
            OnNonQuery = _ => throw new InvalidOperationException("Database unavailable")
        };

        await new PullRequestRecordRecovery(Configuration, jira, new PullRequestRepository(database))
            .RecoverAsync(CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task RecoverAsync_RejectsPullRequestsFromAnotherRepository()
    {
        var handler = new RecoveryHandler("https://github.com/other/repo/pull/42");
        using var jira = new JiraClient(Configuration, handler);
        var database = new DatabaseStub();

        await new PullRequestRecordRecovery(Configuration, jira, new PullRequestRepository(database))
            .RecoverAsync(CancellationToken.None);

        Assert.Empty(database.Commands);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Put);
    }
}
