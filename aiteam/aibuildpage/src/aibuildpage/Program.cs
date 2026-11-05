using AiTeam.Configuration;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;

const string Html = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>DarkFactor AI Team</title></head><body></body></html>";

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
    return scope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>().Load("aibuildpage");
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

app.MapGet("/", () => Results.Content(Html, "text/html"));

app.Run();
