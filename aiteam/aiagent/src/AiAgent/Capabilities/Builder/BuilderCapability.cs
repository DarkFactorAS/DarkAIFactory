using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.LanguageModel;
using AiAgent.Repositories;
using AiAgent.Runner;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Builder;

/// <summary>
/// Implements planned Jira issues: plan gate, claim, branch, runner in 'implement' mode (build and test),
/// pull request through the GitHub core, Jira 'In Review', and failure recovery.
/// </summary>
internal sealed class BuilderCapability(
    Func<BuilderSettings> loadSettings,
    Func<IReadOnlyList<ManagerProject>> loadProjects,
    IAgentPlanRepository plans,
    Func<BuilderSettings, IBuilderJira> jiraFactory,
    Func<Uri, BuilderSettings, GitHubCore> gitHubFactory,
    IRunnerClient runner,
    string workspaceRoot) : IAgentCapability
{
    private const string Log = "Builder";

    public AgentCapabilityKind Kind => AgentCapabilityKind.Builder;

    public async Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var settings = loadSettings();
        var projects = loadProjects()
            .Where(project => context.ProjectKeys.Contains(project.JiraProjectKey, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var jira = jiraFactory(settings);
            IReadOnlyList<BuilderIssue> candidates;
            try
            {
                candidates = await jira.FindWorkAsync(BuildJql(settings, project.JiraProjectKey), cancellationToken);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, Log, $"Jira polling failed for {project.JiraProjectKey}: {exception.Message}. Retrying next cycle.");
                continue;
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessAsync(context, settings, project, jira, candidate, cancellationToken);
                }
                catch (RunnerBusyException)
                {
                    DFLogger.LogOutput(DFLogLevel.INFO, Log, "The runner is busy; remaining issues wait for the next cycle.");
                    return;
                }
            }
        }
    }

    internal static string BuildJql(BuilderSettings settings, string projectKey)
    {
        var jql = settings.AllowLocalPlan
            ? JiraQuery.ScopeToProject(settings.JiraJql, projectKey)
            : $"project = \"{JiraQuery.EscapeJql(projectKey)}\" AND status = \"{JiraQuery.EscapeJql(settings.JiraStatusName)}\" " +
              $"AND issuetype != Epic AND labels in (\"{JiraQuery.EscapeJql(settings.AnalyzedLabel)}\") " +
              "ORDER BY priority DESC, created ASC";
        return JiraQuery.AddPickupLabelExclusion(jql, settings.PickupLabel);
    }

    private async Task ProcessAsync(
        AgentContext context, BuilderSettings settings, ManagerProject project, IBuilderJira jira,
        BuilderIssue candidate, CancellationToken cancellationToken)
    {
        // Claim before refreshing so eligibility is validated against state no other worker can change under us.
        using var claim = context.ClaimLock.TryAcquire(context.AgentId, candidate.Key);
        if (claim is null)
        {
            DFLogger.LogOutput(DFLogLevel.INFO, Log, $"Another worker is already processing {candidate.Key}.");
            return;
        }

        BuilderIssue issue;
        try
        {
            issue = await jira.GetIssueAsync(candidate.Key, cancellationToken);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Log, $"Could not refresh {candidate.Key} before claiming: {exception.Message}.");
            return;
        }

        if (issue.Labels.Contains(settings.PickupLabel) || issue.Labels.Contains(settings.FailureLabel) ||
            issue.Labels.Contains(settings.IgnoredLabel) ||
            (!settings.AllowLocalPlan && !issue.Labels.Contains(settings.AnalyzedLabel)) ||
            !string.Equals(issue.StatusName, candidate.StatusName, StringComparison.Ordinal))
        {
            return;
        }

        var repositoryUrl = new Uri(project.TargetRepositoryUrl);
        var plan = plans.Get(context.AgentId, issue.Key);
        // Plan gate: a stored plan must be posted and its fingerprint must match the current requirements.
        if (plan is not null && !(plan.PostedToJira && !string.IsNullOrWhiteSpace(plan.Plan) &&
            string.Equals(plan.RequirementsHash,
                ManagerPlan.Fingerprint(issue.Summary, issue.Description, repositoryUrl), StringComparison.Ordinal)))
        {
            await IgnoreAsync(context, jira, issue, "the stored plan is stale or not yet posted. Waiting for refreshed planning.", cancellationToken);
            return;
        }

        if (plan is null && !settings.AllowLocalPlan)
        {
            await IgnoreAsync(context, jira, issue, "no plan exists for it. Waiting for the Planning capability.", cancellationToken);
            return;
        }

        var openBlockers = JiraWorkflowRules.OpenBlockers(issue.Blockers, settings.DoneStatus);
        if (openBlockers.Count > 0)
        {
            await IgnoreAsync(context, jira, issue,
                $"it is blocked by tasks that are not {settings.DoneStatus} yet: {JiraWorkflowRules.DescribeBlockers(openBlockers)}.",
                cancellationToken);
            return;
        }

        await IgnoredTaskNotifier.ClearAsync(
            issue.Key, issue.Labels, jira.RemoveLabelAsync,
            message => DFLogger.LogOutput(DFLogLevel.WARNING, Log, message), cancellationToken);
        DFLogger.LogOutput(DFLogLevel.INFO, Log, $"Claiming {issue.Key}: {issue.Summary}");
        await ImplementAsync(context, settings, repositoryUrl, jira, issue, plan, cancellationToken);
    }

    private static Task IgnoreAsync(
        AgentContext context, IBuilderJira jira, BuilderIssue issue, string reason, CancellationToken cancellationToken)
    {
        DFLogger.LogOutput(DFLogLevel.WARNING, Log, $"Skipping {issue.Key}: {reason}");
        return IgnoredTaskNotifier.NotifyAsync(
            context.Agent.Name, issue.Key, reason, jira.GetCommentsAsync, jira.AddCommentAsync, jira.AddLabelAsync,
            message => DFLogger.LogOutput(DFLogLevel.WARNING, Log, message), cancellationToken);
    }

    private async Task ImplementAsync(
        AgentContext context, BuilderSettings settings, Uri repositoryUrl, IBuilderJira jira, BuilderIssue issue,
        AgentPlan? storedPlan, CancellationToken cancellationToken)
    {
        string? workspacePath = null;
        string? branchName = null;
        string? pushedBranch = null;
        string? pullRequestUrl = null;
        var inProgressAttempted = false;
        var inProgressCompleted = false;
        var resultCommentPosted = false;
        var pushStarted = false;
        var delivered = false;
        try
        {
            await jira.AddLabelAsync(issue.Key, settings.PickupLabel, cancellationToken);
            await TryAsync(issue.Key, "assign",
                () => string.IsNullOrEmpty(settings.AssigneeAccountId)
                    ? Task.CompletedTask
                    : jira.AssignAsync(issue.Key, settings.AssigneeAccountId, cancellationToken),
                jira, cancellationToken);
            inProgressAttempted = true;
            inProgressCompleted = await TryAsync(issue.Key, $"move to \"{settings.InProgressStatus}\"",
                () => jira.TransitionToAsync(issue.Key, settings.InProgressStatus, cancellationToken),
                jira, cancellationToken);

            var gitHub = gitHubFactory(repositoryUrl, settings);
            var baseBranch = issue.ParentKey is null ? null : JiraWorkflowRules.ParentBranchName(issue.ParentKey);
            workspacePath = Path.Combine(workspaceRoot, "aiagent-" + Guid.NewGuid().ToString("N"));
            branchName = await gitHub.CloneAndBranchAsync(
                workspacePath, issue.Key, issue.Summary, baseBranch, cancellationToken);

            string plan;
            if (storedPlan is not null)
            {
                plan = storedPlan.Plan;
            }
            else
            {
                DFLogger.LogOutput(DFLogLevel.INFO, Log, $"No stored plan for {issue.Key}; generating a plan locally.");
                plan = await RunRunnerAsync(context, settings, RunnerModes.Plan, workspacePath,
                    PromptBuilder.BuildPlanPrompt(issue), cancellationToken);
            }

            var before = await jira.GetIssueAsync(issue.Key, cancellationToken);
            if (ManagerPlan.Fingerprint(before.Summary, before.Description, repositoryUrl) !=
                ManagerPlan.Fingerprint(issue.Summary, issue.Description, repositoryUrl))
            {
                throw new InvalidOperationException(RequirementsChangedMessage);
            }

            var result = await RunRunnerAsync(context, settings, RunnerModes.Implement, workspacePath,
                PromptBuilder.BuildImplementationPrompt(issue, plan, baseBranch), cancellationToken);
            pushStarted = true;
            var push = await gitHub.CommitAndPushAsync(
                workspacePath, branchName, issue.Key, issue.Summary, cancellationToken);
            var message = push.Message;
            if (push.Pushed)
            {
                pushedBranch = branchName;
                var pullRequest = await gitHub.OpenPullRequestAsync(
                    context.AgentId, issue.Key, issue.Summary, branchName, baseBranch,
                    $"Implements {issue.Key}: {issue.Summary}\n\nGenerated by aiAgent.", cancellationToken);
                pullRequestUrl = pullRequest.PullRequestUrl;
                message += $" Pull request: {pullRequestUrl}";
            }
            else if (inProgressCompleted && issue.StatusName.Length > 0 &&
                !string.Equals(issue.StatusName, settings.InProgressStatus, StringComparison.OrdinalIgnoreCase))
            {
                await jira.TransitionToAsync(issue.Key, issue.StatusName, cancellationToken);
                message += " No pull request was created and the issue status was left unchanged.";
            }

            await jira.AddCommentAsync(
                issue.Key, PromptBuilder.BuildResultComment(result, message, storedPlan), cancellationToken);
            resultCommentPosted = true;
            if (pullRequestUrl is not null)
            {
                await jira.TransitionToAsync(issue.Key, settings.InReviewStatus, cancellationToken);
            }

            delivered = true;
            DFLogger.LogOutput(DFLogLevel.INFO, Log, $"Completed {issue.Key}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await RecoverFromShutdownAsync(settings, jira, issue, pushStarted, pushedBranch, branchName,
                    workspacePath, pullRequestUrl, inProgressAttempted, resultCommentPosted))
            {
                DeleteWorkspace(workspacePath);
                workspacePath = null;
            }

            throw;
        }
        catch (RunnerBusyException)
        {
            await ReleaseAsync(settings, jira, issue, inProgressCompleted, cancellationToken);
            DeleteWorkspace(workspacePath);
            workspacePath = null;
            throw;
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Log, $"Processing {issue.Key} failed: {exception.Message}");
            await ReportFailureAsync(settings, jira, issue, exception, pushedBranch, branchName, workspacePath,
                pullRequestUrl, inProgressAttempted, resultCommentPosted, cancellationToken);
        }
        finally
        {
            if (delivered)
            {
                DeleteWorkspace(workspacePath);
            }
            else if (workspacePath is not null)
            {
                DFLogger.LogOutput(DFLogLevel.WARNING, Log,
                    $"Retained temporary workspace for {issue.Key} at {workspacePath}." +
                    (pushedBranch is not null ? $" Pushed branch: {pushedBranch}." : branchName is not null ? $" Local branch: {branchName}." : ""));
            }
        }
    }

    internal static readonly TimeSpan ShutdownRecoveryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bounded recovery when shutdown interrupts work after pickup. Uses its own token so the pickup is not left
    /// orphaned: unpushed work is released for a clean retry; possibly pushed work keeps the pickup label (duplicate-work
    /// protection) and gets a failure report with recovery instructions. Returns true when the issue was released.
    /// </summary>
    private static async Task<bool> RecoverFromShutdownAsync(
        BuilderSettings settings, IBuilderJira jira, BuilderIssue issue, bool pushStarted, string? pushedBranch,
        string? branchName, string? workspacePath, string? pullRequestUrl, bool inProgressAttempted,
        bool resultCommentPosted)
    {
        using var recovery = new CancellationTokenSource(ShutdownRecoveryTimeout);
        try
        {
            if (!pushStarted && pushedBranch is null)
            {
                DFLogger.LogOutput(DFLogLevel.WARNING, Log, $"Shutdown interrupted {issue.Key} before pushing; releasing the issue.");
                await ReleaseAsync(settings, jira, issue, inProgressAttempted, recovery.Token);
                return true;
            }

            var interruptedBranch = pushedBranch ?? branchName;
            DFLogger.LogOutput(DFLogLevel.WARNING, Log,
                $"Shutdown interrupted {issue.Key} after a push may have completed (branch: {interruptedBranch ?? "none"}); reporting for manual recovery.");
            await ReportFailureAsync(settings, jira, issue,
                new OperationCanceledException(
                    "The agent shut down while delivering the implementation; the branch may already have been pushed."),
                interruptedBranch, branchName, workspacePath, pullRequestUrl, inProgressAttempted,
                resultCommentPosted, recovery.Token);
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Log,
                $"Shutdown recovery for {issue.Key} failed: {exception.Message}. The `{settings.PickupLabel}` label may remain; " +
                $"pushed branch: {pushedBranch ?? "unknown"}; local branch: {branchName ?? "none"}; workspace: {workspacePath ?? "none"}.");
        }

        return false;
    }

    internal const string RequirementsChangedMessage =
        "The Jira requirements changed after pickup. Refresh the plan before retrying implementation.";

    private async Task<string> RunRunnerAsync(
        AgentContext context, BuilderSettings settings, string mode, string workspace, string prompt,
        CancellationToken cancellationToken)
    {
        var agent = context.Agent;
        // Resolve provider and model through the factory so provider defaults and model normalization apply.
        var model = new LanguageModelFactory().Create(agent);
        var isCopilot = model.Provider == "copilot";
        var response = await runner.RunAsync(
            new RunnerRequest(
                model.Provider,
                model.Model,
                mode,
                workspace,
                PromptComposer.Compose(agent, prompt),
                isCopilot ? settings.AgentGitHubToken : null),
            cancellationToken);
        return response.ExitCode == 0
            ? response.Output
            : throw new InvalidOperationException(
                $"The runner failed in '{mode}' mode with exit code {response.ExitCode}: {PromptBuilder.LimitForComment(response.Output)}");
    }

    /// <summary>Runs a non-critical Jira step; failures are logged and commented, never thrown.</summary>
    private static async Task<bool> TryAsync(
        string key, string action, Func<Task> step, IBuilderJira jira, CancellationToken cancellationToken)
    {
        try
        {
            await step();
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Log, $"Could not {action} {key}: {exception.Message}");
            try
            {
                await jira.AddCommentAsync(
                    key,
                    $"aiAgent could not {action}: {PromptBuilder.LimitForComment(exception.Message)}\n\nWork continues.",
                    cancellationToken);
            }
            catch (Exception notificationException) when (!cancellationToken.IsCancellationRequested)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, Log, $"Could not post the warning for {key}: {notificationException.Message}");
            }

            return false;
        }
    }

    /// <summary>Undoes the pickup (label and status) when the runner had no free slot.</summary>
    private static async Task ReleaseAsync(
        BuilderSettings settings, IBuilderJira jira, BuilderIssue issue, bool inProgressCompleted,
        CancellationToken cancellationToken)
    {
        var labelRemoved = false;
        try
        {
            await jira.RemoveLabelAsync(issue.Key, settings.PickupLabel, cancellationToken);
            labelRemoved = true;
            if (inProgressCompleted && issue.StatusName.Length > 0 &&
                !string.Equals(issue.StatusName, settings.InProgressStatus, StringComparison.OrdinalIgnoreCase))
            {
                await jira.TransitionToAsync(issue.Key, issue.StatusName, cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Log, $"Could not release {issue.Key}: {exception.Message}");
            if (!labelRemoved)
            {
                return;
            }

            // The pickup label is gone but the status was not restored, so the issue would no longer match the
            // pickup query and would be silently stranded. Mark it failed and explain how to recover.
            try
            {
                await jira.AddLabelAsync(issue.Key, settings.FailureLabel, cancellationToken);
                await jira.AddCommentAsync(
                    issue.Key,
                    $"aiAgent could not finish releasing this issue: {PromptBuilder.LimitForComment(exception.Message)}\n\n" +
                    $"The `{settings.PickupLabel}` label was removed, but the issue may still be in \"{settings.InProgressStatus}\". " +
                    $"Move it back to \"{issue.StatusName}\" and remove the `{settings.FailureLabel}` label to allow it to be picked up again.",
                    cancellationToken);
            }
            catch (Exception notificationException) when (!cancellationToken.IsCancellationRequested)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, Log,
                    $"Could not report the incomplete release of {issue.Key} to Jira: {notificationException.Message}. " +
                    $"Move it back to {issue.StatusName} manually.");
            }
        }
    }

    private static void DeleteWorkspace(string? workspacePath)
    {
        if (workspacePath is null)
        {
            return;
        }

        DeleteDirectory(workspacePath, "temporary workspace");
        // GitRunner keeps the trusted Git metadata outside the workspace; remove it together with the
        // workspace so per-job object databases do not accumulate. Retained workspaces keep theirs.
        DeleteDirectory(GitMetadataDirectoryFor(workspacePath), "Git metadata for workspace " + workspacePath);
    }

    private static void DeleteDirectory(string path, string description)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, Log, $"Could not remove {description} at {path}: {exception.Message}");
        }
    }

    /// <summary>
    /// Mirrors the default metadata location used by <see cref="GitRunner"/> (the GitRunner the builder is
    /// wired with in Program.cs uses the default metadata root). Keep in sync with GitRunner.MetadataDirectoryFor.
    /// </summary>
    internal static string GitMetadataDirectoryFor(string checkoutPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutPath));
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..32]
            .ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aiagent-git-metadata")), hash);
    }

    private static async Task ReportFailureAsync(
        BuilderSettings settings, IBuilderJira jira, BuilderIssue issue, Exception exception,
        string? pushedBranch, string? branchName, string? workspacePath, string? pullRequestUrl,
        bool inProgressAttempted, bool resultCommentPosted, CancellationToken cancellationToken)
    {
        var statusRestored = false;
        var requirementsChanged = exception is InvalidOperationException &&
            exception.Message.StartsWith("The Jira requirements changed after pickup.", StringComparison.Ordinal);
        var needsRestore = inProgressAttempted && issue.StatusName.Length > 0 &&
            !string.Equals(issue.StatusName, settings.InProgressStatus, StringComparison.OrdinalIgnoreCase);
        if (pushedBranch is null && needsRestore)
        {
            try
            {
                await jira.TransitionToAsync(issue.Key, issue.StatusName, cancellationToken);
                statusRestored = true;
            }
            catch (Exception restoreException) when (!cancellationToken.IsCancellationRequested)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, Log,
                    $"Could not restore the status of {issue.Key} to {issue.StatusName}: {restoreException.Message}");
            }
        }

        try
        {
            await jira.AddLabelAsync(issue.Key, settings.FailureLabel, cancellationToken);
            string recovery;
            if (pushedBranch is not null)
            {
                recovery = pullRequestUrl is null
                    ? $"The changes were already pushed to `{pushedBranch}`. Create or locate a pull request for this branch, post its URL to Jira, and move the issue to \"{settings.InReviewStatus}\". Do not rerun the implementation."
                    : resultCommentPosted
                        ? $"The pull request is {pullRequestUrl} and its result is posted to Jira. Retry moving the issue to \"{settings.InReviewStatus}\" without rerunning the implementation."
                        : $"The pull request is {pullRequestUrl}. Post its URL and the implementation result to Jira, then move the issue to \"{settings.InReviewStatus}\". Do not rerun the implementation.";
            }
            else
            {
                var where = branchName is not null
                    ? $"The local branch `{branchName}` is retained in the agent workspace."
                    : workspacePath is not null ? "The agent workspace is retained for recovery." : "No workspace was created.";
                var status = statusRestored
                    ? $"The issue status was restored to \"{issue.StatusName}\"."
                    : inProgressAttempted && issue.StatusName.Length == 0
                        ? "The issue's original status could not be determined; verify its status before retrying."
                        : needsRestore
                            ? $"The issue may still be in \"{settings.InProgressStatus}\"; move it back to \"{issue.StatusName}\" before retrying."
                            : "The issue status was not changed by the agent.";
                recovery = $"{where} {status}";
            }

            var labelAdvice = pushedBranch is not null
                ? $"The `{settings.PickupLabel}` label remains in place to prevent automatic duplicate work. Do not remove it to rerun implementation."
                : requirementsChanged
                    ? $"The `{settings.PickupLabel}` and `{settings.FailureLabel}` labels remain in place. Remove both to allow the plan to be refreshed and the issue retried."
                    : $"The `{settings.PickupLabel}` label remains in place. Remove it (and `{settings.FailureLabel}`) after resolving the failure to allow the issue to be picked up again.";
            await jira.AddCommentAsync(
                issue.Key,
                $"aiAgent could not complete this task: {PromptBuilder.LimitForComment(exception.Message)}\n\n{labelAdvice}\n\n{recovery}",
                cancellationToken);
        }
        catch (Exception notificationException) when (!cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, Log,
                $"Could not report the failure for {issue.Key} to Jira: {notificationException.Message}. " +
                $"Pushed branch: {pushedBranch ?? "none"}; local branch: {branchName ?? "none"}.");
        }
    }
}
