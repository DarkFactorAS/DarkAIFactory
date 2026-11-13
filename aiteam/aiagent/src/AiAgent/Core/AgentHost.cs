using DFCommonLib.Logger;

namespace AiAgent.Core;

/// <summary>Runs one <see cref="AgentLoop"/> per enabled agent; agents enabled later are picked up on rescan.</summary>
public sealed class AgentHost(
    IAgentRepository repository,
    IEnumerable<IAgentCapability> capabilities,
    IAgentClaimLock claimLock,
    TimeSpan interval)
{
    private readonly IReadOnlyList<IAgentCapability> _capabilities = capabilities.ToList();
    private readonly Dictionary<long, Task> _loops = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    StartMissingLoops(cancellationToken);
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

        await Task.WhenAll(_loops.Values);
    }

    internal IReadOnlyCollection<long> StartMissingLoops(CancellationToken cancellationToken)
    {
        foreach (var finished in _loops.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
        {
            _loops.Remove(finished);
        }

        foreach (var agent in repository.GetEnabledAgents())
        {
            if (_loops.ContainsKey(agent.Id))
            {
                continue;
            }

            var loop = new AgentLoop(agent.Id, repository, _capabilities, claimLock, interval);
            _loops[agent.Id] = Task.Run(() => loop.RunAsync(cancellationToken), CancellationToken.None);
        }

        return _loops.Keys.ToList();
    }
}
