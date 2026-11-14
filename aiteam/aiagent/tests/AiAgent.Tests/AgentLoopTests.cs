using AiAgent.Core;

namespace AiAgent.Tests;

internal sealed class FakeRepository : IAgentRepository
{
    public AgentDefinition? Agent { get; set; } = new(1, "a", "role", "behavior", "Claude", null, true, false);
    public HashSet<AgentCapabilityKind> Capabilities { get; set; } = [];
    public List<AgentDefinition> Others { get; } = [];
    public Dictionary<long, string[]> Projects { get; } = [];
    public IReadOnlyList<AgentDefinition> GetEnabledAgents() =>
        GetAllAgents().Where(a => a is { Enabled: true, Deleted: false }).ToList();
    public IReadOnlyList<AgentDefinition> GetAllAgents() =>
        (Agent is null ? [] : new List<AgentDefinition> { Agent }).Concat(Others).ToList();
    public AgentDefinition? GetAgent(long agentId) =>
        GetAllAgents().FirstOrDefault(a => a.Id == agentId);
    public IReadOnlySet<AgentCapabilityKind> GetEnabledCapabilities(long agentId) => Capabilities.ToHashSet();
    public IReadOnlyList<string> GetProjectKeys(long agentId) =>
        Projects.TryGetValue(agentId, out var keys) ? keys : ["DFAI"];
}

internal sealed class FakeStatusWriter : IAgentStatusWriter
{
    public List<(long Id, AgentState State, string? Issue, AgentCapabilityKind? Capability)> Sets { get; } = [];
    public int Heartbeats;
    public bool Throw { get; set; }
    public void Set(long agentId, AgentState state, string? issueKey, AgentCapabilityKind? capability)
    {
        if (Throw) throw new InvalidOperationException("db down");
        lock (Sets) Sets.Add((agentId, state, issueKey, capability));
    }
    public void Heartbeat(long agentId)
    {
        if (Throw) throw new InvalidOperationException("db down");
        Interlocked.Increment(ref Heartbeats);
    }
}

internal sealed class FakeCapability(AgentCapabilityKind kind, List<AgentCapabilityKind> log) : IAgentCapability
{
    public AgentCapabilityKind Kind => kind;
    public Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        log.Add(kind);
        return Task.CompletedTask;
    }
}

internal sealed class ScopeNameCapability(List<string?> names) : IAgentCapability
{
    public AgentCapabilityKind Kind => AgentCapabilityKind.Jira;
    public Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        names.Add(AgentLog.Current);
        return Task.CompletedTask;
    }
}

internal sealed class NoLock : IAgentClaimLock
{
    public IDisposable? TryAcquire(long agentId, string issueKey) => null;
}

public sealed class AgentLoopTests
{
    [Fact]
    public async Task Cycle_RunsCapabilitiesInAgentLogScope()
    {
        var names = new List<string?>();
        var repo = new FakeRepository { Capabilities = [AgentCapabilityKind.Jira] };
        var loop = new AgentLoop(1, repo, [new ScopeNameCapability(names)], new NoLock(), TimeSpan.FromMilliseconds(10));

        await loop.RunCycleAsync(CancellationToken.None);

        Assert.Equal(["a"], names);
        Assert.Null(AgentLog.Current);
    }

    private static (AgentLoop Loop, FakeRepository Repo, List<AgentCapabilityKind> Log) Create()
    {
        var log = new List<AgentCapabilityKind>();
        var repo = new FakeRepository();
        // Registered out of order on purpose.
        var capabilities = new[] { AgentCapabilityKind.Review, AgentCapabilityKind.Jira, AgentCapabilityKind.Builder,
            AgentCapabilityKind.Planning }.Select(k => (IAgentCapability)new FakeCapability(k, log));
        return (new AgentLoop(1, repo, capabilities, new NoLock(), TimeSpan.FromMilliseconds(10)), repo, log);
    }

    [Fact]
    public async Task RunsEnabledCapabilitiesInOrder()
    {
        var (loop, repo, log) = Create();
        repo.Capabilities = [AgentCapabilityKind.Review, AgentCapabilityKind.Jira, AgentCapabilityKind.Builder];
        Assert.True(await loop.RunCycleAsync(default));
        Assert.Equal([AgentCapabilityKind.Jira, AgentCapabilityKind.Builder, AgentCapabilityKind.Review], log);
    }

