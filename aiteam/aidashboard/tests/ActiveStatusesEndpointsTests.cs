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

public sealed class ActiveStatusesEndpointsTests
{
    private const string Token = "test-write-token";

    private sealed class NoopLabelsStore : IAiLabelsStore
    {
        public void Save(IReadOnlyList<string> labels) { }
    }

    private sealed class FakeStore : IActiveStatusesStore
    {
        public List<IReadOnlyList<string>> Saved { get; } = [];
        public bool Fail { get; set; }

        public void Save(IReadOnlyList<string> statuses)
        {
            if (Fail) throw new InvalidOperationException("secret");
            Saved.Add(statuses);
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, FakeStore Store, IntegrationSettings Settings)> StartAsync()
    {
        var store = new FakeStore();
        var settings = new IntegrationSettings();
        settings.Dashboard.ActiveStatuses = ["Old"];
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Dashboard:WriteToken"] = Token });
        builder.Services.AddAuthentication(DashboardWriteAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DashboardWriteAuthentication>(
                DashboardWriteAuthentication.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IActiveStatusesStore>(store);
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
        var message = new HttpRequestMessage(HttpMethod.Put, "/api/settings/active-statuses")
        {
            Content = JsonContent.Create(body)
        };
        if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    [Fact]
    public async Task GetReturnsConfiguredStatuses()
    {
        var (app, client, _, _) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var json = await client.GetFromJsonAsync<JsonElement>("/api/settings/active-statuses");
        Assert.Equal("Old", json.GetProperty("statuses")[0].GetString());
    }

    [Fact]
    public async Task PutWithoutTokenIsUnauthorized()
    {
        var (app, client, store, _) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var response = await client.SendAsync(Put(null, new { statuses = new[] { "A" } }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task PutRejectsInvalidInput()
    {
        var (app, client, store, _) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        var tooMany = Enumerable.Range(0, 21).Select(i => $"S{i}").ToArray();
        foreach (var statuses in new[] { tooMany, ["a,b"], [new string('x', 256)] })
        {
            var response = await client.SendAsync(Put(Token, new { statuses }));
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
        var response = await client.SendAsync(Put(Token, new { statuses = new[] { " In Progress ", "in progress", "", "Review" } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["In Progress", "Review"], store.Saved.Single());
        Assert.Equal(["In Progress", "Review"], settings.Dashboard.ActiveStatuses);
    }

    [Fact]
    public async Task PutReturns500WhenStoreFailsAndKeepsSetting()
    {
        var (app, client, store, settings) = await StartAsync();
        await using var _a = app;
        using var _c = client;
        store.Fail = true;
        var response = await client.SendAsync(Put(Token, new { statuses = new[] { "A" } }));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync());
        Assert.Equal(["Old"], settings.Dashboard.ActiveStatuses);
    }
}
