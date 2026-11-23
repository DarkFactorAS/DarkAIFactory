using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiDashboard.Tasks;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class CardLabelsEndpointsTests
{
    private const string Token = TestAuthHandler.Token;

    private sealed class NoopLabelsStore : IAiLabelsStore
    {
        public void Save(IReadOnlyList<string> labels) { }
    }

    private sealed class FakeStore : ICardLabelsStore
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
        settings.Dashboard.CardLabels = ["old"];
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ICardLabelsStore>(store);
        builder.Services.AddSingleton<IAiLabelsStore>(new NoopLabelsStore());
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
        var message = new HttpRequestMessage(HttpMethod.Put, "/api/settings/card-labels")
        {
            Content = JsonContent.Create(body)
        };
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    [Fact]
    public async Task GetReturnsConfiguredLabels()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var json = await client.GetFromJsonAsync<JsonElement>("/api/settings/card-labels");
        Assert.Equal("old", json.GetProperty("labels")[0].GetString());
    }

    [Fact]
    public async Task PutWithoutTokenIsUnauthorized()
    {
        var (app, client, store, _) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var response = await client.SendAsync(Put(null, new { labels = new[] { "a" } }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task PutRejectsInvalidInput()
    {
        var (app, client, store, _) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var tooMany = Enumerable.Range(0, 21).Select(i => $"l{i}").ToArray();
        foreach (var labels in new[] { tooMany, ["a,b"], ["has space"], [new string('x', 256)] })
        {
            var response = await client.SendAsync(Put(Token, new { labels }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task PutNormalizesSavesAndUpdatesLiveSetting()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var response = await client.SendAsync(Put(Token, new { labels = new[] { " ai-approved ", "ai-approved", "", "other" } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["ai-approved", "other"], store.Saved.Single());
        Assert.Equal(["ai-approved", "other"], settings.Dashboard.CardLabels);
    }

    [Fact]
    public async Task PutReturns500WhenStoreFailsAndKeepsSetting()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        store.Fail = true;
        var response = await client.SendAsync(Put(Token, new { labels = new[] { "a" } }));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
        Assert.Equal(["old"], settings.Dashboard.CardLabels);
    }
}
