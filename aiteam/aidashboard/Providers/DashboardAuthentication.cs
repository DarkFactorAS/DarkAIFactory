using AccountClientModule.Client;
using DFCommonLib.Utils;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

namespace AiDashboard.Auth;

/// <summary>
/// Cookie session for logged-in dashboard users. Every endpoint requires a session unless it is
/// marked AllowAnonymous (health, login, logout and the SPA shell). API calls without a session get
/// 401 instead of a redirect so the frontend can show the login page.
/// </summary>
public static class DashboardAuthentication
{
    public const string CookieName = "aidashboard_auth";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public static IServiceCollection AddDashboardAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = SessionLifetime;
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }

    /// <summary>
    /// Registers the DFCommonLib account client and the login service (same setup as DFWeb and
    /// GameServer). Pass the application's service collection: the account client's REST client
    /// resolves its dependencies through the static DFServices provider, which is built lazily from
    /// this collection on first use. The client also keeps its state in the ASP.NET session.
    /// </summary>
    public static IServiceCollection AddDashboardAccountLogin(this IServiceCollection services)
    {
        DFServices.Create(services);
        services.AddHttpContextAccessor();
        services.AddDistributedMemoryCache();
        services.AddSession(options =>
        {
            options.Cookie.Name = "aidashboard_session";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        AccountClient.SetupService(services);
        services.AddScoped<IDashboardLogin, AccountServerLogin>();
        return services;
    }
}
