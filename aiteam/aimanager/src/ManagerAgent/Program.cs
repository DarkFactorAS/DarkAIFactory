using ManagerAgent;
using AiTeam.Configuration;
using ManagerAgent.Repository;
using DFCommonLib.Config;
using DFCommonLib.Logger;
using DFCommonLib.Utils;
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
builder.Services.AddScoped<ManagerProjectRepository>();
builder.Services.AddScoped<ManagerPlanRepository>();
builder.Services.AddScoped<PullRequestRepository>();
builder.Services.AddScoped<IntegrationSettingsRepository>();
builder.Services.AddScoped<WorkflowLabelRepository>();

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
var pollInterval = AgentConfiguration.LoadPollInterval(appSettings.PollIntervalSeconds);
using var databaseScope = host.Services.CreateScope();
var settings = databaseScope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>()
    .Load("aimanager", appSettings.PollIntervalSeconds);
var labelRepository = databaseScope.ServiceProvider.GetRequiredService<WorkflowLabelRepository>();
var labels = labelRepository.Load("aimanager");
var workerLabels = labelRepository.Load("aiworker");
// Fail at startup when a required label row is missing.
foreach (var key in new[]
    { "PickupLabel", "FailureLabel", "AnalyzedLabel", "ClarificationLabel", "ApprovalLabel", "ApprovedLabel" })
{
    labels.Get(key);
}
foreach (var key in new[] { "PickupLabel", "AnalyzedLabel" })
{
    workerLabels.Get(key);
}
if (!string.Equals(labels.Get("AnalyzedLabel"), workerLabels.Get("AnalyzedLabel"), StringComparison.Ordinal))
{
    throw new InvalidOperationException("Manager and worker analyzed labels must match for the plan handoff.");
}

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

DFLogger.LogOutput(DFLogLevel.INFO, "Manager", $"Manager agent planning Jira tasks every {pollInterval.TotalSeconds:0} seconds.");
var loggedProjects = new HashSet<ManagerProject>();
var loggedNoProjects = false;
try
{
    while (!cancellation.IsCancellationRequested)
    {
        using var scope = host.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<ManagerProjectRepository>().GetProjects();
        var plans = scope.ServiceProvider.GetRequiredService<ManagerPlanRepository>();
        if (projects.Count == 0 && !loggedNoProjects)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, "Manager", "No manager projects configured in manager_projects. Waiting for project mappings.");
            loggedNoProjects = true;
        }

        foreach (var project in projects)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var configuration = AgentConfiguration.Load(settings, project, labels, workerLabels);
            using var jira = new JiraClient(configuration);
            var copilot = new CopilotRunner(configuration);
            if (loggedProjects.Add(project))
            {
                DFLogger.LogOutput(
                    DFLogLevel.INFO,
                    "Manager",
                    $"Polling manager project {project.Id}: {project.JiraProjectKey}. GitHub repository: {configuration.GitHubRepositoryUrl}");
            }
            try
            {
                await new PullRequestCloser(
                    configuration,
                    scope.ServiceProvider.GetRequiredService<PullRequestRepository>(),
                    jira,
                    (record, token) => GitHubRepository.IsPullRequestMergedAsync(
                        configuration, record.RepositoryUrl, record.Number, token))
                    .CloseMergedAsync(project.JiraProjectKey, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    "Manager",
                    $"Pull request handling failed for manager project {project.Id}: {exception.Message}. Continuing with new tasks.");
            }

            IReadOnlyList<JiraIssue> issues;
            try
            {
                issues = await jira.FindWorkAsync(cancellation.Token);
            }
            catch (Exception exception) when (
                !cancellation.IsCancellationRequested &&
                exception is HttpRequestException or OperationCanceledException)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    "Manager",
                    $"Jira polling failed for manager project {project.Id}: {exception.Message}. Retrying next cycle.");
                continue;
            }

            var planner = new ManagerPlanner(configuration, plans, jira, async (issue, token) =>
            {
                await copilot.CheckAvailableAsync(token);
                var workspacePath = GitHubRepository.CreateWorkspacePath();
                try
                {
                    await GitHubRepository.CloneForPlanningAsync(configuration, workspacePath, token);
                    return await copilot.RunAsync(workspacePath, PromptBuilder.BuildTriagePrompt(issue), token);
                }
                finally
                {
                    GitHubRepository.DeleteWorkspace(workspacePath);
                }
            });
            foreach (var issue in issues)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    await planner.PlanAsync(project.Id, issue, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    DFLogger.LogOutput(DFLogLevel.ERROR, "Manager", $"Planning {issue.Key} failed: {exception.Message}. Pausing automatic retries with the failure label.");
                    try
                    {
                        await jira.AddLabelAsync(issue.Key, configuration.FailureLabel, cancellation.Token);
                        await jira.AddCommentAsync(
                            issue.Key,
                            $"The manager could not complete planning: {PromptBuilder.LimitForComment(exception.Message)}\n\n" +
                            $"No code was implemented. Remove the `{configuration.FailureLabel}` label after resolving the failure to retry planning. " +
                            "Any saved plan will be reused.",
                            cancellation.Token);
                    }
                    catch (Exception notificationException) when (!cancellation.IsCancellationRequested)
                    {
                        DFLogger.LogOutput(
                            DFLogLevel.ERROR,
                            "Manager",
                            $"Could not report planning failure for {issue.Key}: {notificationException.Message}");
                    }
                }
            }
        }

        await Task.Delay(pollInterval, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    DFLogger.LogOutput(DFLogLevel.INFO, "Manager", "Manager agent stopped.");
}
