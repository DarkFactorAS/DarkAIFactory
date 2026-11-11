using AiAgent.Repositories;

namespace AiAgent.Tests;

public sealed class AgentConfigTests
{
    private sealed class StubRepository : IAgentConfigRepository
    {
        public Dictionary<string, AgentDefinition> Agents { get; } = [];
        public HashSet<string> DisabledModels { get; } = [];
        public HashSet<string> DisabledCapabilities { get; } = [];
        public int Reads { get; private set; }

        // Mirrors the SQL repository: disabled models drop the agent, disabled capabilities are removed.
        public Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
        {
            Reads++;
            if (!Agents.TryGetValue(name, out var a) || DisabledModels.Contains(a.LanguageModelKey))
                return Task.FromResult<AgentDefinition?>(null);
            return Task.FromResult<AgentDefinition?>(
                a with { Capabilities = a.Capabilities.Where(c => !DisabledCapabilities.Contains(c)).ToList() });
        }

        public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) =>
            (await Task.WhenAll(Agents.Keys.Select(k => GetByNameAsync(k, ct))))
                .Where(a => a is not null).Select(a => a!).ToList();
    }

    private static readonly AgentDefinition Defaults =
        new("codingagent", "default-role", "", "default-model", []);

    private static AgentDefinition Db(string instructions = "Be careful.", string model = "m1", params string[] caps) =>
        new("codingagent", "coder", instructions, model, caps);

    [Fact]
    public async Task LoadsNameRoleInstructionsModelAndCapabilities()
    {
        var repo = new StubRepository();
        repo.Agents["codingagent"] = Db("Be careful.", "m1", "git", "tests");
        var host = new AgentHost(repo, new AgentConfigLoader(repo), Defaults);

        var s = await host.RefreshAsync();

        Assert.Equal("codingagent", s.Name);
        Assert.Equal("coder", s.Role);
        Assert.Equal("Be careful.", s.Instructions);
        Assert.Equal("m1", s.LanguageModel);
        Assert.Equal(["git", "tests"], s.Capabilities);
        Assert.Contains("Be careful.", host.SystemPrompt);
    }

    [Fact]
    public async Task ConfigChangesApplyWithoutRebuild()
    {
        var repo = new StubRepository();
        repo.Agents["codingagent"] = Db("v1", "m1", "git");
        var host = new AgentHost(repo, new AgentConfigLoader(repo), Defaults);
        await host.RefreshAsync();
        var before = host.SystemPrompt;

        repo.Agents["codingagent"] = Db("v2", "m2", "git", "deploy");
        await host.RefreshAsync();

        Assert.NotEqual(before, host.SystemPrompt);
        Assert.Contains("v2", host.SystemPrompt);
        Assert.Equal("m2", host.Settings.LanguageModel);
        Assert.True(host.Settings.IsEnabled("deploy"));
    }

    [Fact]
    public async Task DisabledCapabilitiesAndModelsAreExcluded()
    {
        var repo = new StubRepository();
        repo.Agents["codingagent"] = Db("x", "m1", "git", "deploy");
        repo.DisabledCapabilities.Add("deploy");
        var host = new AgentHost(repo, new AgentConfigLoader(repo), Defaults);

        Assert.False((await host.RefreshAsync()).IsEnabled("deploy"));
        Assert.True(host.Settings.IsEnabled("git"));

        repo.DisabledModels.Add("m1");
        await host.RefreshAsync();
        Assert.Equal("default-model", host.Settings.LanguageModel);
    }

    [Fact]
    public async Task MissingRowFallsBackToDefaults()
    {
        var repo = new StubRepository();
        var host = new AgentHost(repo, new AgentConfigLoader(repo), Defaults);

        var s = await host.RefreshAsync();

        Assert.Equal("default-role", s.Role);
        Assert.Equal(string.Empty, host.SystemPrompt);
    }

    [Fact]
    public async Task TtlCacheServesStaleUntilExpiry()
    {
        var repo = new StubRepository();
        repo.Agents["codingagent"] = Db("v1");
        var now = DateTimeOffset.UtcNow;
        var loader = new AgentConfigLoader(repo, new AgentConfigOptions { CacheTtl = TimeSpan.FromSeconds(10) }, () => now);

        await loader.LoadAsync("codingagent", Defaults);
        repo.Agents["codingagent"] = Db("v2");
        Assert.Equal("v1", (await loader.LoadAsync("codingagent", Defaults)).Instructions);
        now += TimeSpan.FromSeconds(11);
        Assert.Equal("v2", (await loader.LoadAsync("codingagent", Defaults)).Instructions);
        Assert.Equal(2, repo.Reads);
    }
}
