using System.Net.Http.Json;

namespace ReviewWorker;

/// <summary>
/// Calls the review-runner sandbox. A Claude Code run can take many minutes, so requests use
/// <paramref name="timeout"/> instead of the 100-second HttpClient default. When it expires a
/// <see cref="TimeoutException"/> is thrown, so a slow run is not mistaken for a worker shutdown.
/// </summary>
internal sealed class SandboxRunnerClient(
    string agentModel, TimeSpan timeout, Uri? runnerUrl = null, HttpMessageHandler? handler = null)
    : IFixRunner, IProjectCommandRunner, IDisposable
{
    private readonly HttpClient _httpClient = new(handler ?? new HttpClientHandler())
    {
        BaseAddress = runnerUrl ?? new Uri(
            Environment.GetEnvironmentVariable("REVIEW_RUNNER_URL") ?? "http://localhost:8080/"),
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly TimeSpan _timeout = timeout;

    public async Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken)
    {
        var executionId = Guid.NewGuid().ToString("N");
        using var response = await SendAsync(
            "run", new RunRequest(WorkspaceId(workspacePath), prompt, agentModel, executionId),
            executionId, "Claude Code", cancellationToken);
        await EnsureSuccessAsync(response, "Claude Code", cancellationToken);
    }

    async Task<string?> IProjectCommandRunner.RunAsync(
        string command, string workspacePath, CancellationToken cancellationToken)
    {
        var executionId = Guid.NewGuid().ToString("N");
        using var response = await SendAsync(
            "command", new CommandRequest(WorkspaceId(workspacePath), command, executionId),
            executionId, $"'{command}'", cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendAsync<T>(
        string path, T body, string executionId, string processName, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            return await _httpClient.PostAsJsonAsync(path, body, requestCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ConfirmTerminationAsync(executionId);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await ConfirmTerminationAsync(executionId);
            throw new TimeoutException(
                $"{processName} did not finish within {_timeout.TotalMinutes:0} minute(s) in the sandbox.", exception);
        }
        catch (HttpRequestException)
        {
            await ConfirmTerminationAsync(executionId);
            throw;
        }
    }

    private async Task ConfirmTerminationAsync(string executionId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await _httpClient.PostAsJsonAsync(
                "cancel", new CancelRequest(executionId), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"The sandbox did not confirm termination (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (Exception exception) when (exception is not SandboxWorkspaceAbandonedException)
        {
            throw new SandboxWorkspaceAbandonedException(
                "The sandbox could not confirm process termination; the workspace must not be reused or deleted.",
                exception);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string processName, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{processName} sandbox failed: {detail}");
        }
    }

    private static string WorkspaceId(string workspacePath)
    {
        var checkout = new DirectoryInfo(Path.GetFullPath(workspacePath));
        var workspaceId = checkout.Parent?.Name;
        return checkout.Name == "checkout" && Guid.TryParseExact(workspaceId, "N", out var id)
            ? id.ToString("N")
            : throw new InvalidOperationException("The workspace is not in the shared sandbox volume.");
    }

    private sealed record RunRequest(string WorkspaceId, string Prompt, string Model, string ExecutionId);
    private sealed record CommandRequest(string WorkspaceId, string Command, string ExecutionId);
    private sealed record CancelRequest(string ExecutionId);
}

internal sealed class SandboxWorkspaceAbandonedException(string message, Exception? innerException = null)
    : OperationCanceledException(message, innerException);
