namespace AiAgent;

internal static class RunnerServer
{
    public static WorkspaceStatus Inspect(WorkspaceOptions options)
    {
        var ready = File.Exists(Path.Combine(options.Root, WorkspaceOptions.MarkerFileName))
            && Directory.Exists(options.CheckoutPath);
        var count = ready ? Directory.EnumerateFileSystemEntries(options.CheckoutPath).Count() : 0;
        return new WorkspaceStatus(ready, count);
    }

    public static async Task RunAsync(WorkspaceOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:8080");
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/workspace", () => Results.Ok(Inspect(options)));
        await app.RunAsync();
    }
}

internal sealed record WorkspaceStatus(bool Ready, int EntryCount);
