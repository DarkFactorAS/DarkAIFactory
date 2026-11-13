using AiAgent.Capabilities.Builder;
using AiAgent.Core;
using AiAgent.GitHub;
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
builder.Services.AddSingleton<IAgentCapability>(services =>
{
    var factory = services.GetRequiredService<DFCommonLib.DataAccess.IDbConnectionFactory>();
    var pullRequests = new AgentPullRequestRepository(factory);
    return new BuilderCapability(
        () => BuilderSettings.Load(
            new IntegrationSettingsRepository(factory).Load("aiagent"),
            new WorkflowLabelRepository(factory).Load("aiagent")),
        () => new ManagerProjectRepository(factory).GetProjects(),
        new AgentPlanRepository(factory),
        settings => new BuilderJiraClient(settings),
        (url, settings) => new GitHubCore(
            new GitRunner(settings.GitHubRepositoryToken), url, settings.GitHubRepositoryToken, pullRequests),
        new RunnerClient(),
        RunnerPolicy.WorkspaceRoot);
});
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

// Builder is registered above; Jira, Planning and Review are registered by later sub-tasks.
var connectionFactory = host.Services.GetRequiredService<DFCommonLib.DataAccess.IDbConnectionFactory>();
var agentHost = new AgentHost(
    new AgentRepository(connectionFactory),
    host.Services.GetServices<IAgentCapability>(),
    new AgentClaimLock(connectionFactory),
    TimeSpan.FromSeconds(Math.Max(5, appSettings.PollIntervalSeconds)));
await agentHost.RunAsync(cancellation.Token);
