using DFCommonLib.Logger;

namespace AiAgent.Core;

/// <summary>
/// Runs one <see cref="AgentLoop"/> per enabled agent. Every cycle the agent rows are reloaded: loops start
/// for new or re-enabled agents and stop for disabled or deleted ones. A run in progress finishes first.
/// With an <see cref="IAgentOwnershipLock"/>, several aiagent containers share the agents: a container starts a
/// loop only for an agent whose lock it holds, and for at most <c>maxAgents</c> agents at a time.
/// </summary>
public sealed class AgentHost(
    IAgentRepository repository,
    IEnumerable<IAgentCapability> capabilities,
    IAgentClaimLock claimLock,
    TimeSpan interval,
    IAgentStatusWriter? statusWriter = null,
    TimeSpan? heartbeatInterval = null,
    IAgentOwnershipLock? ownership = null,
    int maxAgents = int.MaxValue)
{
    private readonly IReadOnlyList<IAgentCapability> _capabilities = capabilities.ToList();
    private readonly Dictionary<long, (AgentLoop Loop, Task Task)> _loops = [];
    private readonly Dictionary<long, IDisposable> _leases = [];
    private readonly HashSet<long> _disabledWritten = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Reconcile(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    DFLogger.LogOutput(DFLogLevel.ERROR, "AgentHost", $"Scanning agents failed: {exception.Message}");
                }

                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(_loops.Values.Select(entry => entry.Task));
        foreach (var agentId in _leases.Keys.ToList())
        {
            ReleaseOwnership(agentId);
        }
    }

    /// <summary>Starts loops for enabled agents and stops loops of disabled or deleted agents.</summary>
    internal IReadOnlyCollection<long> Reconcile(CancellationToken cancellationToken)
    {
        foreach (var finished in _loops.Where(pair => pair.Value.Task.IsCompleted).Select(pair => pair.Key).ToList())
        {
            _loops.Remove(finished);
            ReleaseOwnership(finished);
        }

        var agents = repository.GetAllAgents();
        var runnable = agents.Where(agent => agent.Enabled && !agent.Deleted).Select(agent => agent.Id).ToHashSet();
        foreach (var (id, entry) in _loops)
        {
            if (!runnable.Contains(id) && !entry.Loop.StopRequested)
            {
                entry.Loop.Stop();
            }
        }

        // Inactive agents without a loop (for example disabled before the host started) are marked Disabled once,
        // so a status persisted by a previous process does not linger. Agents whose loop is still finishing a run
        // keep their status; the loop writes Disabled itself when it ends.
        foreach (var agent in agents.Where(agent => !runnable.Contains(agent.Id) && !_loops.ContainsKey(agent.Id)))
        {
            if (!_disabledWritten.Contains(agent.Id) && TryWriteDisabled(agent.Id))
            {
                _disabledWritten.Add(agent.Id);
            }
        }

        foreach (var agent in agents.Where(agent => runnable.Contains(agent.Id)))
        {
            // A loop that is stopping but still finishing a run is replaced once it has ended.
            if (_loops.ContainsKey(agent.Id))
            {
                continue;
            }

            // Another container owns the agent, or this one already runs its share.
            if (_loops.Count >= maxAgents || !TryTakeOwnership(agent.Id))
            {
                continue;
            }

            _disabledWritten.Remove(agent.Id);
            var loop = new AgentLoop(agent.Id, repository, _capabilities, claimLock, interval, statusWriter, heartbeatInterval);
            _loops[agent.Id] = (loop, Task.Run(() => loop.RunAsync(cancellationToken), CancellationToken.None));
        }

        return _loops.Where(pair => !pair.Value.Loop.StopRequested).Select(pair => pair.Key).ToList();
    }

    /// <summary>True when this process may run the agent: no ownership lock is configured, or the lock was won.</summary>
    private bool TryTakeOwnership(long agentId)
    {
        if (ownership is null)
        {
            return true;
        }

        try
        {
            var lease = ownership.TryAcquire(agentId);
            if (lease is null)
            {
                return false;
            }

            _leases[agentId] = lease;
            DFLogger.LogOutput(DFLogLevel.INFO, "AgentHost", $"This instance now runs agent {agentId}.");
            return true;
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, "AgentHost", $"Taking ownership of agent {agentId} failed: {exception.Message}");
            return false;
        }
    }

    private void ReleaseOwnership(long agentId)
    {
        if (_leases.Remove(agentId, out var lease))
        {
            lease.Dispose();
            DFLogger.LogOutput(DFLogLevel.INFO, "AgentHost", $"This instance released agent {agentId}.");
        }
    }

    private bool TryWriteDisabled(long agentId)
    {
        if (statusWriter is null)
        {
            return true;
        }

        try
        {
            statusWriter.Set(agentId, AgentState.Disabled, null, null);
            return true;
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, "AgentHost", $"Writing Disabled status for agent {agentId} failed: {exception.Message}");
            return false;
        }
    }
}
