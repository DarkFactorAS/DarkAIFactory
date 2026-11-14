using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiDashboard.Tasks;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class DashboardSettingsEndpointsTests
{
    private const string Token = "test-write-token";

    private sealed class FakeStore : IAiLabelsStore
    {
        public List<IReadOnlyList<string>> Saved { get; } = [];
        public bool Fail { get; set; }

        public void Save(IReadOnlyList<string> labels)
        {
            if (Fail) throw new InvalidOperationException("secret");
            Saved.Add(labels);
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, FakeStore Store, IntegrationSettings Settings)> StartAsync()
    {
        var store = new FakeStore();
        var settings = new IntegrationSettings();
        settings.Dashboard.AiLabels = ["old"];
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Dashboard:WriteToken"] = Token });
        builder.Services.AddAuthentication(DashboardWriteAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DashboardWriteAuthentication>(
                DashboardWriteAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAiLabelsStore>(store);
        builder.Services.AddSingleton(settings);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        DashboardSettingsEndpoints.Map(app);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, store, settings);
    }

    private static HttpRequestMessage Put(string? token, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Put, "/api/settings/ai-labels")
        {
            Content = JsonContent.Create(body)
        };
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Put_WithoutValidToken_Returns401(string? token)
    {
        var (app, client, store, _) = await StartAsync();
        await using var _app = app;
        using var response = await client.SendAsync(Put(token, new { labels = new[] { "a" } }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Put_InvalidLabels_Returns400()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _app = app;
        var invalid = new object[]
        {
            new { labels = (string[]?)null },
            new { labels = new[] { "has space" } },
            new { labels = new[] { "a,b" } },
            new { labels = new[] { new string('x', 256) } },
            new { labels = Enumerable.Range(0, 21).Select(i => "l" + i).ToArray() },
        };
        foreach (var body in invalid)
        {
            using var response = await client.SendAsync(Put(Token, body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty(store.Saved);
        Assert.Equal(["old"], settings.Dashboard.AiLabels);
    }

    [Fact]
    public async Task Put_Valid_PersistsUpdatesRuntimeAndGetReturnsNewList()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _app = app;
        using var response = await client.SendAsync(Put(Token, new { labels = new[] { " ai-a ", "ai-b", "ai-a" } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["ai-a", "ai-b"], store.Saved.Single());
        Assert.Equal(["ai-a", "ai-b"], settings.Dashboard.AiLabels);

        var json = await client.GetFromJsonAsync<JsonElement>("/api/settings/ai-labels");
        Assert.Equal(["ai-a", "ai-b"], json.GetProperty("labels").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Put_EmptyList_IsAllowed()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _app = app;
        using var response = await client.SendAsync(Put(Token, new { labels = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(settings.Dashboard.AiLabels);
        Assert.Empty(store.Saved.Single());
    }

    [Fact]
    public async Task Put_DatabaseFailure_Returns500WithoutDetailsAndKeepsLabels()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _app = app;
        store.Fail = true;
        using var response = await client.SendAsync(Put(Token, new { labels = new[] { "x" } }));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
        Assert.Equal(["old"], settings.Dashboard.AiLabels);
    }

    [Fact]
    public void Normalization_IsSharedWithStartupLoader()
    {
        var parsed = IntegrationSettingsRepository.ParseAiLabels(" a, b,,a ,c");
        Assert.Equal(["a", "b", "c"], parsed);
        Assert.Equal(parsed, IntegrationSettingsRepository.NormalizeAiLabels([" a", "b", "", "a", "c"]));
    }
}
