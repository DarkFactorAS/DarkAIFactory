using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AiDashboard.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiDashboard.Tests;

public sealed class DashboardHttpsTests
{
    private sealed class OkLogin : IDashboardLogin
    {
        public Task<DashboardLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken) =>
            Task.FromResult(new DashboardLoginResult(DashboardLoginStatus.Ok, username, 1));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public async Task Production_ServesHttpsWithSelfSignedCertificate_AndLoginSucceeds()
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration[DashboardHttps.PortKey] = port.ToString();
        builder.UseDashboardHttps();
        builder.Services.AddDashboardAuthentication();
        builder.Services.AddSingleton<IDashboardLogin>(new OkLogin());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        AuthEndpoints.Map(app);
        await app.StartAsync();
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };

            var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "thor", password = "secret12" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"),
                c => c.StartsWith(DashboardAuthentication.CookieName + "=", StringComparison.Ordinal) &&
                     c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void SelfSignedCertificate_HasPrivateKey_AndCoversLocalhost()
    {
        using var certificate = DashboardHttps.CreateSelfSignedCertificate();

        Assert.True(certificate.HasPrivateKey);
        Assert.True(certificate.MatchesHostname("localhost"));
        Assert.True(certificate.NotAfter > DateTime.Now.AddMonths(11));
    }
}
