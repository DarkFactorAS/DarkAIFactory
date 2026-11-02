using ManagerAgent;
using AiTeam.Configuration;
using ManagerAgent.Repository;
using DFCommonLib.Config;
using DFCommonLib.DataAccess;
using DFCommonLib.Logger;
using DFCommonLib.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = Host.CreateApplicationBuilder(args);
Console.WriteLine(
    $"Loading configuration for environment '{builder.Environment.EnvironmentName}': " +
    $"Config/appsettings.{builder.Environment.EnvironmentName}.json");
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AgentSettings>>();
new DFServices(builder.Services)
    .SetupLogger()
    .SetupMySql()
    .LogToConsole(DFLogLevel.INFO);
builder.Services.AddTransient<IStartupDatabasePatcher, AiDatabasePatcher>();
builder.Services.AddScoped<ManagerProjectRepository>();
builder.Services.AddScoped<ManagerPlanRepository>();
builder.Services.AddScoped<IntegrationSettingsRepository>();

using var host = builder.Build();
var appSettings = host.Services.GetRequiredService<IConfigurationHelper>().Settings as AgentSettings
    ?? throw new InvalidOperationException("Expected typed agent JSON settings.");
var pollInterval = AgentConfiguration.LoadPollInterval(appSettings.PollIntervalSeconds);
using var databaseScope = host.Services.CreateScope();
var databasePatcher = databaseScope.ServiceProvider.GetRequiredService<IStartupDatabasePatcher>();
databasePatcher.WaitForConnection();
if (!databasePatcher.RunPatcher())
{
    DFLogger.LogOutput(DFLogLevel.ERROR, "Startup", "Database patcher failed");
    Environment.ExitCode = 1;
    return;
}

DFLogger.LogOutput(DFLogLevel.INFO, "Startup", "Database patcher ran successfully");
var settings = databaseScope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>()
    .Load("aimanager", appSettings.PollIntervalSeconds);

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

Console.WriteLine($"Manager agent planning Jira tasks every {pollInterval.TotalSeconds:0} seconds.");
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
            Console.WriteLine("No manager projects configured in manager_projects. Waiting for project mappings.");
            loggedNoProjects = true;
        }

        foreach (var project in projects)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var configuration = AgentConfiguration.Load(settings, project);
            using var jira = new JiraClient(configuration);
            var copilot = new CopilotRunner(configuration);
            if (loggedProjects.Add(project))
            {
                Console.WriteLine(
                    $"Polling manager project {project.Id}: {project.JiraProjectKey}. Repository: {configuration.RepositoryUrl}");
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
                Console.Error.WriteLine(
                    $"Jira polling failed for manager project {project.Id}: {exception.Message}. Retrying next cycle.");
                continue;
            }

            foreach (var issue in issues)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var storedPlan = plans.Find(issue.Key);
                    if (storedPlan is null)
                    {
                        await copilot.CheckAvailableAsync(cancellation.Token);
                        var workspacePath = GitHubRepository.CreateWorkspacePath();
                        string plan;
                        try
                        {
                            await GitHubRepository.CloneForPlanningAsync(configuration, workspacePath, cancellation.Token);
                            plan = await copilot.RunAsync(
                                workspacePath, PromptBuilder.BuildPlanPrompt(issue), cancellation.Token);
                        }
                        finally
                        {
                            GitHubRepository.DeleteWorkspace(workspacePath);
                        }

                        plans.Save(project.Id, issue.Key, plan);
                        storedPlan = new ManagerPlan(plan, false);
                    }

                    if (!storedPlan.PostedToJira)
                    {
                        foreach (var comment in PromptBuilder.BuildPlanComments(storedPlan.Plan))
                        {
                            await jira.AddCommentAsync(issue.Key, comment, cancellation.Token);
                        }

                        plans.MarkPostedToJira(issue.Key);
                    }

                    await jira.AddLabelAsync(issue.Key, configuration.AnalyzedLabel, cancellation.Token);
                    if (!string.Equals(configuration.PickupLabel, configuration.AnalyzedLabel, StringComparison.Ordinal))
                    {
                        await jira.AddLabelAsync(issue.Key, configuration.PickupLabel, cancellation.Token);
                    }
                    Console.WriteLine($"Plan for {issue.Key} stored in Jira and manager_plans.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Planning {issue.Key} failed: {exception.Message}. Retrying next cycle.");
                    try
                    {
                        await jira.AddLabelAsync(issue.Key, configuration.FailureLabel, cancellation.Token);
                        await jira.AddCommentAsync(
                            issue.Key,
                            $"The manager could not complete planning: {PromptBuilder.LimitForComment(exception.Message)}\n\n" +
                            "No code was implemented. Any saved plan will be reused when planning is retried.",
                            cancellation.Token);
                    }
                    catch (Exception notificationException) when (!cancellation.IsCancellationRequested)
                    {
                        Console.Error.WriteLine(
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
    Console.WriteLine("Manager agent stopped.");
}
