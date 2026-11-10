using System.Net.Http.Json;

namespace ReviewWorker;

internal sealed class SandboxRunnerClient(string agentGitHubToken, string agentModel, Uri? runnerUrl = null)
    : IFixRunner, IProjectCommandRunner, IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = runnerUrl ?? new Uri(
            Environment.GetEnvironmentVariable("REVIEW_RUNNER_URL") ?? "http://localhost:8080/")
    };

    public async Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "run",
            new RunRequest(WorkspaceId(workspacePath), prompt, agentModel, agentGitHubToken),
            cancellationToken);
        await EnsureSuccessAsync(response, "Copilot", cancellationToken);
    }

    async Task<string?> IProjectCommandRunner.RunAsync(
        string command, string workspacePath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "command", new CommandRequest(WorkspaceId(workspacePath), command), cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

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

    private sealed record RunRequest(string WorkspaceId, string Prompt, string Model, string GitHubToken);
    private sealed record CommandRequest(string WorkspaceId, string Command);
}
