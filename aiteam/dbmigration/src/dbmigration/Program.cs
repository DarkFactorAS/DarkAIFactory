using dbmigration.Repository;
using DFCommonLib.Config;
using DFCommonLib.DataAccess;
using DFCommonLib.Logger;
using DFCommonLib.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AppSettings>>();
new DFServices(builder.Services)
    .SetupLogger()
    .SetupMySql()
    .LogToConsole(DFLogLevel.INFO);
builder.Services.AddTransient<IStartupDatabasePatcher, AiDatabasePatcher>();

using var host = builder.Build();
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Loading configuration for environment '{builder.Environment.EnvironmentName}': " +
    $"Config/appsettings.{builder.Environment.EnvironmentName}.json");
var settings = host.Services.GetRequiredService<IConfigurationHelper>().Settings;
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Migration",
    $"Connecting to database '{settings.DatabaseConnection.Database}' " +
    $"at {settings.DatabaseConnection.Server}:{settings.DatabaseConnection.Port}.");
using var scope = host.Services.CreateScope();
var patcher = scope.ServiceProvider.GetRequiredService<IStartupDatabasePatcher>();
patcher.WaitForConnection();
MigrationHistory.Initialize(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>());
if (!patcher.RunPatcher())
{
    DFLogger.LogOutput(DFLogLevel.ERROR, "Migration", "Database migration failed");
    Environment.ExitCode = 1;
    return;
}

DFLogger.LogOutput(DFLogLevel.INFO, "Migration", "Database migration completed successfully");
