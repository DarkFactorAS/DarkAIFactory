using AiAgent;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;

var options = WorkspaceOptions.FromEnvironment(Environment.GetEnvironmentVariable);

if (args.Contains("--git-fetch"))
{
    return await GitFetcher.RunAsync(options);
}

if (args.Contains("--runner"))
{
    await RunnerServer.RunAsync(options);
    return 0;
}

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = AgentHost.CreateBuilder(args);
using var host = builder.Build();

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
using var sigterm = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    });

try
{
    return await AgentHost.RunAsync(host.Services, cancellation.Token);
}
catch (Exception ex) when (ex is InvalidOperationException)
{
    Console.Error.WriteLine($"Startup failed: {ex.Message}");
    return 1;
}
