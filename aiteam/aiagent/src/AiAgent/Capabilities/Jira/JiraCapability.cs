using System.Collections.Concurrent;
using AiAgent.Core;
using AiAgent.Repositories;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Jira;

/// <summary>
/// Jira capability: intake of agent_tasks into Jira issues, tracking recovery, In Review → Done when all
/// pull requests are merged and unblocking of issues whose blockers are Done. Runs only for agents whose
/// Jira capability is enabled (enforced by <see cref="AgentLoop"/>).
/// </summary>
public sealed class JiraCapability(
    Func<JiraCapabilityConfig> loadConfig,
    Func<JiraCapabilityConfig, IJiraClient> createClient,
    IAgentTaskProcessorRepository tasks,
    IAgentPullRequestRepository pullRequests,
    IPullRequestMergeChecker mergeChecker,
    Func<string, Uri?> resolveRepository) : IAgentCapability
{
    private readonly ConcurrentDictionary<long, Dictionary<long, int>> _attempts = new();

    public AgentCapabilityKind Kind => AgentCapabilityKind.Jira;

    public async Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var config = loadConfig();
        var jira = createClient(config);
        try
        {
            if (config.IntakeEnabled)
            {
                await RunStepAsync("intake", () => new AgentTaskIntake(
                        tasks, jira, config.SelectedStatus, JiraCapabilityConfig.MaxIntakeAttempts,
                        _attempts.GetOrAdd(context.AgentId, _ => []))
                    .ProcessNewTasksAsync(context.ProjectKeys, cancellationToken), cancellationToken);
            }

            var recovery = new PullRequestTrackingRecovery(
                context.AgentId, jira, pullRequests,
                key => resolveRepository(key) ??
                    (Uri.TryCreate(config.DefaultRepositoryUrl, UriKind.Absolute, out var fallback) ? fallback : null));
            var closer = new PullRequestCloser(
                config, jira, pullRequests,
                (record, token) => mergeChecker.IsMergedAsync(config.GitHubToken, record, token));
            var unblocker = new BlockedIssueUnblocker(config, jira);
            foreach (var projectKey in context.ProjectKeys)
            {
                await RunStepAsync($"recovery for {projectKey}", () => recovery.RecoverAsync(projectKey, cancellationToken), cancellationToken);
                await RunStepAsync($"closing for {projectKey}", () => closer.CloseMergedAsync(context.AgentId, projectKey, cancellationToken), cancellationToken);
                await RunStepAsync($"unblocking for {projectKey}", () => unblocker.UnblockAsync(projectKey, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            (jira as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Logs a warning for every enabled agent whose Jira capability does intake: jira-manager-agent also
    /// processes New agent_tasks, so both must not run against the same database at once.
    /// </summary>
    public static void LogIntakeWarning(IAgentRepository agents, Func<JiraCapabilityConfig> loadConfig)
    {
        try
        {
            var names = agents.GetEnabledAgents()
                .Where(agent => agents.GetEnabledCapabilities(agent.Id).Contains(AgentCapabilityKind.Jira))
                .Select(agent => agent.Name).ToList();
            if (names.Count == 0 || !loadConfig().IntakeEnabled)
            {
                return;
            }

            DFLogger.LogOutput(
                DFLogLevel.WARNING, "Startup",
                $"Jira intake is on for agent(s) {string.Join(", ", names)}. Make sure jira-manager-agent is stopped " +
                "or does not handle the same projects; both turn New agent_tasks into Jira issues.");
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, "Startup", $"Could not check the Jira intake setting: {exception.Message}");
        }
    }

    private static async Task RunStepAsync(string name, Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraCapability", $"Jira {name} failed: {exception.Message}");
        }
    }
}
