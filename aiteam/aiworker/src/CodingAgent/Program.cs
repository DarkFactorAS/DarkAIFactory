using CodingAgent;
using CodingAgent.Repository;
using AiTeam.Configuration;
using DFCommonLib.Config;
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
new DFServices(builder.Services).SetupLogger().SetupMySql().LogToConsole(DFLogLevel.INFO);
builder.Services.AddScoped<IntegrationSettingsRepository>();
builder.Services.AddScoped<ManagerPlanReader>();
using var host = builder.Build();
var appSettings = host.Services.GetRequiredService<IConfigurationHelper>().Settings as AgentSettings
    ?? throw new InvalidOperationException("Expected typed agent JSON settings.");
Console.WriteLine(
    $"Connecting to database '{appSettings.DatabaseConnection.Database}' " +
    $"at {appSettings.DatabaseConnection.Server}:{appSettings.DatabaseConnection.Port}.");
using var databaseScope = host.Services.CreateScope();
var settings = databaseScope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>()
    .Load("aiworker", appSettings.PollIntervalSeconds);
var configuration = AgentConfiguration.Load(settings);
DFLogger.LogOutput(DFLogLevel.INFO, "Startup", $"Starting {appSettings.AppName}");

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

using var jira = new JiraClient(configuration);
var copilot = new CopilotRunner(configuration);
await copilot.CheckAvailableAsync(cancellation.Token);

Console.WriteLine(
    $"Coding agent polling Jira for eligible work every {configuration.PollInterval.TotalSeconds:0} seconds. " +
    $"Repository: {configuration.RepositoryUrl}");

