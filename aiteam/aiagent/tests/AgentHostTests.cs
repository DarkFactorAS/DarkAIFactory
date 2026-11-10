using AiAgent.Capabilities;
using AiAgent.LanguageModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiAgent.Tests;

public class AgentHostTests
{
    private sealed class FakeCapability(string name) : ICapability
    {
        public string Name { get; } = name;
        public bool Ran { get; private set; }
        public Task RunAsync(CancellationToken cancellationToken) { Ran = true; return Task.CompletedTask; }
    }

    private sealed class FakeModel(string name) : ILanguageModel
    {
        public string Name { get; } = name;
        public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(prompt);
    }

    [Fact]
    public async Task NoCapabilitiesEnabled_ExitsCleanlyWithZero()
    {
        using var host = AgentHost.CreateBuilder([]).Build();
        Assert.Equal(0, await AgentHost.RunAsync(host.Services, CancellationToken.None));
    }

    [Fact]
    public async Task EnabledCapability_IsRun()
    {
        var capability = new FakeCapability("poller");
        var builder = AgentHost.CreateBuilder([]);
        builder.Services.Configure<AgentSettings>(s => s.Capabilities.Add("Poller"));
        builder.Services.AddSingleton<ICapability>(capability);
        using var host = builder.Build();
        Assert.Equal(0, await AgentHost.RunAsync(host.Services, CancellationToken.None));
        Assert.True(capability.Ran);
    }

    [Fact]
    public async Task UnknownCapability_Throws()
    {
        var builder = AgentHost.CreateBuilder([]);
        builder.Services.Configure<AgentSettings>(s => s.Capabilities.Add("missing"));
        using var host = builder.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AgentHost.RunAsync(host.Services, CancellationToken.None));
    }

    [Fact]
    public void CapabilityRegistry_RejectsDuplicates()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CapabilityRegistry([new FakeCapability("a"), new FakeCapability("A")]));
    }

    [Fact]
    public void LanguageModelRegistry_FindsCaseInsensitiveAndThrowsOnUnknown()
    {
        var registry = new LanguageModelRegistry([new FakeModel("Claude")]);
        Assert.NotNull(registry.Find("claude"));
        Assert.Throws<InvalidOperationException>(() => registry.Get("other"));
    }
}
