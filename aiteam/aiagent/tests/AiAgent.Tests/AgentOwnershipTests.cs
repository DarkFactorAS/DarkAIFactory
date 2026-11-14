using AiAgent.Core;
using AiAgent.Repositories;

namespace AiAgent.Tests;

/// <summary>Several aiagent containers (hosts) sharing the agents through one ownership lock (the database).</summary>
public sealed class AgentOwnershipTests
{
    private sealed class SharedOwnership : IAgentOwnershipLock
    {
        private readonly HashSet<long> _held = [];

        public IReadOnlyCollection<long> Held { get { lock (_held) return _held.ToList(); } }

        public IDisposable? TryAcquire(long agentId)
        {
            lock (_held)
            {
                return _held.Add(agentId) ? new Lease(this, agentId) : null;
            }
        }

        private sealed class Lease(SharedOwnership owner, long agentId) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._held) owner._held.Remove(agentId);
            }
        }
    }

    private static AgentDefinition Def(long id, bool enabled = true) =>
        new(id, $"agent{id}", "role", "behavior", "Claude", null, enabled, false);

    private static FakeRepository ThreeAgents()
    {
        var repo = new FakeRepository();
        repo.Others.Add(Def(2));
        repo.Others.Add(Def(3));
        return repo;
    }

    private static AgentHost Host(FakeRepository repo, SharedOwnership ownership) =>
        new(repo, [], new NoLock(), TimeSpan.FromMilliseconds(10), ownership: ownership, maxAgents: 1);

    [Fact]
    public void EachInstanceRunsADifferentAgent()
    {
        var repo = ThreeAgents();
        var ownership = new SharedOwnership();
        using var cts = new CancellationTokenSource();

        var first = Host(repo, ownership).Reconcile(cts.Token);
        var second = Host(repo, ownership).Reconcile(cts.Token);
        var third = Host(repo, ownership).Reconcile(cts.Token);

        Assert.Equal([1L], first);
        Assert.Equal([2L], second);
        Assert.Equal([3L], third);
        cts.Cancel();
    }

    [Fact]
    public void InstanceWithoutAFreeAgentIdles()
    {
        var repo = new FakeRepository();
        var ownership = new SharedOwnership();
        using var cts = new CancellationTokenSource();

        Assert.Equal([1L], Host(repo, ownership).Reconcile(cts.Token));
        Assert.Empty(Host(repo, ownership).Reconcile(cts.Token));
        cts.Cancel();
    }

    [Fact]
    public async Task DisabledAgentIsReleasedAndAnotherInstanceTakesTheNextAgent()
    {
        var repo = ThreeAgents();
        var ownership = new SharedOwnership();
        using var cts = new CancellationTokenSource();
        var first = Host(repo, ownership);
        var second = Host(repo, ownership);
        Assert.Equal([1L], first.Reconcile(cts.Token));
        Assert.Equal([2L], second.Reconcile(cts.Token));

        repo.Agent = Def(1, enabled: false);
        first.Reconcile(cts.Token);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (ownership.Held.Contains(1L))
        {
            Assert.True(DateTime.UtcNow < until, "agent 1 was not released");
            await Task.Delay(10);
            first.Reconcile(cts.Token);
        }

        Assert.Equal([3L], first.Reconcile(cts.Token));
        cts.Cancel();
    }

    [Fact]
    public void AgentsPerInstanceLimitsTheLoops()
    {
        var repo = ThreeAgents();
        var ownership = new SharedOwnership();
        using var cts = new CancellationTokenSource();
        var host = new AgentHost(repo, [], new NoLock(), TimeSpan.FromMilliseconds(10), ownership: ownership, maxAgents: 2);

        Assert.Equal([1L, 2L], host.Reconcile(cts.Token).Order());
        Assert.Equal([1L, 2L], ownership.Held.Order());
        cts.Cancel();
    }

    [Fact]
    public void LockNameIsPerAgent() =>
        Assert.Equal("aiagent-agent-42", AgentOwnershipLock.LockName(42));
}