try
{
    while (!cancellation.IsCancellationRequested)
    {
        IReadOnlyList<JiraIssue> issues;
        try
        {
            issues = await jira.FindWorkAsync(cancellation.Token);
        }
        catch (Exception exception) when (
            !cancellation.IsCancellationRequested &&
            (exception is HttpRequestException || exception is OperationCanceledException))
        {
            Console.Error.WriteLine(
                $"Jira polling failed: {exception.Message}. Retrying in {configuration.PollInterval.TotalSeconds:0} seconds.");
            await Task.Delay(configuration.PollInterval, cancellation.Token);
            continue;
        }

        foreach (var issue in issues)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Console.WriteLine($"Claiming {issue.Key}: {issue.Summary}");

            string? managerPlan = null;
            try
            {
                using var planScope = host.Services.CreateScope();
                managerPlan = planScope.ServiceProvider.GetRequiredService<ManagerPlanReader>().Find(issue.Key);
            }
            catch (Exception exception) when (!cancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"Could not read the manager plan for {issue.Key}: {exception.Message}. Falling back to local planning.");
            }

            string? workspacePath = null;
            string? branchName = null;
            string? pushedBranchName = null;
            string? pullRequestUrl = null;
            var inProgressTransitionAttempted = false;
            var resultCommentPosted = false;
            try
            {
                await jira.AddLabelAsync(issue.Key, configuration.PickupLabel, cancellation.Token);
                try
                {
                    inProgressTransitionAttempted = true;
                    await jira.TransitionToAsync(issue.Key, configuration.InProgressStatus, cancellation.Token);
                }
                catch (Exception exception) when (!cancellation.IsCancellationRequested)
                {
                    Console.Error.WriteLine(
                        $"Could not move {issue.Key} to {configuration.InProgressStatus}: {exception.Message}");
                    try
                    {
                        await jira.AddCommentAsync(
                            issue.Key,
                            $"The coding agent could not move this issue to \"{configuration.InProgressStatus}\": {LimitForComment(exception.Message)}\n\nWork continues.",
                            cancellation.Token);
                    }
                    catch (Exception notificationException) when (!cancellation.IsCancellationRequested)
                    {
                        Console.Error.WriteLine(
                            $"Could not post the transition warning for {issue.Key}: {notificationException.Message}");
                    }
                }

                var delivered = false;
                try
                {
                    workspacePath = GitHubRepository.CreateWorkspacePath();
                    var createdBranchName = await GitHubRepository.CloneForIssueAsync(
                        configuration,
                        workspacePath,
                        issue.Key,
                        cancellation.Token);
                    branchName = createdBranchName;
                    string plan;
                    if (managerPlan is not null)
                    {
                        Console.WriteLine($"Using the manager's stored plan for {issue.Key}.");
                        plan = managerPlan;
                    }
                    else
                    {
                        Console.WriteLine($"No manager plan found for {issue.Key}; generating a plan locally.");
                        plan = await copilot.RunAsync(
                            workspacePath,
                            PromptBuilder.BuildPlanPrompt(issue),
                            allowChanges: false,
                            cancellation.Token);
                    }

                    var result = await copilot.RunAsync(
                        workspacePath,
                        PromptBuilder.BuildImplementationPrompt(issue, plan),
                        allowChanges: true,
                        cancellation.Token);
                    var pushResult = await GitHubRepository.CommitAndPushAsync(
                        configuration,
                        workspacePath,
                        createdBranchName,
                        issue.Key,
                        issue.Summary,
                        cancellation.Token);
                    pushedBranchName = pushResult.PushedBranchName;
                    var message = pushResult.Message;
                    if (pushedBranchName is not null)
                    {
                        pullRequestUrl = await GitHubRepository.CreatePullRequestAsync(
                            configuration,
                            pushedBranchName,
                            issue.Key,
                            issue.Summary,
                            cancellation.Token);
                        message += $" Pull request: {pullRequestUrl}";
                    }
                    else
                    {
                        message += " No pull request was created and the issue status was left unchanged.";
                    }

                    await jira.AddCommentAsync(
                        issue.Key,
                        $"Implementation result (generated by the coding agent):\n\n{LimitForComment(result)}\n\n{message}",
                        cancellation.Token);
                    resultCommentPosted = true;
                    if (pullRequestUrl is not null)
                    {
                        await jira.TransitionToAsync(issue.Key, configuration.InReviewStatus, cancellation.Token);
                    }

                    delivered = true;
                    Console.WriteLine($"Completed {issue.Key}.");
                }
                finally
                {
                    if (workspacePath is not null && delivered)
                    {
                        try
                        {
                            GitHubRepository.DeleteWorkspace(workspacePath);
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException)
                        {
                            Console.Error.WriteLine(
                                $"Could not remove temporary workspace for {issue.Key}: {exception.Message}");
                        }
                    }
                    else if (workspacePath is not null)
                    {
                        var branchStatus = pushedBranchName is null
                            ? branchName is null ? string.Empty : $" Local branch: {branchName}."
                            : $" Pushed branch: {pushedBranchName}.";
                        Console.Error.WriteLine(
                            $"Retained temporary workspace for {issue.Key} at {workspacePath}.{branchStatus}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Processing {issue.Key} failed: {exception.Message}");
                await ReportFailureAsync(
                    jira,
                    configuration,
                    issue,
                    exception,
                    pushedBranchName,
                    branchName,
                    workspacePath,
                    pullRequestUrl,
                    inProgressTransitionAttempted,
                    resultCommentPosted,
                    cancellation.Token);
            }
        }

        await Task.Delay(configuration.PollInterval, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Coding agent stopped.");
}

static string LimitForComment(string text) => PromptBuilder.LimitForComment(text);

static async Task ReportFailureAsync(
    JiraClient jira,
    AgentConfiguration configuration,
    JiraIssue issue,
    Exception exception,
    string? pushedBranchName,
    string? branchName,
    string? workspacePath,
    string? pullRequestUrl,
    bool inProgressTransitionAttempted,
    bool resultCommentPosted,
    CancellationToken cancellationToken)
{
    var statusRestored = false;
    if (pushedBranchName is null &&
        inProgressTransitionAttempted &&
        issue.StatusName.Length > 0 &&
        !string.Equals(issue.StatusName, configuration.InProgressStatus, StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            await jira.TransitionToAsync(issue.Key, issue.StatusName, cancellationToken);
            statusRestored = true;
        }
        catch (Exception restorationException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"Could not restore the status of {issue.Key} to {issue.StatusName}: {restorationException.Message}");
        }
    }

    try
    {
        await jira.AddLabelAsync(issue.Key, configuration.FailureLabel, cancellationToken);
        var recoveryDetails = pushedBranchName is not null
            ? pullRequestUrl is null
                ? $"The changes were already pushed to `{pushedBranchName}`. Create or locate a pull request for this branch, post its URL to Jira, and move the issue to \"{configuration.InReviewStatus}\". Do not rerun the implementation."
                : resultCommentPosted
                    ? $"The pull request is {pullRequestUrl} and its result is posted to Jira. Retry moving the issue to \"{configuration.InReviewStatus}\" without rerunning the implementation."
                    : $"The pull request is {pullRequestUrl}. Post its URL and the implementation result to Jira, then move the issue to \"{configuration.InReviewStatus}\". Do not rerun the implementation."
            : $"{(branchName is not null ? $"The local branch `{branchName}` is retained in the worker workspace." : workspacePath is not null ? "The worker workspace is retained for recovery." : "No workspace was created.")} " +
              (statusRestored
                  ? $"The issue status was restored to \"{issue.StatusName}\"."
                  : inProgressTransitionAttempted && issue.StatusName.Length == 0
                      ? "The issue's original status could not be determined; verify its status before retrying."
                  : inProgressTransitionAttempted && issue.StatusName.Length > 0 &&
                    !string.Equals(issue.StatusName, configuration.InProgressStatus, StringComparison.OrdinalIgnoreCase)
                      ? $"The issue may still be in \"{configuration.InProgressStatus}\"; move it back to \"{issue.StatusName}\" before retrying."
                      : "The issue status was not changed by the worker.");
        await jira.AddCommentAsync(
            issue.Key,
            $"The coding agent could not complete this task: {LimitForComment(exception.Message)}\n\n" +
            (pushedBranchName is not null
                ? $"The `{configuration.PickupLabel}` label remains in place to prevent automatic duplicate work. Do not remove it to rerun implementation.\n\n"
                : $"The `{configuration.PickupLabel}` label remains in place. Remove it after resolving the failure to allow the issue to be picked up again.\n\n") +
            recoveryDetails,
            cancellationToken);
    }
    catch (Exception notificationException)
    {
        Console.Error.WriteLine(
            $"Could not report the failure for {issue.Key} to Jira: {notificationException.Message}. " +
            $"Pushed branch: {pushedBranchName ?? "none"}; local branch: {branchName ?? "none"}.");
    }
}
