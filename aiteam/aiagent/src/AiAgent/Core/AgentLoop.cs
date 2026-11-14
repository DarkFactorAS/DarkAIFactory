using DFCommonLib.Logger;

namespace AiAgent.Core;

/// <summary>
/// Runs one agent. Every cycle reloads the agent row, its capabilities and projects, so edits in the
/// database take effect on the next cycle without a restart. A disabled or deleted agent idles.
/// <see cref="Stop"/> ends the loop between cycles; a capability run in progress is allowed to finish.
/// </summary>
public sealed class AgentLoop(
    long agentId,
    IAgentRepository repository,
    IEnumerable<IAgentCapability> capabilities,
    IAgentClaimLock claimLock,
    TimeSpan interval,
    IAgentStatusWriter? statusWriter = null,
    TimeSpan? heartbeatInterval = null)
{
    private readonly List<IAgentCapability> _capabilities =
        capabilities.OrderBy(capability => capability.Kind).ToList();

    // Only used to end the loop's waiting and scheduling; capability runs get the host token.
    private readonly CancellationTokenSource _stop = new();
    private readonly object _statusLock = new();
    private volatile string? _lastName;
    private string? _currentIssue;
    private AgentCapabilityKind? _currentCapability;
    // The capability being polled; only reported as working once it claims an issue.
    private AgentCapabilityKind? _polling;

    public long AgentId => agentId;

    public bool StopRequested => _stop.IsCancellationRequested;

    /// <summary>Asks the loop to end after the run in progress. Does not interrupt a run.</summary>
    public void Stop() => _stop.Cancel();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        while (!cancellationToken.IsCancellationRequested && !StopRequested)
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
                // Runs outside the cycle's scope, so add the last known name here.
                using var scope = AgentLog.BeginScope(_lastName);
                AgentLog.LogOutput(DFLogLevel.ERROR, "AgentLoop", $"Agent {agentId} cycle failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(interval, linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (StopRequested && !cancellationToken.IsCancellationRequested)
        {
            WriteStatus(AgentState.Disabled, null, null);
        }
    }

    /// <summary>Runs a single cycle. Returns false when the agent was idle (disabled or deleted).</summary>
    public async Task<bool> RunCycleAsync(CancellationToken cancellationToken)
    {
        var agent = repository.GetAgent(agentId);
        if (agent is null || agent.Deleted || !agent.Enabled)
        {
            WriteStatus(AgentState.Disabled, null, null);
            return false;
        }

        _lastName = agent.Name;
        using var nameScope = AgentLog.BeginScope(agent.Name);
        var enabled = repository.GetEnabledCapabilities(agentId);
        var context = new AgentContext(agent, repository.GetProjectKeys(agentId), new StatusClaimLock(this, claimLock));
        WriteStatus(AgentState.Idle, null, null);
        foreach (var capability in _capabilities.Where(capability => enabled.Contains(capability.Kind)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StopRequested)
            {
                // Stop was requested while an earlier capability ran; let that run finish but start no more.
                break;
            }

            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = RunHeartbeatAsync(heartbeat.Token);
            var failed = false;
            lock (_statusLock)
            {
                _polling = capability.Kind;
            }

            try
            {
                await capability.RunCycleAsync(context, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One failing capability must not starve the others. Capability-local
                // cancellations (e.g. HttpClient timeouts) are treated as failures; only
                // host cancellation propagates.
                failed = true;
                AgentLog.LogOutput(
                    DFLogLevel.ERROR, "AgentLoop",
                    $"Agent {agentId} capability {capability.Kind} failed: {exception.Message}");
            }
            finally
            {
                heartbeat.Cancel();
                await heartbeatTask;
                lock (_statusLock)
                {
                    _polling = null;
                }
            }

            WriteStatus(failed ? AgentState.Error : AgentState.Idle, null, failed ? capability.Kind : null);
        }

        return true;
    }

    private async Task RunHeartbeatAsync(CancellationToken token)
    {
        if (statusWriter is null)
        {
            return;
        }

        var period = heartbeatInterval ?? interval;
        try
        {
            while (true)
            {
                await Task.Delay(period, token);
                try
                {
                    statusWriter.Heartbeat(agentId);
                }
                catch (Exception exception)
                {
                    LogStatusFailure(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void WriteStatus(AgentState state, string? issueKey, AgentCapabilityKind? capability)
    {
        if (statusWriter is null)
        {
            return;
        }

        try
        {
            lock (_statusLock)
            {
                _currentIssue = issueKey;
                _currentCapability = capability;
                statusWriter.Set(agentId, state, issueKey, capability);
            }
        }
        catch (Exception exception)
        {
            LogStatusFailure(exception);
        }
    }

    private void IssueClaimed(string? issueKey)
    {
        AgentCapabilityKind? capability;
        lock (_statusLock)
        {
            capability = _polling;
        }

        // Only update while a capability is running; the claim handle may be disposed after the run ends.
        if (capability is not null)
        {
            // Highlight the capability only while it holds a claim; a released claim returns to Idle.
            if (issueKey is null)
            {
                WriteStatus(AgentState.Idle, null, null);
            }
            else
            {
                WriteStatus(AgentState.Running, issueKey, capability);
            }
        }
    }

    private void LogStatusFailure(Exception exception)
    {
        // May run outside the cycle's scope (shutdown, disabled or deleted cycles), so fall back to the last known name.
        using var scope = AgentLog.BeginScope(AgentLog.Current ?? _lastName);
        AgentLog.LogOutput(DFLogLevel.ERROR, "AgentLoop", $"Agent {agentId} status write failed: {exception.Message}");
    }

    /// <summary>Records the claimed issue as current_issue_key while the claim is held.</summary>
    private sealed class StatusClaimLock(AgentLoop loop, IAgentClaimLock inner) : IAgentClaimLock
    {
        public IDisposable? TryAcquire(long agentId, string issueKey)
        {
            var claim = inner.TryAcquire(agentId, issueKey);
            if (claim is null)
            {
                return null;
            }

            loop.IssueClaimed(issueKey);
            return new Release(loop, claim);
        }

        private sealed class Release(AgentLoop loop, IDisposable claim) : IDisposable
        {
            public void Dispose()
            {
                claim.Dispose();
                loop.IssueClaimed(null);
            }
        }
    }
}
