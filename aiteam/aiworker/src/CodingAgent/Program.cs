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
builder.Services.AddSingleton<IConfigurationHelper, ConfigurationHelper<AgentSettings>>();
new DFServices(builder.Services).SetupLogger().SetupMySql().LogToConsole(DFLogLevel.INFO);
builder.Services.AddScoped<IntegrationSettingsRepository>();
builder.Services.AddScoped<WorkflowLabelRepository>();
builder.Services.AddScoped<ManagerPlanReader>();
builder.Services.AddScoped<ManagerProjectRepository>();
builder.Services.AddScoped<PullRequestRepository>();
builder.Services.AddScoped<JiraIssueClaimRepository>();
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
using var databaseScope = host.Services.CreateScope();
var settings = databaseScope.ServiceProvider.GetRequiredService<IntegrationSettingsRepository>()
    .Load("aiworker", appSettings.PollIntervalSeconds);
var labels = databaseScope.ServiceProvider.GetRequiredService<WorkflowLabelRepository>().Load("aiworker");
// Fail at startup when a required label row is missing.
foreach (var key in new[] { "PickupLabel", "FailureLabel", "AnalyzedLabel" })
{
    labels.Get(key);
}
if (settings.PollIntervalSeconds < 5)
{
    throw new InvalidOperationException("PollIntervalSeconds must be at least 5 seconds.");
}
var pollInterval = TimeSpan.FromSeconds(settings.PollIntervalSeconds);
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

var loggedConfigurations = new HashSet<AgentConfiguration>();

