using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AiDashboard.Tasks;

public sealed class DashboardWriteAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DashboardWrite";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = configuration["Dashboard:WriteToken"];
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(expected) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]))))
        {
            return Task.FromResult(AuthenticateResult.Fail("A valid dashboard write token is required."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "dashboard-writer")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
