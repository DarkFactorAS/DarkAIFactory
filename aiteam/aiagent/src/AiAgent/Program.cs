using AiAgent.Core;
using AiAgent.Repositories;
using AiAgent.Runner;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;

if (args is ["--runner-server"])
{
    await RunnerServer.RunAsync();
    return;
}

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AgentSettings>>();
new DFServices(builder.Services).SetupLogger().SetupMySql().LogToConsole(DFLogLevel.INFO);
builder.Services.AddScoped<IntegrationSettingsRepository>();
using var host = builder.Build();
var appSettings = host.Services.GetRequiredService<IConfigurationHelper>().Settings as AgentSettings
    ?? throw new InvalidOperationException("Expected typed agent JSON settings.");
DFLogger.LogOutput(
    DFLogLevel.INFO,
    "Startup",
    $"AiAgent orchestrator started for environment '{builder.Environment.EnvironmentName}'.");

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

// Capabilities (Jira, Planning, Builder, Review) are registered by later sub-tasks.
var connectionFactory = host.Services.GetRequiredService<DFCommonLib.DataAccess.IDbConnectionFactory>();
var agentHost = new AgentHost(
    new AgentRepository(connectionFactory),
    host.Services.GetServices<IAgentCapability>(),
    new AgentClaimLock(connectionFactory),
    TimeSpan.FromSeconds(Math.Max(5, appSettings.PollIntervalSeconds)));
await agentHost.RunAsync(cancellation.Token);
