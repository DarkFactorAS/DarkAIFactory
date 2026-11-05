using AiTeam.Configuration;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;
using JiraManagerAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AgentSettings>>();
new DFServices(builder.Services)
    .SetupLogger()
    .SetupMySql()
    .LogToConsole(DFLogLevel.INFO);
builder.Services.AddScoped<SharedSettingsRepository>();
builder.Services.AddScoped<IAgentTaskProcessorRepository, AgentTaskRepository>();

using var host = builder.Build();
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Loading configuration for environment '{builder.Environment.EnvironmentName}': " +
    $"Config/appsettings.{builder.Environment.EnvironmentName}.json");
var appSettings = host.Services.GetRequiredService<IConfigurationHelper>().Settings as AgentSettings
    ?? throw new InvalidOperationException("Expected typed agent JSON settings.");
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"Connecting to database '{appSettings.DatabaseConnection.Database}' " +
    $"at {appSettings.DatabaseConnection.Server}:{appSettings.DatabaseConnection.Port}.");
AgentConfiguration.LoadPollInterval(appSettings.PollIntervalSeconds);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
using var sigtermRegistration = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    });

AgentConfiguration configuration;
using (var startupScope = host.Services.CreateScope())
{
    configuration = startupScope.ServiceProvider.GetRequiredService<SharedSettingsRepository>()
        .Load(appSettings.PollIntervalSeconds);
}

DFLogger.LogOutput(
    DFLogLevel.INFO, "JiraManager",
    $"Jira manager creating Jira issues for new tasks every {configuration.PollInterval.TotalSeconds:0} seconds.");
using var jira = new JiraClient(configuration);
var attempts = new Dictionary<long, int>();
try
{
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            await new AgentTaskProcessor(
                    scope.ServiceProvider.GetRequiredService<IAgentTaskProcessorRepository>(),
                    jira, configuration.JiraStatusName, configuration.MaxAttempts, attempts)
                .ProcessNewTasksAsync(cancellation.Token);
        }
        catch (Exception exception) when (!cancellation.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraManager", $"Task polling failed: {exception.Message}. Retrying next cycle.");
        }

        await Task.Delay(configuration.PollInterval, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    DFLogger.LogOutput(DFLogLevel.INFO, "JiraManager", "Jira manager agent stopped.");
}
