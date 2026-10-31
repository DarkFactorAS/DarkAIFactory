using aibuildpage.Repository;
using DFCommonLib.Config;
using DFCommonLib.DataAccess;
using DFCommonLib.Logger;
using DFCommonLib.Utils;

const string Html = "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>DarkFactor AI Team</title></head><body></body></html>";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AppSettings>>();
new DFServices(builder.Services)
    .SetupLogger()
    .SetupMySql()
    .LogToConsole(DFLogLevel.INFO);

builder.Services.AddTransient<IStartupDatabasePatcher, AiDatabasePatcher>();

var app = builder.Build();

var databasePatcher = DFServices.GetService<IStartupDatabasePatcher>();
databasePatcher.WaitForConnection();
if (databasePatcher.RunPatcher())
{
    DFLogger.LogOutput(DFLogLevel.INFO, "Startup", "Database patcher ran successfully");
}
else
{
    DFLogger.LogOutput(DFLogLevel.ERROR, "Startup", "Database patcher failed");
    Environment.Exit(1);
    return;
}

app.MapGet("/", () => Results.Content(Html, "text/html"));

app.Run();
