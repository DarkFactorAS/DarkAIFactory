using System.Diagnostics;
using AiAgent.Core;

namespace AiAgent.LanguageModel;

/// <summary>One prompt to run through a language-model CLI.</summary>
public sealed record LanguageModelRequest(string WorkspacePath, string Prompt, bool AllowChanges);

/// <summary>A language-model backend that runs a prompt inside a workspace.</summary>
public interface ILanguageModel
{
    string Provider { get; }
    string Model { get; }
    Task<string> RunAsync(LanguageModelRequest request, CancellationToken cancellationToken);
}

/// <summary>Resolves the language model for an agent each cycle, so provider/model changes need no restart.</summary>
public interface ILanguageModelFactory
{
    ILanguageModel Create(AgentDefinition agent);
}

public sealed class LanguageModelFactory(
    Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>>? processRunner = null) : ILanguageModelFactory
{
    public ILanguageModel Create(AgentDefinition agent)
    {
        var model = string.IsNullOrWhiteSpace(agent.LlmModel) ? null : agent.LlmModel.Trim();
        return agent.LlmProvider.Trim().ToLowerInvariant() switch
        {
            "claude" or "anthropic" => new ClaudeLanguageModel(model, processRunner),
            "copilot" or "github" => new CopilotLanguageModel(model, processRunner),
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{agent.LlmProvider}'.")
        };
    }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CliProcess
{
    internal static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
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

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    internal static string Limit(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    internal static void RemoveSecrets(IDictionary<string, string?> environment, params string[] extra)
    {
        foreach (var name in environment.Keys.Where(name =>
                     name.StartsWith("JIRA_", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GITHUB_REPOSITORY_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                     extra.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray())
        {
            environment.Remove(name);
        }
    }
}
