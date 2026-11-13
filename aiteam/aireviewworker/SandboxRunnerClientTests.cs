using System.Net;

namespace ReviewWorker.Tests;

public sealed class SandboxRunnerClientTests
{
    private static readonly string Workspace =
        Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "checkout");

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class UnconfirmedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task RunTimeoutThrowsTimeoutExceptionNotCancellation()
    {
        using var client = new SandboxRunnerClient(
            "", TimeSpan.FromMilliseconds(50), new Uri("http://runner/"), new SlowHandler());

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => client.RunAsync(Workspace, "prompt", CancellationToken.None));

        Assert.Contains("Claude Code", exception.Message);
    }

    [Fact]
    public async Task CallerCancellationStaysCancellation()
    {
        using var client = new SandboxRunnerClient(
            "", TimeSpan.FromMinutes(5), new Uri("http://runner/"), new SlowHandler());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RunAsync(Workspace, "prompt", cancellation.Token));
    }

    [Fact]
    public async Task UnconfirmedTerminationAbandonsTheWorkspace()
    {
        using var client = new SandboxRunnerClient(
            "", TimeSpan.FromMilliseconds(50), new Uri("http://runner/"), new UnconfirmedHandler());

        await Assert.ThrowsAsync<SandboxWorkspaceAbandonedException>(
            () => client.RunAsync(Workspace, "prompt", CancellationToken.None));
    }
}
