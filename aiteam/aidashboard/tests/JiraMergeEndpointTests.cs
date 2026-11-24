using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiDashboard.Agents;
using AiDashboard.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class JiraMergeEndpointTests
{
    private sealed class FakeClient(bool fail = false) : IJiraLabelClient
    {
        public List<(string Key, string Label)> Calls { get; } = [];

        public Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken)
        {
            if (fail) throw new HttpRequestException("boom");
            Calls.Add((issueKey, label));
            return Task.CompletedTask;
        }
    }

    private sealed class ActiveTasks(params string[] labels) : IActiveAiTaskService
    {
        public Task<IReadOnlyList<ActiveAiTask>> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActiveAiTask>>([new("DFAI-1", "Task", "Active", "", labels)]);
    }

    private sealed class Labels : IApprovalLabelSource
    {
        public ApprovalLabels Get() => new("a", "b", "merge-req", "ready", "failed");
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    private static Task<IResult> Run(FakeClient client, ActiveTasks tasks, string key = "DFAI-1") =>
        JiraMergeEndpoint.HandleAsync(client, tasks, new Labels(), key, CancellationToken.None);

    [Fact]
    public async Task InvalidKeyReturns400()
    {
        var client = new FakeClient();
        Assert.Equal(400, Status(await Run(client, new ActiveTasks("ready"), "../x")));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task InactiveIssueReturns403()
    {
        var client = new FakeClient();
        Assert.Equal(403, Status(await Run(client, new ActiveTasks("ready"), "DFAI-2")));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task NotReadyReturns409()
    {
        var client = new FakeClient();
        Assert.Equal(409, Status(await Run(client, new ActiveTasks("other"))));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task FailedReturns409()
    {
        var client = new FakeClient();
        Assert.Equal(409, Status(await Run(client, new ActiveTasks("ready", "failed"))));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AddsMergeRequestedLabel()
    {
        var client = new FakeClient();
        Assert.Equal(200, Status(await Run(client, new ActiveTasks("ready"))));
        Assert.Equal(("DFAI-1", "merge-req"), Assert.Single(client.Calls));
    }

    [Fact]
    public async Task AlreadyRequestedIsIdempotent()
    {
        var client = new FakeClient();
        Assert.Equal(200, Status(await Run(client, new ActiveTasks("ready", "merge-req"))));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task JiraFailureReturns502()
    {
        Assert.Equal(502, Status(await Run(new FakeClient(fail: true), new ActiveTasks("ready"))));
    }

    [Fact]
    public void ConfigReturnsMergeLabels()
    {
        var result = JiraApprovalEndpoint.ConfigAsync(new ProjectsStub(), new Labels());
        var json = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
        Assert.Equal("merge-req", json.GetProperty("mergeRequestedLabel").GetString());
        Assert.Equal("ready", json.GetProperty("readyToMergeLabel").GetString());
    }

    private sealed class ProjectsStub : IAgentDefinitionRepository
    {
        public IReadOnlyList<string> ListProjectKeys() => ["DFAI"];
        public IReadOnlyList<AgentView> List() => [];
        public AgentView? Find(long id) => null;
        public bool NameExists(string name, long? excludeId) => false;
        public IReadOnlyList<string> ExistingProjectKeys(IReadOnlyCollection<string> k) => [];
        public IReadOnlyList<string> ListBuilderProjectKeys() => ["DFAI"];
        public AgentView Create(AgentWrite agent) => throw new NotSupportedException();
        public AgentView? Update(long id, AgentWrite agent) => throw new NotSupportedException();
        public bool SetEnabled(long id, bool enabled) => false;
        public bool SoftDelete(long id) => false;
    }

    [Fact]
    public async Task EndpointRequiresLogin()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        var client = new FakeClient();
        builder.Services.AddSingleton<IJiraLabelClient>(client);
        builder.Services.AddSingleton<IActiveAiTaskService>(new ActiveTasks("ready"));
        builder.Services.AddSingleton<IApprovalLabelSource>(new Labels());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        JiraMergeEndpoint.Map(app);
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/tasks/DFAI-1/merge", null)).StatusCode);
        Assert.Empty(client.Calls);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tasks/DFAI-1/merge");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthHandler.Token);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);
        Assert.Single(client.Calls);
    }
}
