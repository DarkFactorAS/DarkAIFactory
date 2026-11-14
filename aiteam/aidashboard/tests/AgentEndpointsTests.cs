using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiDashboard.Agents;
using AiDashboard.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class AgentEndpointsTests
{
    private sealed class FakeRepository : IAgentDefinitionRepository
    {
        public List<(AgentView View, bool Deleted)> Rows { get; } = [];
        public string[] Projects { get; set; } = ["DFAI", "OTHER"];

        public IReadOnlyList<AgentView> List() => Rows.Where(r => !r.Deleted).Select(r => r.View).ToList();
        public AgentView? Find(long id) => List().FirstOrDefault(a => a.Id == id);
        public bool NameExists(string name, long? excludeId) =>
            List().Any(a => a.Name == name && a.Id != excludeId);
        public IReadOnlyList<string> ListProjectKeys() => Projects;
        public IReadOnlyList<string> ExistingProjectKeys(IReadOnlyCollection<string> keys) =>
            Projects.Where(p => keys.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

        public AgentView Create(AgentWrite w)
        {
            var view = ToView(Rows.Count + 1, w);
            Rows.Add((view, false));
            return view;
        }

        public AgentView? Update(long id, AgentWrite w)
        {
            var index = Rows.FindIndex(r => r.View.Id == id && !r.Deleted);
            if (index < 0) return null;
            Rows[index] = (ToView(id, w), false);
            return Rows[index].View;
        }

        public bool SetEnabled(long id, bool enabled)
        {
            var index = Rows.FindIndex(r => r.View.Id == id && !r.Deleted);
            if (index < 0) return false;
            Rows[index] = (Rows[index].View with { Enabled = enabled }, false);
            return true;
        }

        public bool SoftDelete(long id)
        {
            var index = Rows.FindIndex(r => r.View.Id == id && !r.Deleted);
            if (index < 0) return false;
            Rows[index] = (Rows[index].View with { Enabled = false }, true);
            return true;
        }

        private static AgentView ToView(long id, AgentWrite w) => new(
            id, w.Name, w.Role, w.Behavior, w.LlmProvider, w.LlmModel, w.Enabled, DateTime.UtcNow, DateTime.UtcNow,
            w.Capabilities, w.Projects, null);
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;
    private static object? Value(IResult result) => ((IValueHttpResult)result).Value;
    private static string Error(IResult result) =>
        (string)Value(result)!.GetType().GetProperty("error")!.GetValue(Value(result))!;

    private static AgentRequest Valid(string name = "Agent") =>
        new(name, "Role", "Behavior", "Claude", null, null, null, ["DFAI"]);

    [Fact]
    public void Create_NormalisesAndWritesAllCapabilities()
    {
        var repository = new FakeRepository();
        var request = Valid(" Dev ") with
        {
            LlmProvider = "copilot", LlmModel = " ",
            Capabilities = [new("builder", true)], Projects = ["dfai", "DFAI"]
        };
        var result = AgentEndpoints.Create(repository, request);

        Assert.Equal(201, Status(result));
        var agent = (AgentView)Value(result)!;
        Assert.Equal("Dev", agent.Name);
        Assert.Equal("Copilot", agent.LlmProvider);
        Assert.Null(agent.LlmModel);
        Assert.False(agent.Enabled);
        Assert.Equal(4, agent.Capabilities.Count);
        Assert.Single(agent.Capabilities, c => c.Enabled && c.Capability == "Builder");
        Assert.Equal(["DFAI"], agent.Projects);
    }

    public static TheoryData<AgentRequest?, string> InvalidRequests => new()
    {
        { null, "request body" },
        { Valid() with { Name = "  " }, "name is required" },
        { Valid() with { Name = new string('x', 256) }, "name is required" },
        { Valid() with { Role = null }, "role is required" },
        { Valid() with { Behavior = "" }, "behavior is required" },
        { Valid() with { Behavior = new string('x', 8001) }, "at most 8000" },
        { Valid() with { LlmProvider = "OpenAI" }, "Claude or Copilot" },
        { Valid() with { LlmProvider = null }, "Claude or Copilot" },
        { Valid() with { LlmModel = new string('x', 256) }, "llmModel" },
        { Valid() with { Capabilities = [new("Deploy", true)] }, "capability must be" },
        { Valid() with { Capabilities = [new("Jira", true), new("jira", false)] }, "more than once" },
        { Valid() with { Projects = ["NOPE"] }, "Unknown project key(s): NOPE" },
        { Valid() with { Projects = [" "] }, "must not be empty" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Create_RejectsInvalidInput(AgentRequest? request, string message)
    {
        var repository = new FakeRepository();
        var result = AgentEndpoints.Create(repository, request);

        Assert.Equal(400, Status(result));
        Assert.Contains(message, Error(result));
        Assert.Empty(repository.Rows);
    }

    [Fact]
    public void Create_AcceptsBehaviorOfExactlyMaxLength()
    {
        var result = AgentEndpoints.Create(new FakeRepository(), Valid() with { Behavior = new string('x', 8000) });
        Assert.Equal(201, Status(result));
    }

    [Fact]
    public void DuplicateName_IsRejectedButDeletedNameCanBeReused()
    {
        var repository = new FakeRepository();
        AgentEndpoints.Create(repository, Valid("A"));
        var duplicate = AgentEndpoints.Create(repository, Valid("A"));
        Assert.Equal(400, Status(duplicate));
        Assert.Contains("already exists", Error(duplicate));

        Assert.Equal(204, Status(AgentEndpoints.Delete(repository, 1)));
        Assert.Equal(201, Status(AgentEndpoints.Create(repository, Valid("A"))));
    }

    [Fact]
    public void Update_ReplacesFieldsKeepsEnabledAndAllowsOwnName()
    {
        var repository = new FakeRepository();
        AgentEndpoints.Create(repository, Valid("A") with { Enabled = true });
        var result = AgentEndpoints.Update(repository, 1, Valid("A") with { Role = "New" });

        Assert.Equal(200, Status(result));
        var agent = (AgentView)Value(result)!;
        Assert.Equal("New", agent.Role);
        Assert.True(agent.Enabled);
    }

    [Fact]
    public void Update_RejectsNameOfAnotherAgentAndMissingAgents()
    {
        var repository = new FakeRepository();
        AgentEndpoints.Create(repository, Valid("A"));
        AgentEndpoints.Create(repository, Valid("B"));

        Assert.Equal(400, Status(AgentEndpoints.Update(repository, 2, Valid("A"))));
        Assert.Equal(404, Status(AgentEndpoints.Update(repository, 99, Valid("Z"))));
        AgentEndpoints.Delete(repository, 1);
        Assert.Equal(404, Status(AgentEndpoints.Update(repository, 1, Valid("Z"))));
    }

    [Fact]
    public void SetEnabled_TogglesAndValidates()
    {
        var repository = new FakeRepository();
        AgentEndpoints.Create(repository, Valid());

        Assert.Equal(200, Status(AgentEndpoints.SetEnabled(repository, 1, new AgentEnabledRequest(true))));
        Assert.True(repository.Find(1)!.Enabled);
        Assert.Equal(400, Status(AgentEndpoints.SetEnabled(repository, 1, new AgentEnabledRequest(null))));
        Assert.Equal(400, Status(AgentEndpoints.SetEnabled(repository, 1, null)));
        Assert.Equal(404, Status(AgentEndpoints.SetEnabled(repository, 5, new AgentEnabledRequest(true))));
    }

    [Fact]
    public void Delete_HidesAgentFromReadsButKeepsRow()
    {
        var repository = new FakeRepository();
        AgentEndpoints.Create(repository, Valid());

        Assert.Equal(204, Status(AgentEndpoints.Delete(repository, 1)));
        Assert.Equal(404, Status(AgentEndpoints.Get(repository, 1)));
        Assert.Empty((IReadOnlyList<AgentView>)Value(AgentEndpoints.List(repository))!);
        Assert.Single(repository.Rows);
        Assert.Equal(404, Status(AgentEndpoints.Delete(repository, 1)));
    }

    [Fact]
    public void RepositoryFailure_Returns500WithoutDetails()
    {
        var result = AgentEndpoints.Get(new ThrowingRepository(), 1);
        Assert.Equal(500, Status(result));
        Assert.DoesNotContain("secret", Error(result));
    }

    private sealed class ThrowingRepository : IAgentDefinitionRepository
    {
        public IReadOnlyList<AgentView> List() => throw new InvalidOperationException("secret");
        public AgentView? Find(long id) => throw new InvalidOperationException("secret");
        public bool NameExists(string name, long? excludeId) => throw new InvalidOperationException("secret");
        public IReadOnlyList<string> ExistingProjectKeys(IReadOnlyCollection<string> keys) => [];
        public IReadOnlyList<string> ListProjectKeys() => [];
        public AgentView Create(AgentWrite agent) => throw new InvalidOperationException("secret");
        public AgentView? Update(long id, AgentWrite agent) => throw new InvalidOperationException("secret");
        public bool SetEnabled(long id, bool enabled) => throw new InvalidOperationException("secret");
        public bool SoftDelete(long id) => throw new InvalidOperationException("secret");
    }

    // Integration tests: real Kestrel host on a random port with the real write authentication.
    private const string Token = "test-write-token";

    private static async Task<(WebApplication App, HttpClient Client, FakeRepository Repository)> StartAsync()
    {
        var repository = new FakeRepository();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Dashboard:WriteToken"] = Token });
        builder.Services.AddAuthentication(DashboardWriteAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DashboardWriteAuthentication>(
                DashboardWriteAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAgentDefinitionRepository>(repository);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        AgentEndpoints.Map(app);
        await app.StartAsync();
        var address = app.Urls.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) }, repository);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? token, object? body = null)
    {
        var message = new HttpRequestMessage(method, url);
        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }

        return message;
    }

    [Theory]
    [InlineData("POST", "/api/agents")]
    [InlineData("PUT", "/api/agents/1")]
    [InlineData("PATCH", "/api/agents/1/enabled")]
    [InlineData("DELETE", "/api/agents/1")]
    public async Task Writes_RequireValidToken(string method, string url)
    {
        var (app, client, repository) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        AgentEndpoints.Create(repository, Valid());
        object body = method == "PATCH" ? new { enabled = true } : new { name = "X" };

        foreach (var token in new string?[] { null, "wrong", "" })
        {
            using var response = await client.SendAsync(Request(new HttpMethod(method), url, token, body));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Single(repository.Rows);
        Assert.False(repository.Rows[0].Deleted);
        Assert.False(repository.Find(1)!.Enabled);
    }

    [Fact]
    public async Task FullLifecycle_WorksOverHttp()
    {
        var (app, client, repository) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var anonymousList = await client.GetAsync("/api/agents");
        Assert.Equal(HttpStatusCode.OK, anonymousList.StatusCode);

        var body = new
        {
            name = "Dev", role = "Role", behavior = "B", llmProvider = "Claude",
            capabilities = new[] { new { capability = "Jira", enabled = true } }, projects = new[] { "DFAI" }
        };
        using var created = await client.SendAsync(Request(HttpMethod.Post, "/api/agents", Token, body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/agents/1", created.Headers.Location!.ToString());
        var json = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Dev", json.GetProperty("name").GetString());
        Assert.Equal("Claude", json.GetProperty("llmProvider").GetString());
        Assert.Equal(JsonValueKindNull, json.GetProperty("status").ValueKind.ToString());

        using var bad = await client.SendAsync(Request(HttpMethod.Post, "/api/agents", Token, new { name = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("error", await bad.Content.ReadAsStringAsync());

        using var updated = await client.SendAsync(Request(
            HttpMethod.Put, "/api/agents/1", Token, new { name = "Dev2", role = "R", behavior = "B", llmProvider = "Copilot" }));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var patched = await client.SendAsync(
            Request(HttpMethod.Patch, "/api/agents/1/enabled", Token, new { enabled = true }));
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.True(repository.Find(1)!.Enabled);

        using var get = await client.GetAsync("/api/agents/1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete, "/api/agents/1", Token));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await client.GetAsync("/api/agents/1");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Single(repository.Rows);
    }

    private const string JsonValueKindNull = "Null";

    [Fact]
    public async Task ListProjects_ReturnsSortedKeysWithoutToken()
    {
        var (app, client, repository) = await StartAsync();
        await using var _ = app;
        repository.Projects = ["ZED", "ABC", "MID"];
        var response = await client.SendAsync(Request(HttpMethod.Get, "/api/agents/projects", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["ABC", "MID", "ZED"], await response.Content.ReadFromJsonAsync<string[]>());
    }

    [Fact]
    public async Task List_IncludesStatusFieldsUsedByTheUi()
    {
        var (app, client, repository) = await StartAsync();
        await using var _ = app;
        var heartbeat = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        repository.Rows.Add((new AgentView(1, "A", "R", "B", "Claude", null, true, DateTime.UtcNow, DateTime.UtcNow,
            [], ["DFAI"], new AgentStatusView("Running", "DFAI-1", "Builder", heartbeat)), false));
        var json = await client.GetStringAsync("/api/agents");
        Assert.Contains("\"currentIssueKey\":\"DFAI-1\"", json);
        Assert.Contains("\"state\":\"Running\"", json);
        Assert.Contains("\"heartbeatAt\"", json);
    }
}
