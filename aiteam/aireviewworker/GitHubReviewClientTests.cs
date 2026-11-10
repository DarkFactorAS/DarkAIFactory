using System.Data;
using System.Text.Json.Nodes;
using AiTeam.Tests;
using ReviewWorker.Repository;

namespace ReviewWorker.Tests;

public sealed class GitHubReviewClientTests
{
    private static readonly Uri Repo = new("https://github.com/owner/repo");
    private static readonly TrackedPullRequest Pr = new(1, "DFAI-1", Repo, 7, "https://github.com/owner/repo/pull/7");

    private sealed class Handler(Func<JsonNode, string> respond) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Requests.Add(body);
            return new HttpResponseMessage { Content = new StringContent(respond(body)) };
        }
    }

    private static string Thread(
        long id, string login, bool resolved = false, bool outdated = false,
        bool hasNextCommentPage = false, params (long Id, string Login)[] replies)
    {
        var comments = new JsonArray { Comment(id, login) };
        foreach (var reply in replies)
        {
            comments.Add(Comment(reply.Id, reply.Login));
        }

        return new JsonObject
        {
            ["id"] = $"thread-{id}",
            ["line"] = 3,
            ["isResolved"] = resolved,
            ["isOutdated"] = outdated,
            ["comments"] = new JsonObject
            {
                ["pageInfo"] = new JsonObject
                {
                    ["hasNextPage"] = hasNextCommentPage,
                    ["endCursor"] = hasNextCommentPage ? "comment-cursor" : null
                },
                ["nodes"] = comments
            }
        }.ToJsonString();
    }

    private static JsonObject Comment(long id, string login) =>
        new()
        {
            ["fullDatabaseId"] = id.ToString(),
            ["body"] = $"fix {id}",
            ["path"] = "a.cs",
            ["author"] = new JsonObject { ["login"] = login }
        };

    private static string CommentsPage((long Id, string Login)[] comments, string? next = null) =>
        new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["node"] = new JsonObject
                {
                    ["line"] = 3,
                    ["comments"] = new JsonObject
                    {
                        ["pageInfo"] = new JsonObject
                        {
                            ["hasNextPage"] = next is not null,
                            ["endCursor"] = next
                        },
                        ["nodes"] = new JsonArray(comments.Select(comment => (JsonNode?)Comment(comment.Id, comment.Login)).ToArray())
                    }
                }
            }
        }.ToJsonString();

    private static string Page(string state, string threads, string? next = null) =>
        JsonNode.Parse(
            "{\"data\":{\"repository\":{\"pullRequest\":{\"state\":\"" + state + "\",\"headRefOid\":\"sha1\"," +
            "\"reviewThreads\":{\"pageInfo\":{\"hasNextPage\":" + (next is null ? "false" : "true") +
            ",\"endCursor\":" + (next is null ? "null" : "\"" + next + "\"") + "},\"nodes\":[" + threads + "]}}}}}")!
            .ToJsonString();

    private static (GitHubReviewClient Client, DatabaseStub Db) Create(
        Handler handler, Action<DatabaseStub>? setup = null)
    {
        var db = new DatabaseStub();
        db.OnReader = command =>
        {
            var table = new DataTable();
            if (command.CommandText.Contains("review_comment_attempts"))
            {
                table.Columns.Add("id", typeof(long));
                foreach (var id in HandledIds)
                {
                    table.Rows.Add(id);
                }
            }
            else
            {
                table.Columns.Add("id", typeof(long));
                table.Columns.Add("k", typeof(string));
                table.Columns.Add("u", typeof(string));
                table.Columns.Add("n", typeof(long));
                table.Columns.Add("p", typeof(string));
                table.Rows.Add(1L, "DFAI-1", Repo.AbsoluteUri, 7L, Pr.Url);
                table.Rows.Add(2L, "DFAI-2", Repo.AbsoluteUri, 8L, "https://github.com/owner/repo/pull/8");
            }

            return table.CreateDataReader();
        };
        setup?.Invoke(db);
        return (new GitHubReviewClient("t", "Copilot", 3, new PullRequestReader(db),
            new ReviewCommentAttemptRepository(db), handler), db);
    }

    [ThreadStatic] private static long[] HandledIds = [];

    [Fact]
    public async Task FiltersByReviewerResolvedOutdatedAndHandled()
    {
        HandledIds = [14];
        var handler = new Handler(_ => Page("OPEN",
            string.Join(",",
                Thread(10, "Copilot"), Thread(11, "someone"), Thread(12, "Copilot", resolved: true),
                Thread(13, "Copilot", outdated: true), Thread(14, "Copilot"), Thread(15, "copilot"))));
        var (client, _) = Create(handler);

        var comments = await client.GetActionableCommentsAsync(Pr, default);

        Assert.Equal([10L, 15L], comments.Select(c => c.Id));
        Assert.All(comments, c => Assert.Equal("sha1", c.HeadSha));
        Assert.Equal("a.cs", comments[0].Path);
        Assert.Equal(3, comments[0].Line);
        Assert.Equal("fix 10", comments[0].Body);
    }

    [Fact]
    public async Task FollowsPagination()
    {
        HandledIds = [];
        var handler = new Handler(request => request["variables"]!["cursor"] is null
            ? Page("OPEN", Thread(1, "Copilot"), next: "c1")
            : Page("OPEN", Thread(2, "Copilot")));
        var (client, _) = Create(handler);

        var comments = await client.GetActionableCommentsAsync(Pr, default);

        Assert.Equal([1L, 2L], comments.Select(c => c.Id));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("c1", handler.Requests[1]["variables"]!["cursor"]!.GetValue<string>());
    }

    [Fact]
    public async Task FiltersEveryCommentAcrossPaginatedThreadComments()
    {
        HandledIds = [];
        var handler = new Handler(request => request["variables"]!["threadId"] is not null
            ? CommentsPage([(22, "Copilot")])
            : Page("OPEN", Thread(
                20, "someone", hasNextCommentPage: true, replies: [(21, "Copilot")])));
        var (client, _) = Create(handler);

        var comments = await client.GetActionableCommentsAsync(Pr, default);

        Assert.Equal([21L, 22L], comments.Select(comment => comment.Id));
        Assert.Equal(3, comments[0].Line);
        Assert.Equal(3, comments[1].Line);
        Assert.Equal("thread-20", handler.Requests[1]["variables"]!["threadId"]!.GetValue<string>());
        Assert.Equal("comment-cursor", handler.Requests[1]["variables"]!["cursor"]!.GetValue<string>());
    }

    [Fact]
    public async Task ParsesFullDatabaseIdsBeyondInt32()
    {
        HandledIds = [];
        var (client, _) = Create(new Handler(_ => Page("OPEN", Thread(2_147_483_648, "Copilot"))));

        var comments = await client.GetActionableCommentsAsync(Pr, default);

        Assert.Equal(2_147_483_648L, Assert.Single(comments).Id);
    }

    [Fact]
    public async Task ClosedPullRequestYieldsNoComments()
    {
        HandledIds = [];
        var (client, _) = Create(new Handler(_ => Page("MERGED", Thread(1, "Copilot"))));
        Assert.Empty(await client.GetActionableCommentsAsync(Pr, default));
    }

    [Fact]
    public async Task ListsOnlyOpenRecordedPullRequests()
    {
        HandledIds = [];
        var (client, _) = Create(new Handler(r =>
            Page(r["variables"]!["number"]!.GetValue<long>() == 7 ? "OPEN" : "CLOSED", "")));

        var open = await client.ListOpenPullRequestsAsync(Repo, default);

        Assert.Equal([7L], open.Select(p => p.Number));
    }

    [Fact]
    public async Task HandledQueryUsesPullRequestIdAndMaxAttempts()
    {
        HandledIds = [];
        var (client, db) = Create(new Handler(_ => Page("OPEN", "")));
        await client.GetActionableCommentsAsync(Pr, default);
        var command = Assert.Single(db.Commands);
        Assert.Equal(1L, command.Values["@pull_request_id"]);
        Assert.Equal(3, command.Values["@max_attempts"]);
    }

    [Fact]
    public async Task GraphQlErrorsThrow()
    {
        HandledIds = [];
        var (client, _) = Create(new Handler(_ => """{"errors":[{"message":"boom"}]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetActionableCommentsAsync(Pr, default));
    }
}
