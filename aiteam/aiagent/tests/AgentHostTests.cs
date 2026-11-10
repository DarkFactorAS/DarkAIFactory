using AiAgent.Capabilities;
using AiAgent.LanguageModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
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

    private sealed class BlockingCapability : ICapability
    {
        public string Name => "blocking";
        public bool CancellationObserved { get; private set; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                CancellationObserved = cancellationToken.IsCancellationRequested;
            }
        }
    }

    private sealed class FaultingCapability(Exception exception) : ICapability
    {
        public string Name => "faulting";
        public Task RunAsync(CancellationToken cancellationToken) => Task.FromException(exception);
    }

    private static void AddJsonAppName(HostApplicationBuilder builder)
    {
        var jsonIndex = builder.Configuration.Sources
            .Select((source, index) => (source, index))
            .First(item => item.source is JsonConfigurationSource json && json.Path == "Config/appsettings.json")
            .index;
        builder.Configuration.Sources.Insert(jsonIndex, new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?> { ["AppName"] = "json" }
        });
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
    public void RuntimeConfiguration_OverridesJsonConfiguration()
    {
        var previousAppName = Environment.GetEnvironmentVariable("AppName");
        Environment.SetEnvironmentVariable("AppName", "environment");

        try
        {
            var builder = AgentHost.CreateBuilder(["--AppName=command-line"]);
            AddJsonAppName(builder);

            Assert.Equal("command-line", builder.Configuration["AppName"]);

            var environmentBuilder = AgentHost.CreateBuilder([]);
            AddJsonAppName(environmentBuilder);
            Assert.Equal("environment", environmentBuilder.Configuration["AppName"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AppName", previousAppName);
        }
    }

    [Fact]
    public async Task CapabilityFailure_CancelsOtherCapabilitiesAndPropagatesOriginalException()
    {
        var failure = new InvalidOperationException("capability failed");
        var blocking = new BlockingCapability();
        var builder = AgentHost.CreateBuilder([]);
        builder.Services.Configure<AgentSettings>(s =>
        {
            s.Capabilities.Add("faulting");
            s.Capabilities.Add("blocking");
        });
        builder.Services.AddSingleton<ICapability>(new FaultingCapability(failure));
        builder.Services.AddSingleton<ICapability>(blocking);
        using var host = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentHost.RunAsync(host.Services, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(failure, exception);
        Assert.True(blocking.CancellationObserved);
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
