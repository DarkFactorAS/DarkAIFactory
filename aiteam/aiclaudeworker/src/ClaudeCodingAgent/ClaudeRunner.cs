using System.Diagnostics;

namespace ClaudeCodingAgent;

/// <summary>
/// Runs the Claude Code CLI (<c>claude</c>) in headless print mode inside a cloned workspace.
/// </summary>
internal sealed class ClaudeRunner
{
    internal const string OAuthTokenPrefix = "sk-ant-oat";

    // Tools that only read the workspace; used for local planning.
    private static readonly string[] ReadOnlyTools = ["Read", "Glob", "Grep"];

    // Tools that can change the workspace or run commands; denied during planning.
    private static readonly string[] MutatingTools = ["Bash", "Edit", "MultiEdit", "Write", "NotebookEdit"];

    // The worker pushes and opens the pull request itself; the agent must never do so.
    private static readonly string[] AlwaysDeniedTools = ["Bash(git push:*)", "Bash(gh:*)"];

    private readonly AgentConfiguration _configuration;

    public ClaudeRunner(AgentConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(Path.GetTempPath());
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Claude Code CLI.");
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            throw new InvalidOperationException(
                $"Claude Code CLI version check failed with exit code {process.ExitCode}. " +
                (detail.Length == 0 ? string.Empty : Limit(detail, 2_000)));
        }
    }

    public async Task<string> RunAsync(
        string workspacePath,
        string prompt,
        bool allowChanges,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(workspacePath);
        foreach (var argument in BuildArguments(_configuration.AgentModel, allowChanges))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Claude Code CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            // The prompt goes through stdin so it is not visible in the process list.
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var output = (await stdoutTask).Trim();
        var error = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            var detail = error.Length == 0
                ? output.Length == 0 ? string.Empty : $" Details: {Limit(output, 2_000)}"
                : $" Details: {Limit(error, 2_000)}";
            throw new InvalidOperationException($"Claude Code CLI failed with exit code {process.ExitCode}.{detail}");
        }

        return output.Length > 0
            ? output
            : throw new InvalidOperationException("Claude Code CLI returned no response.");
    }

    internal static IReadOnlyList<string> BuildArguments(string model, bool allowChanges)
    {
        var arguments = new List<string> { "--print", "--output-format", "text" };
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
        environment.Remove("ANTHROPIC_API_KEY");
        environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        environment.Remove("ANTHROPIC_AUTH_TOKEN");
        if (anthropicCredential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal))
        {
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = anthropicCredential;
        }
        else
        {
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

    private ProcessStartInfo CreateStartInfo(string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        ApplyEnvironment(startInfo.Environment, _configuration.AnthropicApiKey);
        return startInfo;
    }

    private static string Limit(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength] + "...";
}