    [Fact]
    public async Task TogglingCapabilityTakesEffectNextCycle()
    {
        var (loop, repo, log) = Create();
        repo.Capabilities = [AgentCapabilityKind.Jira];
        await loop.RunCycleAsync(default);
        repo.Capabilities = [AgentCapabilityKind.Planning];
        await loop.RunCycleAsync(default);
        Assert.Equal([AgentCapabilityKind.Jira, AgentCapabilityKind.Planning], log);
    }

    [Fact]
    public async Task IdlesWhenDisabledOrDeleted()
    {
        var (loop, repo, log) = Create();
        repo.Capabilities = [AgentCapabilityKind.Jira];
        repo.Agent = repo.Agent! with { Enabled = false };
        Assert.False(await loop.RunCycleAsync(default));
        repo.Agent = repo.Agent with { Enabled = true, Deleted = true };
        Assert.False(await loop.RunCycleAsync(default));
        repo.Agent = null;
        Assert.False(await loop.RunCycleAsync(default));
        Assert.Empty(log);
        repo.Agent = new(1, "a", "r", "b", "Claude", null, true, false);
        Assert.True(await loop.RunCycleAsync(default));
        Assert.Single(log);
    }

    [Fact]
    public async Task FailingCapabilityDoesNotStopOthers()
    {
        var log = new List<AgentCapabilityKind>();
        var repo = new FakeRepository { Capabilities = [AgentCapabilityKind.Jira, AgentCapabilityKind.Planning] };
        var loop = new AgentLoop(1, repo,
            [new ThrowingCapability(), new FakeCapability(AgentCapabilityKind.Planning, log)],
            new NoLock(), TimeSpan.FromMilliseconds(10));
        await loop.RunCycleAsync(default);
        Assert.Equal([AgentCapabilityKind.Planning], log);
    }

    [Fact]
    public async Task HostStartsLoopPerEnabledAgentAndStopsOnCancel()
    {
        var log = new List<AgentCapabilityKind>();
        var repo = new FakeRepository { Capabilities = [AgentCapabilityKind.Jira] };
        var host = new AgentHost(repo, [new FakeCapability(AgentCapabilityKind.Jira, log)], new NoLock(),
            TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var run = host.RunAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(log);
    }

    [Fact]
    public void PromptContainsRoleAndBehavior()
    {
        var prompt = PromptComposer.Compose(new(1, "Bot", "Developer", "Be careful", "Claude", null, true, false), "Fix it");
        Assert.Contains("You are Bot.", prompt);
        var text = prompt.Replace("\r", "");
        Assert.Contains("## Agent role and behavior", text);
        Assert.Contains("Role: Developer", text);
        Assert.Contains("Be careful", prompt);
        Assert.Contains("Fix it", prompt);
    }

    private sealed class ClaimingCapability(bool claim) : IAgentCapability
    {
        public AgentCapabilityKind Kind => AgentCapabilityKind.Builder;
        public Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
        {
            if (claim)
            {
                using var held = context.ClaimLock.TryAcquire(1, "DFAI-1");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysLock : IAgentClaimLock
    {
        public IDisposable? TryAcquire(long agentId, string issueKey) => new Handle();
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task CapabilityWithoutClaimIsNeverReportedRunning()
    {
        var writer = new FakeStatusWriter();
        var repo = new FakeRepository { Capabilities = [AgentCapabilityKind.Builder] };
        var loop = new AgentLoop(1, repo, [new ClaimingCapability(false)], new NoLock(),
            TimeSpan.FromMilliseconds(10), writer);
        await loop.RunCycleAsync(default);
        Assert.NotEmpty(writer.Sets);
        Assert.All(writer.Sets, set =>
        {
            Assert.NotEqual(AgentState.Running, set.State);
            Assert.Null(set.Capability);
        });
    }

    [Fact]
    public async Task ClaimedCapabilityIsRunningOnlyWhileClaimHeld()
    {
        var writer = new FakeStatusWriter();
        var repo = new FakeRepository { Capabilities = [AgentCapabilityKind.Builder] };
        var loop = new AgentLoop(1, repo, [new ClaimingCapability(true)], new AlwaysLock(),
            TimeSpan.FromMilliseconds(10), writer);
        await loop.RunCycleAsync(default);
        var running = writer.Sets.Single(set => set.State == AgentState.Running);
        Assert.Equal("DFAI-1", running.Issue);
        Assert.Equal(AgentCapabilityKind.Builder, running.Capability);
        var last = writer.Sets[^1];
        Assert.Equal(AgentState.Idle, last.State);
        Assert.Null(last.Capability);
    }

    private sealed class ThrowingCapability : IAgentCapability
    {
        public AgentCapabilityKind Kind => AgentCapabilityKind.Jira;
        public Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
