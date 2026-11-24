using AiDashboard.Tasks;
using Microsoft.AspNetCore.Http;

namespace AiDashboard.Tests;

public sealed class JiraRetryEndpointTests
{
    private sealed class FakeClient(bool fail = false) : IJiraLabelClient
    {
        public List<string> Cleared { get; } = [];

        public Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ClearLabelsAsync(string issueKey, CancellationToken cancellationToken)
        {
            if (fail) throw new HttpRequestException("boom");
            Cleared.Add(issueKey);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeComments(bool fail = false) : IJiraCommentClient
    {
        public List<(string Key, string Text)> Added { get; } = [];

        public Task<IReadOnlyList<JiraCommentDto>> GetLatestCommentsAsync(
            string issueKey, int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JiraCommentDto>>([]);

        public Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken)
        {
            if (fail) throw new HttpRequestException("comment boom");
            Added.Add((issueKey, text));
            return Task.CompletedTask;
        }
    }

    private sealed class ActiveTasks(params string[] labels) : IActiveAiTaskService
    {
        public Task<IReadOnlyList<ActiveAiTask>> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActiveAiTask>>([new("DFAI-1", "Task", "Active", "", labels)]);
    }

    private sealed class Labels(string failure = "failed") : IApprovalLabelSource
    {
        public ApprovalLabels Get() => new("a", "b", "merge-req", "ready", failure);
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    private static Task<IResult> Run(FakeClient client, ActiveTasks tasks, string key = "DFAI-1", string failure = "failed",
        FakeComments? comments = null) =>
        JiraRetryEndpoint.HandleAsync(client, comments ?? new FakeComments(), tasks, new Labels(failure), key, CancellationToken.None);

    [Fact]
    public async Task InvalidKeyReturns400()
    {
        var client = new FakeClient();
        Assert.Equal(400, Status(await Run(client, new ActiveTasks("failed"), "../x")));
        Assert.Empty(client.Cleared);
    }

    [Fact]
    public async Task InactiveIssueReturns403()
    {
        var client = new FakeClient();
        Assert.Equal(403, Status(await Run(client, new ActiveTasks("failed"), "DFAI-2")));
        Assert.Empty(client.Cleared);
    }

    [Fact]
    public async Task NoFailureLabelReturns409()
    {
        var client = new FakeClient();
        Assert.Equal(409, Status(await Run(client, new ActiveTasks("other"))));
        Assert.Empty(client.Cleared);
    }

    [Fact]
    public async Task FailureLabelClearsLabels()
    {
        var client = new FakeClient();
        Assert.Equal(200, Status(await Run(client, new ActiveTasks("failed", "other"))));
        Assert.Equal("DFAI-1", Assert.Single(client.Cleared));
    }

    [Fact]
    public async Task CustomFailureLabelIsHonoured()
    {
        var client = new FakeClient();
        Assert.Equal(409, Status(await Run(client, new ActiveTasks("failed"), failure: "custom")));
        Assert.Equal(200, Status(await Run(client, new ActiveTasks("CUSTOM"), failure: "custom")));
        Assert.Single(client.Cleared);
    }

    [Fact]
    public async Task JiraFailureReturns502()
    {
        Assert.Equal(502, Status(await Run(new FakeClient(fail: true), new ActiveTasks("failed"))));
    }

    [Fact]
    public async Task SuccessfulRetryAddsOneComment()
    {
        var client = new FakeClient();
        var comments = new FakeComments();
        Assert.Equal(200, Status(await Run(client, new ActiveTasks("failed"), comments: comments)));
        Assert.Single(client.Cleared);
        var added = Assert.Single(comments.Added);
        Assert.Equal("DFAI-1", added.Key);
        Assert.Equal(JiraRetryEndpoint.RetryCommentText, added.Text);
    }

    [Fact]
    public async Task NoCommentOnRejectedOrFailedRetry()
    {
        var comments = new FakeComments();
        await Run(new FakeClient(), new ActiveTasks("failed"), "../x", comments: comments);
        await Run(new FakeClient(), new ActiveTasks("failed"), "DFAI-2", comments: comments);
        await Run(new FakeClient(), new ActiveTasks("other"), comments: comments);
        Assert.Equal(502, Status(await Run(new FakeClient(fail: true), new ActiveTasks("failed"), comments: comments)));
        Assert.Empty(comments.Added);
    }

    [Fact]
    public async Task CommentFailureStillReturns200()
    {
        var client = new FakeClient();
        Assert.Equal(200, Status(await Run(client, new ActiveTasks("failed"), comments: new FakeComments(fail: true))));
        Assert.Single(client.Cleared);
    }

    [Fact]
    public void ClearBodyEmptiesLabels() =>
        Assert.Equal("{\"fields\":{\"labels\":[]}}", JiraLabelClient.BuildClearBody());
}
