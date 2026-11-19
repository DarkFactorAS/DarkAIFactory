using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDashboard.Tests;

/// <summary>
/// Test stand-in for a logged-in dashboard user: a request with <c>Authorization: Bearer test-write-token</c>
/// is authenticated, anything else is not. Lets endpoint tests check authorization without the cookie login.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string Token = "test-write-token";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.Authorization.ToString() != "Bearer " + Token)
        {
            return Task.FromResult(AuthenticateResult.Fail("Not logged in."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
