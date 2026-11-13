using AiAgent.Core;
using AiAgent.Runner;

namespace AiAgent.Tests;

public sealed class RunnerServerTests
{
    [Fact]
    public async Task HealthEndpointReturnsOk()
    {
        await using var app = RunnerServer.Build("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Urls.First();
        using var client = new HttpClient();
        var response = await client.GetAsync($"{address}/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void AgentSettingsDefaultPollInterval() => Assert.Equal(30, new AgentSettings().PollIntervalSeconds);
}
