using System.Diagnostics;

namespace AiAgent.Capabilities.Review;

/// <summary>Runs the configured build/test commands with bash in the workspace.</summary>
public sealed class ProcessCommandRunner : IProjectCommandRunner
{
    public async Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("bash") { WorkingDirectory = workspacePath };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        // Credentials must not be visible to project code.
        foreach (var name in startInfo.Environment.Keys
                     .Where(n => n.StartsWith("JIRA_", StringComparison.OrdinalIgnoreCase) ||
                                 n.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                                 n.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        var result = await AiAgent.LanguageModel.CliProcessAccess.RunAsync(startInfo, cancellationToken);
        if (result.ExitCode == 0)
        {
            return null;
        }

        var text = (result.StandardError + "\n" + result.StandardOutput).Trim();
        return $"exit code {result.ExitCode}. {(text.Length > 1_500 ? text[^1_500..] : text)}";
    }
}
