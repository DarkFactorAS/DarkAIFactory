using DFCommonLib.Logger;

namespace AiAgent.Core;

/// <summary>
/// Runs one agent. Every cycle reloads the agent row and its capabilities, so toggles in the database
/// take effect on the next cycle without a restart. A disabled or deleted agent idles.
/// </summary>
public sealed class AgentLoop(
    long agentId,
    IAgentRepository repository,
    IEnumerable<IAgentCapability> capabilities,
    IAgentClaimLock claimLock,
    TimeSpan interval)
{
    private readonly List<IAgentCapability> _capabilities =
        capabilities.OrderBy(capability => capability.Kind).ToList();

    public long AgentId => agentId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, "AgentLoop", $"Agent {agentId} cycle failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Runs a single cycle. Returns false when the agent was idle (disabled or deleted).</summary>
    public async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
    {
        var agent = repository.GetAgent(agentId);
        if (agent is null || agent.Deleted || !agent.Enabled)
        {
            return false;
        }

        var enabled = repository.GetEnabledCapabilities(agentId);
        var context = new AgentContext(agent, repository.GetProjectKeys(agentId), claimLock);
        foreach (var capability in _capabilities.Where(capability => enabled.Contains(capability.Kind)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await capability.RunCycleAsync(context, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One failing capability must not starve the others. Capability-local
                // cancellations (e.g. HttpClient timeouts) are treated as failures; only
                // host cancellation propagates.
                DFLogger.LogOutput(
                    DFLogLevel.ERROR, "AgentLoop",
                    $"Agent {agentId} capability {capability.Kind} failed: {exception.Message}");
            }
        }

        return true;
    }
}
