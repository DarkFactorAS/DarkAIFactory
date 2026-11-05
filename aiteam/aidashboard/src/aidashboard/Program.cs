using AiTeam.Configuration;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;

const string Html = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>DarkAIFactoy - AI Team workflow</title></head><body><h1>DarkAIFactoy - AI Team workflow</h1></body></html>";

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AppSettings>>();
new DFServices(builder.Services)
    .SetupLogger()
    .SetupMySql()
    .LogToConsole(DFLogLevel.INFO);

builder.Services.AddScoped<IntegrationSettingsRepository>();
builder.Services.AddSingleton(services =>
{
    using var scope = services.CreateScope();
    return scope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>().Load("aidashboard");
});

var app = builder.Build();

var appSettings = app.Services.GetRequiredService<IConfigurationHelper>().Settings;
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Loading configuration for environment '{builder.Environment.EnvironmentName}': " +
    $"Config/appsettings.{builder.Environment.EnvironmentName}.json");
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Connecting to database '{appSettings.DatabaseConnection.Database}' " +
    $"at {appSettings.DatabaseConnection.Server}:{appSettings.DatabaseConnection.Port}.");

// Load credentials server-side only; never expose them in page responses.
_ = app.Services.GetRequiredService<IntegrationSettings>();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));

app.UseDefaultFiles();
app.UseStaticFiles();

// SPA fallback; unknown /api routes stay 404.
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        context.Response.ContentType = "text/html";
        return context.Response.WriteAsync(Html);
    }

    context.Response.ContentType = "text/html";
    return context.Response.SendFileAsync(index);
});

app.Run();
