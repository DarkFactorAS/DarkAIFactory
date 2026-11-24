using AccountClientModule.Client;
using DFCommonLib.Utils;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace AiDashboard.Auth;

/// <summary>
/// Cookie session for logged-in dashboard users. The dashboard is read-only without a session:
/// read requests (GET/HEAD) are open to everyone, while every other method (POST, PUT, PATCH,
/// DELETE) requires a logged-in user unless the endpoint is marked AllowAnonymous (login, logout).
/// Endpoints with explicit RequireAuthorization (for example /api/auth/me) always need a session.
/// Calls without a session get 401 instead of a redirect so the frontend can offer the login page.
/// </summary>
public static class DashboardAuthentication
{
    public const string CookieName = "aidashboard_auth";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>Lifetime of the cookie when the user ticks "Remember me" (sliding).</summary>
    public static readonly TimeSpan RememberMeLifetime = TimeSpan.FromDays(7);

    /// <summary>Fallback policy: anonymous users may read (GET/HEAD); any write needs a session.</summary>
    public static readonly AuthorizationPolicy ReadOnlyForAnonymousPolicy = new AuthorizationPolicyBuilder()
        .RequireAssertion(context =>
            context.User.Identity?.IsAuthenticated == true ||
            (context.Resource is HttpContext http && IsReadOnly(http.Request.Method)))
        .Build();

    public static bool IsReadOnly(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    public static IServiceCollection AddDashboardAuthentication(this IServiceCollection services, string? dataProtectionKeysPath = null)
    {
        // The auth cookie is encrypted with Data Protection keys. Without a persisted key ring every
        // restart generates new keys and all existing cookies (including "Remember me") stop working.
        var dataProtection = services.AddDataProtection().SetApplicationName("aidashboard");
        if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
        }
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
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
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IWebHostEnvironment>((options, environment) =>
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always);
        services.AddOptions<SessionOptions>()
            .Configure<IWebHostEnvironment>((options, environment) =>
                options.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always);
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(ReadOnlyForAnonymousPolicy);
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
