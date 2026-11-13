using AiAgent.Capabilities.Jira;
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

// Capabilities Planning, Builder and Review are registered by later sub-tasks.
var connectionFactory = host.Services.GetRequiredService<DFCommonLib.DataAccess.IDbConnectionFactory>();
var agentRepository = new AgentRepository(connectionFactory);
var pollSeconds = Math.Max(5, appSettings.PollIntervalSeconds);
JiraCapabilityConfig LoadJiraConfig() => JiraCapabilityConfig.From(
    new IntegrationSettingsRepository(connectionFactory).Load("aiagent", pollSeconds),
    new WorkflowLabelRepository(connectionFactory).Load("aiagent"));
var jiraCapability = new JiraCapability(
    LoadJiraConfig,
    config => new JiraClient(config),
    new AgentTaskRepository(connectionFactory),
    new AgentPullRequestRepository(connectionFactory),
    new GitHubMergeChecker(),
    key => new ManagerProjectRepository(connectionFactory).GetProjects()
        .Where(project => string.Equals(project.JiraProjectKey, key, StringComparison.OrdinalIgnoreCase))
        .Select(project => Uri.TryCreate(project.TargetRepositoryUrl, UriKind.Absolute, out var url) ? url : null)
        .FirstOrDefault());
JiraCapability.LogIntakeWarning(agentRepository, LoadJiraConfig);
var agentHost = new AgentHost(
    agentRepository,
    host.Services.GetServices<IAgentCapability>().Append(jiraCapability),
    new AgentClaimLock(connectionFactory),
    TimeSpan.FromSeconds(pollSeconds));
await agentHost.RunAsync(cancellation.Token);
