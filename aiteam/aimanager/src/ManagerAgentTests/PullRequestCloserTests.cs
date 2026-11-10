using System.Net;
using System.Text;
using System.Text.Json;
using AiTeam.Tests;
using ManagerAgent.Repository;

namespace ManagerAgent.Tests;

public sealed class PullRequestCloserTests
{
    private static readonly AgentConfiguration Configuration = new(
        new Uri("https://example.atlassian.net"), "bot@example.com", "token", "gh", "repo",
        new Uri("https://github.com/owner/repo"), "project = X", "picked", "failed",
        "In Progress", "In Review", TimeSpan.FromSeconds(30));

    private const string TransitionsJson = """{"transitions":[{"id":"31","name":"Done","to":{"name":"Done"}}]}""";

    private sealed class JiraHandler(params string[] inReviewKeys) : HttpMessageHandler
    {
        public List<string> Transitioned { get; } = [];
        public string? Jql { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path == "/rest/api/3/search/jql")
            {
                Jql = Uri.UnescapeDataString(request.RequestUri.Query);
                var issues = string.Join(",", inReviewKeys.Select(key => JsonSerializer.Serialize(new
                {
                    key,
                    fields = new { summary = "S", status = new { name = "In Review" } }
                })));
                body = $$"""{"issues":[{{issues}}]}""";
            }
            else if (request.Method == HttpMethod.Post)
            {
                Transitioned.Add(path.Split('/')[^2] + ":" + await request.Content!.ReadAsStringAsync(token));
                body = "{}";
            }
            else
            {
                body = TransitionsJson;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static DatabaseStub Database(params (string Key, long Number)[] rows)
    {
        var database = new DatabaseStub();
        database.OnReader = command =>
        {
            var table = new System.Data.DataTable();
            table.Columns.Add("repository_url", typeof(string));
            table.Columns.Add("pr_number", typeof(long));
            table.Columns.Add("pr_url", typeof(string));
            foreach (var (key, number) in rows.Where(row => row.Key == (string)command.Values["@jira_key"]))
            {
                table.Rows.Add("https://github.com/owner/repo", number, $"https://github.com/owner/repo/pull/{number}");
            }

            return table.CreateDataReader();
        };
        return database;
    }

    [Fact]
    public async Task CloseMergedAsync_MovesOnlyIssuesWhoseEveryPullRequestIsMerged()
    {
        var handler = new JiraHandler("T-1", "T-2", "T-3", "T-4");
        using var jira = new JiraClient(Configuration, handler);
        var database = Database(("T-1", 1), ("T-2", 2), ("T-2", 3), ("T-3", 4), ("T-3", 5));
        var merged = new HashSet<long> { 1, 2, 4, 5 };
        var closer = new PullRequestCloser(
            Configuration, new PullRequestRepository(database), jira,
            (record, _) => Task.FromResult(merged.Contains(record.Number)));

        var closed = await closer.CloseMergedAsync("T", CancellationToken.None);

        Assert.Equal(["T-1", "T-3"], closed);
        Assert.Equal(2, handler.Transitioned.Count);
        Assert.Contains("In Review", handler.Jql);
        Assert.Contains("project = \"T\"", handler.Jql);
    }

    [Fact]
    public async Task CloseMergedAsync_IsolatesFailuresPerIssue()
    {
        var handler = new JiraHandler("T-1", "T-2");
        using var jira = new JiraClient(Configuration, handler);
        var database = Database(("T-1", 1), ("T-2", 2));
        var closer = new PullRequestCloser(
            Configuration, new PullRequestRepository(database), jira,
            (record, _) => record.Number == 1
                ? throw new InvalidOperationException("GitHub unavailable")
                : Task.FromResult(true));

        Assert.Equal(["T-2"], await closer.CloseMergedAsync("T", CancellationToken.None));
    }

    [Fact]
    public async Task IsPullRequestMergedAsync_ReadsMergedFlag()
    {
        var merged = new StubHandler(HttpStatusCode.OK, """{"state":"closed","merged":true}""");
        var unmerged = new StubHandler(HttpStatusCode.OK, """{"state":"closed","merged":false}""");

        Assert.True(await GitHubRepository.IsPullRequestMergedAsync(
            Configuration, new Uri("https://github.com/owner/repo.git"), 7, CancellationToken.None, merged));
        Assert.Equal("/repos/owner/repo/pulls/7", merged.Path);
        Assert.False(await GitHubRepository.IsPullRequestMergedAsync(
            Configuration, new Uri("https://github.com/owner/repo"), 7, CancellationToken.None, unmerged));
    }

    [Fact]
    public async Task IsPullRequestMergedAsync_ThrowsWhenGitHubRejectsRequest()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubRepository.IsPullRequestMergedAsync(
            Configuration, new Uri("https://github.com/owner/repo"), 7, CancellationToken.None,
            new StubHandler(HttpStatusCode.Forbidden, "{}")));
    }

    [Fact]
    public async Task CreateOrFindPullRequestAsync_UsesExistingParentPullRequestWhenAvailable()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"default_branch":"main"}"""),
            (HttpStatusCode.OK, """[{"html_url":"https://github.com/owner/repo/pull/42"}]"""));

        var url = await GitHubRepository.CreateOrFindPullRequestAsync(
            Configuration, "ai-agent/TASK-1", "TASK-1", "Title", CancellationToken.None, handler);

        Assert.Equal("https://github.com/owner/repo/pull/42", url);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("head=owner%3Aai-agent%2FTASK-1", handler.Requests[1].Request.RequestUri!.Query);
        Assert.Contains("base=main", handler.Requests[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task CreateOrFindPullRequestAsync_CreatesParentPullRequestAgainstDefaultBranch()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"default_branch":"main"}"""),
            (HttpStatusCode.OK, "[]"),
            (HttpStatusCode.Created, """{"html_url":"https://github.com/owner/repo/pull/43"}"""));

        var url = await GitHubRepository.CreateOrFindPullRequestAsync(
            Configuration, "ai-agent/TASK-1", "TASK-1", "Title", CancellationToken.None, handler);

        Assert.Equal("https://github.com/owner/repo/pull/43", url);
        var request = handler.Requests[2];
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("ai-agent/TASK-1", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            var (status, responseBody) = responses[_next++];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
