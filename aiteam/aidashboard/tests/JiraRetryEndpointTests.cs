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

    private static Task<IResult> Run(FakeClient client, ActiveTasks tasks, string key = "DFAI-1", string failure = "failed") =>
        JiraRetryEndpoint.HandleAsync(client, tasks, new Labels(failure), key, CancellationToken.None);

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
    public void ClearBodyEmptiesLabels() =>
        Assert.Equal("{\"fields\":{\"labels\":[]}}", JiraLabelClient.BuildClearBody());
}
