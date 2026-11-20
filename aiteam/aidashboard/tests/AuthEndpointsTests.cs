using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccountClientModule.Client;
using AccountCommon.SharedModel;
using AiDashboard.Auth;
using AiDashboard.Tasks;
using AiTeam.Configuration;
using DFCommonLib.Config;
using DFCommonLib.HttpApi;
using DFCommonLib.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class AuthEndpointsTests
{
    private sealed class FakeLogin(DashboardLoginResult result) : IDashboardLogin
    {
        public List<(string Username, string Password)> Calls { get; } = [];

        public Task<DashboardLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken)
        {
            Calls.Add((username, password));
            return Task.FromResult(result);
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(IDashboardLogin login)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDashboardAuthentication();
        builder.Services.AddSingleton(login);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/health", () => Results.Ok()).AllowAnonymous();
        // No explicit authorization metadata: protected by the fallback policy.
        app.MapGet("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapPost("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapPut("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapPatch("/api/protected", () => Results.Ok(new { ok = true }));
        app.MapDelete("/api/protected", () => Results.Ok(new { ok = true }));
        AuthEndpoints.Map(app);
        app.MapFallback(() => Results.Text("<html></html>", "text/html")).AllowAnonymous();
        await app.StartAsync();
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        return (app, new HttpClient(handler) { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string? username = "thor", string? password = "secret12") =>
        client.PostAsJsonAsync("/api/auth/login", new { username, password });

    private static async Task<string?> Error(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();

    [Fact]
    public async Task Login_Ok_SetsHttpOnlySessionCookie_AndMeReturnsUser()
    {
        var login = new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor", 7));
        var (app, client) = await StartAsync(login);
        await using var _ = app;

        using var response = await Login(client, "  thor  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("thor", "secret12"), Assert.Single(login.Calls));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(DashboardAuthentication.CookieName + "=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret12", cookie);

        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("Thor", (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());

        using var protectedResponse = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task Login_InvalidCredentials_Returns401WithoutCookie()
    {
        var (app, client) = await StartAsync(new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.InvalidCredentials)));
        await using var _ = app;

        using var response = await Login(client);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid username or password.", await Error(response));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var write = await client.PostAsync("/api/protected", null);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Theory]
    [InlineData(null, "secret12")]
    [InlineData("   ", "secret12")]
    [InlineData("thor", "")]
    [InlineData("thor", null)]
    public async Task Login_MissingCredentials_Returns400WithoutCallingAccountServer(string? username, string? password)
    {
        var login = new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor"));
        var (app, client) = await StartAsync(login);
        await using var _ = app;

        using var response = await Login(client, username, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(login.Calls);
    }

    [Fact]
    public async Task Login_TooLongUsername_Returns400()
    {
        var login = new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor"));
        var (app, client) = await StartAsync(login);
        await using var _ = app;

        using var response = await Login(client, new string('a', AuthEndpoints.MaxUsernameLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(login.Calls);
    }

    [Theory]
    [InlineData(DashboardLoginStatus.Unavailable, HttpStatusCode.BadGateway)]
    [InlineData(DashboardLoginStatus.NotConfigured, HttpStatusCode.ServiceUnavailable)]
    public async Task Login_ServerProblems_ReturnErrorWithoutCookie(DashboardLoginStatus status, HttpStatusCode expected)
    {
        var (app, client) = await StartAsync(new FakeLogin(new DashboardLoginResult(status)));
        await using var _ = app;

        using var response = await Login(client);

        Assert.Equal(expected, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(await Error(response)));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task WithoutSession_ReadsArePublic_WhileHealthAndSpaArePublic()
    {
        var (app, client) = await StartAsync(new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor")));
        await using var _ = app;

        using var read = await client.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/protected"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var spa = await client.GetAsync("/some/page");
        Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task WithoutSession_WritesReturn401NotRedirect_AndSucceedAfterLogin(string method)
    {
        var (app, client) = await StartAsync(new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor")));
        await using var _ = app;

        using var anonymous = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/protected"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);

        using (var loginResponse = await Login(client)) Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        using var loggedIn = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/protected"));
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        var (app, client) = await StartAsync(new FakeLogin(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor")));
        await using var _ = app;
        using (var loginResponse = await Login(client)) Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        using var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var write = await client.PostAsync("/api/protected", null);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task CommentRouteRequiresAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new IntegrationSettings());
        builder.Services.AddScoped<IJiraCommentClient>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IActiveAiTaskService>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        JiraCommentEndpoint.Map(app);
        var routeBuilder = (IEndpointRouteBuilder)app;
        var endpoint = Assert.Single(routeBuilder.DataSources.SelectMany(source => source.Endpoints));
        Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    private sealed class FakeConfig(AppSettings settings) : IConfigurationHelper
    {
        public AppSettings Settings => settings;
    }

    private sealed class FakeAccountClient(Func<LoginData, AccountData> login) : IAccountClient
    {
        public string? Endpoint { get; private set; }
        public (string? Id, string? Secret, string? Scope) Credentials { get; private set; }
        public LoginData? LastLogin { get; private set; }

        public void SetEndpoint(string endpoint) => Endpoint = endpoint;
        public Task<WebAPIData> Ping() => throw new NotSupportedException();
        public void SetAuthCredentials(string clientId, string clientSecret, string scope) => Credentials = (clientId, clientSecret, scope);

        public AccountData LoginAccount(LoginData loginData)
        {
            LastLogin = loginData;
            return login(loginData);
        }

        public AccountData LoginToken(LoginTokenData loginData) => throw new NotSupportedException();
        public AccountData LoginGameCenter(LoginGameCenterData loginData) => throw new NotSupportedException();
        public AccountData CreateAccount(CreateAccountData createAccountData) => throw new NotSupportedException();
        public ReturnData ResetPasswordWithEmail(string email) => throw new NotSupportedException();
        public ReturnData ResetPasswordWithCode(string code, string email) => throw new NotSupportedException();
        public ReturnData ResetPasswordWithToken(string token, string password) => throw new NotSupportedException();
    }

    private static DashboardAppSettings Configured() => new()
    {
        AccountServer = new AccountServerSettings
        {
            Endpoint = "https://account.example", ClientId = "id", ClientSecret = "client-secret", Scope = "scope",
        },
    };

    [Fact]
    public void AddDashboardAccountLogin_ResolvesRealAccountClient()
    {
        // Regression: AccountRestClient's constructor reads the static DFServices provider and
        // threw "DFServices has not been initialized" when DFServices.Create was never called.
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationHelper>(new FakeConfig(Configured()));
        services.AddDashboardAccountLogin();
        new DFServices(services).SetupLogger();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<AccountServerLogin>(scope.ServiceProvider.GetRequiredService<IDashboardLogin>());
    }

    [Fact]
    public async Task AccountServerLogin_Ok_UsesConfiguredServerAndEncryptsPassword()
    {
        var client = new FakeAccountClient(_ => new AccountData { errorCode = AccountData.ErrorCode.OK, nickname = "Thor", id = 42, token = "t" });
        var login = new AccountServerLogin(client, new FakeConfig(Configured()));

        var result = await login.LoginAsync("thor", "secret12", CancellationToken.None);

        Assert.Equal(new DashboardLoginResult(DashboardLoginStatus.Ok, "Thor", 42), result);
        Assert.Equal("https://account.example", client.Endpoint);
        Assert.Equal(("id", "client-secret", "scope"), client.Credentials);
        Assert.Equal("thor", client.LastLogin!.username);
        Assert.False(string.IsNullOrEmpty(client.LastLogin.password));
        Assert.NotEqual("secret12", client.LastLogin.password);
    }

    [Theory]
    [InlineData(AccountData.ErrorCode.WrongPassword)]
    [InlineData(AccountData.ErrorCode.UserDoesNotExist)]
    public async Task AccountServerLogin_RejectedAccount_IsInvalidCredentials(AccountData.ErrorCode code)
    {
        var login = new AccountServerLogin(
            new FakeAccountClient(_ => new AccountData { errorCode = code }), new FakeConfig(Configured()));

        var result = await login.LoginAsync("thor", "secret12", CancellationToken.None);

        Assert.Equal(DashboardLoginStatus.InvalidCredentials, result.Status);
    }

    [Fact]
    public async Task AccountServerLogin_ClientThrows_IsUnavailable()
    {
        var login = new AccountServerLogin(
            new FakeAccountClient(_ => throw new HttpRequestException("down")), new FakeConfig(Configured()));

        var result = await login.LoginAsync("thor", "secret12", CancellationToken.None);

        Assert.Equal(DashboardLoginStatus.Unavailable, result.Status);
    }

    [Fact]
    public async Task AccountServerLogin_WithoutEndpoint_IsNotConfigured()
    {
        var client = new FakeAccountClient(_ => throw new InvalidOperationException("must not be called"));
        var login = new AccountServerLogin(client, new FakeConfig(new DashboardAppSettings()));

        var result = await login.LoginAsync("thor", "secret12", CancellationToken.None);

        Assert.Equal(DashboardLoginStatus.NotConfigured, result.Status);
        Assert.Null(client.LastLogin);
    }
}
