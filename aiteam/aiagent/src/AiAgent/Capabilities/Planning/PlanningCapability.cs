using AiAgent.Core;
using AiAgent.Providers;
using AiAgent.Repositories;
using AiAgent.Runner;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Planning;

/// <summary>Produces plans (runner in 'plan' mode) and the git/GitHub side effects planning needs.</summary>
internal interface IPlanningRunner
{
    Task<string> GeneratePlanAsync(
        AgentContext context, IntegrationSettings settings, PlanningConfiguration configuration,
        JiraIssue issue, CancellationToken cancellationToken);

    Task<string> CreateParentPullRequestAsync(
        AgentContext context, IntegrationSettings settings, PlanningConfiguration configuration,
        string headBranch, string issueKey, string summary, CancellationToken cancellationToken);
}

/// <summary>
/// Planning capability: triages and plans Jira issues of the agent's projects, stores the plan with the
/// requirements fingerprint in agent_plans, posts it to Jira and labels the issue for the Builder.
/// Must not share projects with a running manager-agent (see README).
/// </summary>
internal sealed class PlanningCapability(
    Func<IntegrationSettings> loadSettings,
    Func<WorkflowLabels> loadLabels,
    Func<IReadOnlyList<ManagerProject>> loadProjects,
    IAgentPlanRepository plans,
    Func<IntegrationSettings, IPlanningJira> createJira,
    IPlanningRunner runner) : IAgentCapability
{
    public const string AgentName = "aiagent";

    public AgentCapabilityKind Kind => AgentCapabilityKind.Planning;

    public async Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var keys = context.ProjectKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projects = loadProjects().Where(project => keys.Contains(project.JiraProjectKey.Trim())).ToList();
        if (projects.Count == 0)
        {
            return;
        }

        var settings = loadSettings();
        var labels = loadLabels();
        var jira = createJira(settings);
        try
        {
            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await PlanProjectAsync(context, settings, labels, project, jira, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    DFLogger.LogOutput(
                        DFLogLevel.ERROR, "Planning",
                        $"Planning project {project.JiraProjectKey} failed: {exception.Message}. Retrying next cycle.");
                }
            }
        }
        finally
        {
            (jira as IDisposable)?.Dispose();
        }
    }

    private async Task PlanProjectAsync(
        AgentContext context, IntegrationSettings settings, WorkflowLabels labels, ManagerProject project,
        IPlanningJira jira, CancellationToken cancellationToken)
    {
        var configuration = PlanningConfiguration.Load(settings, project, labels);
        var planner = new AgentPlanner(
            configuration,
            new AgentPlanStore(plans, context.AgentId),
            jira,
            (issue, token) => runner.GeneratePlanAsync(context, settings, configuration, issue, token),
            (config, head, key, summary, token) =>
                runner.CreateParentPullRequestAsync(context, settings, config, head, key, summary, token));
        await planner.ReportExcludedIssuesAsync(project.JiraProjectKey, cancellationToken);
        foreach (var issue in await jira.SearchAsync(configuration.JiraJql, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var claim = context.ClaimLock.TryAcquire(context.AgentId, issue.Key);
            if (claim is null)
            {
                continue;
            }

            try
            {
                await planner.PlanAsync(project.Id, issue, cancellationToken);
            }
            catch (RunnerBusyException exception)
            {
                DFLogger.LogOutput(DFLogLevel.INFO, "Planning", $"Planning {issue.Key} deferred: {exception.Message}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(
                    DFLogLevel.ERROR, "Planning",
                    $"Planning {issue.Key} failed: {exception.Message}. Pausing automatic retries with the failure label.");
                try
                {
                    await jira.AddLabelAsync(issue.Key, configuration.FailureLabel, cancellationToken);
                    await jira.AddCommentAsync(
                        issue.Key,
                        $"aiAgent could not complete planning: {PlanningPrompts.LimitForComment(exception.Message)}\n\n" +
                        $"No code was implemented. Remove the `{configuration.FailureLabel}` label after resolving the failure to retry planning. " +
                        "Any saved plan will be reused.",
                        cancellationToken);
                }
                catch (Exception notification) when (!cancellationToken.IsCancellationRequested)
                {
                    DFLogger.LogOutput(
                        DFLogLevel.ERROR, "Planning",
                        $"Could not report planning failure for {issue.Key}: {notification.Message}");
                }
            }
        }
    }
}
