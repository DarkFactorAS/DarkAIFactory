using AiDashboard.Tasks;
using AiTeam.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiDashboard.Tests;

public sealed class DashboardWriteAuthenticationTests
{
    [Theory]
    [InlineData(null, "test-write-token", false)]
    [InlineData("", "test-write-token", false)]
    [InlineData("test-write-token", "", false)]
    [InlineData("test-write-token", "wrong", false)]
    [InlineData("test-write-token", "test-write-token", true)]
    public async Task AuthenticatesOnlyConfiguredWriteToken(string? configured, string supplied, bool expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Dashboard:WriteToken"] = configured }).Build());
        services.AddAuthentication(DashboardWriteAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DashboardWriteAuthentication>(
                DashboardWriteAuthentication.SchemeName, _ => { });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = string.IsNullOrEmpty(supplied) ? "" : "Bearer " + supplied;

        var result = await context.AuthenticateAsync();

        Assert.Equal(expected, result.Succeeded);
        if (expected) Assert.True(result.Principal!.Identity!.IsAuthenticated);
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
}
