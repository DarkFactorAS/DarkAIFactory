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
        Timeout = timeout
    };

    public async Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            "run", new RunRequest(WorkspaceId(workspacePath), prompt, agentModel), "Claude Code", cancellationToken);
        await EnsureSuccessAsync(response, "Claude Code", cancellationToken);
    }

    async Task<string?> IProjectCommandRunner.RunAsync(
        string command, string workspacePath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            "command", new CommandRequest(WorkspaceId(workspacePath), command), $"'{command}'", cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<HttpResponseMessage> SendAsync<T>(
        string path, T body, string processName, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{processName} did not finish within {_httpClient.Timeout.TotalMinutes:0} minute(s) in the sandbox.", exception);
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

    private sealed record RunRequest(string WorkspaceId, string Prompt, string Model);
    private sealed record CommandRequest(string WorkspaceId, string Command);
}
