using AiTeam.Configuration;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReviewWorker;
using ReviewWorker.Repository;
using System.Runtime.InteropServices;

if (args is ["--runner-server"])
{
    await ReviewRunnerServer.RunAsync();
    return;
}

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AgentSettings>>();
new DFServices(builder.Services).SetupLogger().SetupMySql().LogToConsole(DFLogLevel.INFO);
builder.Services.AddScoped<IntegrationSettingsRepository>();
builder.Services.AddScoped<PullRequestReader>();
builder.Services.AddScoped<ReviewCommentAttemptRepository>();
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

// The review worker shares the aiworker settings client (tokens, repository URL).
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

AgentConfiguration? loggedConfiguration = null;
var cycle = 0L;
try
{
    while (!cancellation.IsCancellationRequested)
    {
        cycle++;
        try
        {
            DFLogger.LogOutput(DFLogLevel.INFO, "Worker", $"Cycle {cycle}: started.");
            using var scope = host.Services.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>()
                .Load("aiworker", appSettings.PollIntervalSeconds);
            var configuration = AgentConfiguration.Load(settings, appSettings);
            if (loggedConfiguration != configuration)
            {
                loggedConfiguration = configuration;
                DFLogger.LogOutput(
                    DFLogLevel.INFO,
                    "Worker",
                    $"{appSettings.AppName} review worker polling every {configuration.PollInterval.TotalSeconds:0} seconds. " +
                    $"Reviewer: {configuration.ReviewerLogin}. " +
                    $"Max attempts per pull request: {configuration.MaxAttemptsPerPullRequest}. " +
                    $"GitHub repository: {configuration.GitHubRepositoryUrl}");
            }

            await ReviewCycleScanner.ScanAsync(cycle, configuration, scope.ServiceProvider, cancellation.Token);

            // Review comment fixing is added in follow-up sub-tasks of DFAI-137.
            DFLogger.LogOutput(
                DFLogLevel.INFO,
                "Worker",
                $"Cycle {cycle}: done. Comment fixing is not enabled yet; sleeping {configuration.PollInterval.TotalSeconds:0} seconds.");
            await Task.Delay(configuration.PollInterval, cancellation.Token);
        }
        catch (Exception exception) when (
            !cancellation.IsCancellationRequested && exception is not InvalidOperationException)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "Worker", $"Polling cycle failed: {exception.Message}. Retrying next cycle.");
            await Task.Delay(TimeSpan.FromSeconds(AgentConfiguration.MinimumPollIntervalSeconds), cancellation.Token);
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}

DFLogger.LogOutput(DFLogLevel.INFO, "Worker", "Review worker stopped.");
