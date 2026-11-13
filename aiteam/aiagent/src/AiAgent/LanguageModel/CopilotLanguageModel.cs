using System.Diagnostics;

namespace AiAgent.LanguageModel;

/// <summary>Runs the GitHub Copilot CLI as <c>copilot -p</c>.</summary>
public sealed class CopilotLanguageModel(
    string? model = null,
    Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>>? processRunner = null) : ILanguageModel
{
    public const string DefaultModel = "gpt-5-mini";

    public string Provider => "copilot";
    public string Model { get; } = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public Task<string> RunAsync(LanguageModelRequest request, CancellationToken cancellationToken) =>
        RunAsync(request, gitHubToken: null, cancellationToken);

    /// <summary>
    /// Runs the prompt, authenticating the Copilot CLI with <paramref name="gitHubToken"/> (e.g. the configured
    /// <c>GitHub.AgentGitHubToken</c>). The token is set only on this child process's environment, never on the
    /// process-wide environment, so concurrent invocations can use different credentials.
    /// </summary>
    public async Task<string> RunAsync(LanguageModelRequest request, string? gitHubToken, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = "copilot", WorkingDirectory = request.WorkspacePath };
        foreach (var argument in BuildArguments(request.Prompt, Model, request.AllowChanges))
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo.Environment, gitHubToken);
        var result = await (processRunner ?? CliProcess.RunAsync)(startInfo, cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            throw new InvalidOperationException(
                $"Copilot CLI failed with exit code {result.ExitCode}." +
                (detail.Length == 0 ? string.Empty : $" Details: {CliProcess.Limit(detail, 2_000)}"));
        }

        var output = result.StandardOutput.Trim();
        return output.Length > 0 ? output : throw new InvalidOperationException("Copilot CLI returned no response.");
    }

    internal static IReadOnlyList<string> BuildArguments(string prompt, string model, bool allowChanges)
    {
        var arguments = new List<string> { "--prompt", prompt };
        if (model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        // Permission flags are intentionally hard-coded (copied from CopilotRunner.cs).
        arguments.Add("--silent");
        arguments.Add("--trust-folder");
        arguments.Add("--secret-env-vars=GH_TOKEN,GITHUB_TOKEN");
        arguments.Add("--allow-all-tools");
        arguments.Add("--deny-tool=shell(git push)");
        arguments.Add("--deny-tool=shell(gh)");
        if (!allowChanges)
        {
            arguments.Add("--deny-tool=shell");
            arguments.Add("--deny-tool=write");
        }

        return arguments;
    }

    internal static void ApplyEnvironment(IDictionary<string, string?> environment, string? gitHubToken = null)
    {
        environment["NO_COLOR"] = "1";
        environment["TERM"] = "dumb";
        // The Copilot CLI authenticates through GH_TOKEN. Prefer the explicitly configured token for this
        // child process; otherwise fall back to whatever GH_TOKEN the host provides.
        environment.Remove("GITHUB_TOKEN");
        environment.Remove("COPILOT_GITHUB_TOKEN");
        CliProcess.RemoveSecrets(environment);
        if (!string.IsNullOrWhiteSpace(gitHubToken))
        {
            environment["GH_TOKEN"] = gitHubToken.Trim();
        }
    }
}