try
{
    while (!cancellation.IsCancellationRequested)
    {
        using var scope = host.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<ManagerProjectRepository>().GetProjects();
        AgentConfiguration[] configurations = projects.Count == 0
            ? [AgentConfiguration.Load(settings, labels)]
            : projects.Select(project => AgentConfiguration.Load(settings, labels, project)).ToArray();
        foreach (var configuration in configurations)
        {
            using var jira = new JiraClient(configuration);
            var copilot = new CopilotRunner(configuration);
            var pullRequests = scope.ServiceProvider.GetRequiredService<PullRequestRepository>();
            try
            {
                await new PullRequestRecordRecovery(configuration, jira, pullRequests)
                    .RecoverAsync(cancellation.Token);
            }
            catch (Exception exception) when (!cancellation.IsCancellationRequested)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    "Worker",
                    $"Pull-request tracking recovery failed for {configuration.GitHubRepositoryUrl}: {exception.Message}");
            }

            try
            {
                var sanityActions = await new PullRequestSanityCheck(
                        configuration,
                        pullRequests,
                        jira.GetIssueAsync,
                        new GitHubSanityOperations(configuration))
                    .RunAsync(cancellation.Token);
                foreach (var action in sanityActions)
                {
                    DFLogger.LogOutput(
                        DFLogLevel.INFO,
                        "Worker",
                        $"Pull-request sanity check {action.Kind} {action.JiraKey} (PR #{action.PullRequestNumber}) {action.NewPullRequestUrl}".TrimEnd());
                }
            }
            catch (Exception exception) when (!cancellation.IsCancellationRequested)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    "Worker",
                    $"Pull-request sanity check failed for {configuration.GitHubRepositoryUrl}: {exception.Message}");
            }

            if (loggedConfigurations.Add(configuration))
            {
                await copilot.CheckAvailableAsync(cancellation.Token);
                DFLogger.LogOutput(
                    DFLogLevel.INFO,
                    "Worker",
                    $"Coding agent polling every {pollInterval.TotalSeconds:0} seconds. " +
                    $"Manager project: {configuration.ManagerProjectId?.ToString() ?? "legacy single-repository mode"}. " +
                    $"GitHub repository: {configuration.GitHubRepositoryUrl}");
            }
            IReadOnlyList<JiraIssue> issues;
            try
            {
                issues = await jira.FindWorkAsync(cancellation.Token);
            }
            catch (Exception exception) when (
                !cancellation.IsCancellationRequested &&
                (exception is HttpRequestException || exception is OperationCanceledException))
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR,
                    "Worker",
                    $"Jira polling failed for {configuration.GitHubRepositoryUrl}: {exception.Message}. Retrying next cycle.");
                continue;
            }

            foreach (var candidate in issues)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                JiraIssue issue;
                try
                {
                    issue = await jira.GetIssueAsync(candidate.Key, cancellation.Token);
                }
                catch (Exception exception) when (
                    !cancellation.IsCancellationRequested &&
                    exception is HttpRequestException or OperationCanceledException)
                {
                    DFLogger.LogOutput(DFLogLevel.ERROR, "Worker", $"Could not refresh {candidate.Key} before claiming: {exception.Message}. Retrying next cycle.");
                    continue;
                }
                if (issue.Labels.Contains(configuration.PickupLabel) ||
                    !string.Equals(issue.StatusName, candidate.StatusName, StringComparison.Ordinal))
                {
                    continue;
                }
                var managerPlan = scope.ServiceProvider.GetRequiredService<ManagerPlanReader>()
                    .Find(issue.Key, configuration.ManagerProjectId);
                if (managerPlan is not null &&
                    !managerPlan.CanImplement(issue.Summary, issue.Description, configuration.GitHubRepositoryUrl))
                {
                    DFLogger.LogOutput(DFLogLevel.WARNING, "Worker", $"Skipping {issue.Key}: the manager plan is stale or not yet posted. Waiting for refreshed planning.");
                    await ReportIgnoredAsync(jira, issue.Key, "the manager plan is stale or not yet posted. Waiting for refreshed planning.", cancellation.Token);
                    continue;
                }
                if (configuration.RequiresManagerPlan &&
                    (managerPlan is null || !issue.Labels.Contains(configuration.AnalyzedLabel)))
                {
                    var reason = managerPlan is null
                        ? "no manager plan exists for the selected project. Waiting for the manager to plan it."
                        : $"the manager plan exists, but the \"{configuration.AnalyzedLabel}\" handoff label is missing. Waiting for the manager to complete the handoff.";
                    DFLogger.LogOutput(
                        DFLogLevel.WARNING,
                        "Worker",
                        $"Skipping {issue.Key}: {reason}");
                    await ReportIgnoredAsync(jira, issue.Key, reason, cancellation.Token);
                    continue;
                }
                var openBlockers = JiraWorkflowRules.OpenBlockers(issue.Blockers, configuration.DoneStatus);
                if (openBlockers.Count > 0)
                {
                    var blockedReason =
                        $"it is blocked by tasks that are not {configuration.DoneStatus} yet: {JiraWorkflowRules.DescribeBlockers(openBlockers)}.";
                    DFLogger.LogOutput(DFLogLevel.WARNING, "Worker", $"Skipping {issue.Key}: {blockedReason}");
                    await ReportIgnoredAsync(jira, issue.Key, blockedReason, cancellation.Token);
                    continue;
                }
                await IgnoredTaskNotifier.ClearAsync(
                    issue.Key,
                    issue.Labels,
                    jira.RemoveLabelAsync,
                    message => DFLogger.LogOutput(DFLogLevel.WARNING, "Worker", message),
                    cancellation.Token);
                using var issueClaim = scope.ServiceProvider.GetRequiredService<JiraIssueClaimRepository>()
                    .TryAcquire(issue.Key);
                if (issueClaim is null)
                {
                    DFLogger.LogOutput(DFLogLevel.INFO, "Worker", $"Another worker is already processing {issue.Key}.");
                    continue;
                }

                DFLogger.LogOutput(DFLogLevel.INFO, "Worker", $"Claiming {issue.Key}: {issue.Summary}");

                string? workspacePath = null;
                string? branchName = null;
                string? pushedBranchName = null;
                string? pullRequestUrl = null;
                var inProgressTransitionAttempted = false;
                var resultCommentPosted = false;
                try
                {
                    await jira.AddLabelAsync(issue.Key, configuration.PickupLabel, cancellation.Token);
                    if (!string.IsNullOrEmpty(configuration.AssigneeAccountId))
                    {
                        try
                        {
                            await jira.AssignAsync(issue.Key, configuration.AssigneeAccountId, cancellation.Token);
                        }
                        catch (Exception exception) when (!cancellation.IsCancellationRequested)
                        {
                            DFLogger.LogOutput(DFLogLevel.ERROR, "Worker", $"Could not assign {issue.Key}: {exception.Message}");
                            try
                            {
                                await jira.AddCommentAsync(
                                    issue.Key,
                                    $"The coding agent could not assign this issue: {LimitForComment(exception.Message)}\n\nWork continues.",
                                    cancellation.Token);
                            }
                            catch (Exception notificationException) when (!cancellation.IsCancellationRequested)
                            {
                                DFLogger.LogOutput(
                                    DFLogLevel.ERROR,
                                    "Worker",
                                    $"Could not post the assignment warning for {issue.Key}: {notificationException.Message}");
                            }
                        }
                    }

                    try
                    {
                        inProgressTransitionAttempted = true;
                        await jira.TransitionToAsync(issue.Key, configuration.InProgressStatus, cancellation.Token);
                    }
                    catch (Exception exception) when (!cancellation.IsCancellationRequested)
                    {
                        DFLogger.LogOutput(
                            DFLogLevel.ERROR,
                            "Worker",
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
                            DFLogger.LogOutput(
                                DFLogLevel.ERROR,
                                "Worker",
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
                            cancellation.Token,
                            issue.ParentKey);
                        branchName = createdBranchName;
                        string plan;
                        if (managerPlan is not null)
                        {
                            DFLogger.LogOutput(DFLogLevel.INFO, "Worker", $"Using the manager's stored plan for {issue.Key}.");
                            plan = managerPlan.Plan;
                        }
                        else
                        {
                            DFLogger.LogOutput(DFLogLevel.INFO, "Worker", $"No manager plan found for {issue.Key}; generating a plan locally.");
                            plan = await copilot.RunAsync(
                                workspacePath,
                                PromptBuilder.BuildPlanPrompt(issue),
                                allowChanges: false,
                                cancellation.Token);
                        }

                        var beforeImplementation = await jira.GetIssueAsync(issue.Key, cancellation.Token);
                        if (ManagerPlan.Fingerprint(beforeImplementation.Summary, beforeImplementation.Description, configuration.GitHubRepositoryUrl) !=
                            ManagerPlan.Fingerprint(issue.Summary, issue.Description, configuration.GitHubRepositoryUrl))
                        {
                            throw new InvalidOperationException("The Jira requirements changed after pickup. Refresh the manager plan before retrying implementation.");
                        }
                        var result = await copilot.RunAsync(
                            workspacePath,
                            PromptBuilder.BuildImplementationPrompt(
                                issue,
                                plan,
                                issue.ParentKey is null ? null : JiraWorkflowRules.ParentBranchName(issue.ParentKey)),
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
                                cancellation.Token,
                                baseBranch: issue.ParentKey is null ? null : JiraWorkflowRules.ParentBranchName(issue.ParentKey));
                            message += $" Pull request: {pullRequestUrl}";
                            try
                            {
                                pullRequests.Record(issue.Key, configuration.GitHubRepositoryUrl, pullRequestUrl);
                            }
                            catch (Exception exception) when (!cancellation.IsCancellationRequested)
                            {
                                // The PR already exists; a bookkeeping failure must not rerun the implementation.
                                DFLogger.LogOutput(
                                    DFLogLevel.ERROR,
                                    "Worker",
                                    $"Could not record pull request {pullRequestUrl} for {issue.Key} in the database: {exception.Message}");
                                try
                                {
                                    await jira.AddCommentAsync(
                                        issue.Key,
                                        $"{PullRequestRecordRecovery.PendingCommentPrefix}{pullRequestUrl}",
                                        cancellation.Token);
                                    await jira.AddLabelAsync(
                                        issue.Key,
                                        PullRequestRecordRecovery.PendingLabel,
                                        cancellation.Token);
                                }
                                catch (Exception recoveryException) when (!cancellation.IsCancellationRequested)
                                {
                                    DFLogger.LogOutput(
                                        DFLogLevel.ERROR,
                                        "Worker",
                                        $"Could not schedule record-only recovery for {pullRequestUrl}: {recoveryException.Message}");
                                }
                            }
                        }
                        else
                        {
                            message += " No pull request was created and the issue status was left unchanged.";
                        }

                        await jira.AddCommentAsync(
                            issue.Key,
                            PromptBuilder.BuildResultComment(result, message, managerPlan),
                            cancellation.Token);
                        resultCommentPosted = true;
                        if (pullRequestUrl is not null)
                        {
                            await jira.TransitionToAsync(issue.Key, configuration.InReviewStatus, cancellation.Token);
                        }

                        delivered = true;
                        DFLogger.LogOutput(DFLogLevel.INFO, "Worker", $"Completed {issue.Key}.");
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
                                DFLogger.LogOutput(
                                    DFLogLevel.WARNING,
                                    "Worker",
                                    $"Could not remove temporary workspace for {issue.Key}: {exception.Message}");
                            }
                        }
                        else if (workspacePath is not null)
                        {
                            var branchStatus = pushedBranchName is null
                                ? branchName is null ? string.Empty : $" Local branch: {branchName}."
                                : $" Pushed branch: {pushedBranchName}.";
                            DFLogger.LogOutput(
                                DFLogLevel.WARNING,
                                "Worker",
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
                    DFLogger.LogOutput(DFLogLevel.ERROR, "Worker", $"Processing {issue.Key} failed: {exception.Message}");
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
        }
        await Task.Delay(pollInterval, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    DFLogger.LogOutput(DFLogLevel.INFO, "Worker", "Coding agent stopped.");
}

static string LimitForComment(string text) => PromptBuilder.LimitForComment(text);

static Task ReportIgnoredAsync(JiraClient jira, string issueKey, string reason, CancellationToken cancellationToken) =>
    IgnoredTaskNotifier.NotifyAsync(
        "AIWorker",
        issueKey,
        reason,
        jira.GetCommentsAsync,
        jira.AddCommentAsync,
        jira.AddLabelAsync,
        message => DFLogger.LogOutput(DFLogLevel.WARNING, "Worker", message),
        cancellationToken);

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
    var requirementsChanged = exception is InvalidOperationException &&
        exception.Message.StartsWith("The Jira requirements changed after pickup.", StringComparison.Ordinal);
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
            DFLogger.LogOutput(
                DFLogLevel.ERROR,
                "Worker",
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
                : requirementsChanged
                    ? $"The `{configuration.PickupLabel}` and `{configuration.FailureLabel}` labels remain in place. Remove both to allow the manager to refresh the plan and the worker to retry.\n\n"
                    : $"The `{configuration.PickupLabel}` label remains in place. Remove it after resolving the failure to allow the issue to be picked up again.\n\n") +
            recoveryDetails,
            cancellationToken);
    }
    catch (Exception notificationException)
    {
        DFLogger.LogOutput(
            DFLogLevel.ERROR,
            "Worker",
            $"Could not report the failure for {issue.Key} to Jira: {notificationException.Message}. " +
            $"Pushed branch: {pushedBranchName ?? "none"}; local branch: {branchName ?? "none"}.");
    }
}
