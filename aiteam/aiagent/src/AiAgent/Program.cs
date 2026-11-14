using AiAgent.Capabilities.Jira;
using AiAgent.Capabilities.Builder;
using AiAgent.Capabilities.Planning;
using AiAgent.Capabilities.Review;
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
using PlanningJiraClient = AiAgent.Providers.JiraClient;

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
using var runnerClient = new RunnerClient();
var capabilities = host.Services.GetServices<IAgentCapability>().Append(new PlanningCapability(
    () => new IntegrationSettingsRepository(connectionFactory).Load("aiagent", pollSeconds),
    () => new WorkflowLabelRepository(connectionFactory).Load(PlanningCapability.AgentName),
    () => new ManagerProjectRepository(connectionFactory).GetProjects(),
    new AgentPlanRepository(connectionFactory),
    settings => new PlanningJiraClient(settings.Jira),
    new RunnerPlanningRunner(runnerClient, new AgentPullRequestRepository(connectionFactory))))
    .Append(jiraCapability);
var reviewSettings = new IntegrationSettingsRepository(connectionFactory).Load("aiagent", pollSeconds);
if (string.IsNullOrWhiteSpace(reviewSettings.GitHub.GitHubRepositoryToken))
{
    DFLogger.LogOutput(
        DFLogLevel.WARNING,
        "Startup",
        "GitHub.GitHubRepositoryToken is not set; the Review capability is not registered.");
}
else
{
    string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    var repositoryToken = reviewSettings.GitHub.GitHubRepositoryToken.Trim();
    capabilities = capabilities.Append(new ReviewCapability(
        new ReviewOptions(
            repositoryToken,
            Env("AIAGENT_REVIEWER_LOGIN") ?? "copilot-pull-request-reviewer",
            MaxAttempts: 3,
            CopilotToken: reviewSettings.GitHub.AgentGitHubToken,
            WorkspaceRoot: RunnerPolicy.WorkspaceRoot)
        {
            RunnerTimeout = TimeSpan.FromMinutes(Math.Max(1, appSettings.RunnerTimeoutMinutes))
        },
        new AgentPullRequestRepository(connectionFactory),
        new AgentReviewAttemptRepository(connectionFactory),
        new GitRunner(repositoryToken),
        runnerClient));
}
var agentHost = new AgentHost(
    agentRepository,
    capabilities,
    new AgentClaimLock(connectionFactory),
    TimeSpan.FromSeconds(pollSeconds),
    new AgentStatusRepository(connectionFactory));
await agentHost.RunAsync(cancellation.Token);
