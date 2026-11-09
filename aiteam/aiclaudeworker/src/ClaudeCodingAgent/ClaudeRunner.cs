using System.Net.Http.Json;

namespace ClaudeCodingAgent;

/// <summary>
/// Runs the Claude Code CLI as <c>claude -p</c> (headless print mode) inside a cloned workspace.
/// Authentication comes from the CLI's own login unless a credential is configured.
/// </summary>
internal sealed class ClaudeRunner : IDisposable
{
    internal const string OAuthTokenPrefix = "sk-ant-oat";

    // Tools that only read the workspace; used for local planning.
    private static readonly string[] ReadOnlyTools = ["Read", "Glob", "Grep"];

    // Tools that can change the workspace or run commands; denied during planning.
    private static readonly string[] MutatingTools = ["Bash", "Edit", "MultiEdit", "Write", "NotebookEdit"];

    // The worker pushes and opens the pull request itself; the agent must never do so.
    private static readonly string[] AlwaysDeniedTools = ["Bash(git push:*)", "Bash(gh:*)"];

    private readonly AgentConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public ClaudeRunner(AgentConfiguration configuration, HttpMessageHandler? handler = null, Uri? runnerUrl = null)
    {
        _configuration = configuration;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = runnerUrl ?? new Uri(
            Environment.GetEnvironmentVariable("CLAUDE_RUNNER_URL") ?? "http://claude-cli:8080");
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/check",
            new ClaudeRunnerServer.RunnerRequest(AnthropicCredential: _configuration.AnthropicApiKey),
            cancellationToken);
        await EnsureRunnerSuccessAsync(response, cancellationToken);
    }

    public async Task<string> RunAsync(
        string workspacePath,
        string prompt,
        bool allowChanges,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "/run",
            new ClaudeRunnerServer.RunnerRequest(
                workspacePath, prompt, _configuration.AgentModel, allowChanges, _configuration.AnthropicApiKey),
            cancellationToken);
        await EnsureRunnerSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<ClaudeRunnerServer.RunnerResponse>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The isolated Claude runner returned no response.");
        return result.Output.Length > 0
            ? result.Output
            : throw new InvalidOperationException("Claude Code CLI returned no response.");
    }

    private static async Task EnsureRunnerSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"The isolated Claude runner returned status {(int)response.StatusCode}. " +
            (body.Length <= 2_000 ? body : body[..2_000] + "..."));
    }

    internal static IReadOnlyList<string> BuildArguments(string model, bool allowChanges)
    {
        var arguments = new List<string> { "-p", "--output-format", "text" };
        if (model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        if (allowChanges)
        {
            // Headless implementation inside the Docker sandbox: no interactive approvals.
            // Deny rules still take precedence over this mode.
            arguments.Add("--permission-mode");
            arguments.Add("bypassPermissions");
        }
        else
        {
            arguments.Add("--permission-mode");
            arguments.Add("default");
            foreach (var tool in ReadOnlyTools)
            {
                arguments.Add("--allowedTools");
                arguments.Add(tool);
            }

            foreach (var tool in MutatingTools)
            {
                arguments.Add("--disallowedTools");
                arguments.Add(tool);
            }
        }

        foreach (var tool in AlwaysDeniedTools)
        {
            arguments.Add("--disallowedTools");
            arguments.Add(tool);
        }

        return arguments;
    }

    internal static void ApplyEnvironment(IDictionary<string, string?> environment, string anthropicCredential)
    {
        environment["NO_COLOR"] = "1";
        environment["TERM"] = "dumb";
        environment["DISABLE_AUTOUPDATER"] = "1";
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        if (anthropicCredential.Length == 0)
        {
            // No configured credential: the CLI uses the login stored in CLAUDE_CONFIG_DIR.
        }
        else if (anthropicCredential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal))
        {
            environment.Remove("ANTHROPIC_API_KEY");
            environment.Remove("ANTHROPIC_AUTH_TOKEN");
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = anthropicCredential;
        }
        else
        {
            environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
            environment.Remove("ANTHROPIC_AUTH_TOKEN");
            environment["ANTHROPIC_API_KEY"] = anthropicCredential;
        }

        // The agent never needs GitHub or Jira credentials: the worker clones, pushes and
        // creates pull requests itself. NuGet credentials stay so the agent can restore and run tests.
        foreach (var name in environment.Keys.Where(name =>
                     name.StartsWith("JIRA_", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("COPILOT_GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GITHUB_REPOSITORY_TOKEN", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            environment.Remove(name);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
