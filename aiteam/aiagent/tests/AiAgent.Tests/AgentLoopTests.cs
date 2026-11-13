using AiAgent.Core;

namespace AiAgent.Tests;

internal sealed class FakeRepository : IAgentRepository
{
    public AgentDefinition? Agent { get; set; } = new(1, "a", "role", "behavior", "Claude", null, true, false);
    public HashSet<AgentCapabilityKind> Capabilities { get; set; } = [];
    public IReadOnlyList<AgentDefinition> GetEnabledAgents() =>
        Agent is { Enabled: true, Deleted: false } ? [Agent] : [];
    public AgentDefinition? GetAgent(long agentId) => Agent;
    public IReadOnlySet<AgentCapabilityKind> GetEnabledCapabilities(long agentId) => Capabilities.ToHashSet();
    public IReadOnlyList<string> GetProjectKeys(long agentId) => ["DFAI"];
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

internal sealed class NoLock : IAgentClaimLock
{
    public IDisposable? TryAcquire(long agentId, string issueKey) => null;
}

public sealed class AgentLoopTests
{
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
        Assert.Contains("## Role\nDeveloper", prompt.Replace("\r", ""));
        Assert.Contains("Be careful", prompt);
        Assert.Contains("Fix it", prompt);
    }

    private sealed class ThrowingCapability : IAgentCapability
    {
        public AgentCapabilityKind Kind => AgentCapabilityKind.Jira;
        public Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
