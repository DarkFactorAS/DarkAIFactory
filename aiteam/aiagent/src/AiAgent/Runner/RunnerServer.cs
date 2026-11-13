using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AiAgent.Runner;

/// <summary>Sandbox runner started with <c>--runner-server</c>. Stage 1 only exposes <c>/health</c>.</summary>
internal static class RunnerServer
{
    public static WebApplication Build(string url = "http://0.0.0.0:8080")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        return app;
    }

    public static Task RunAsync() => Build().RunAsync();
}
