using System.Diagnostics;

namespace ReviewWorker;

/// <summary>Runs the Copilot CLI on a checkout of the PR head branch.</summary>
internal sealed class CopilotRunner(string agentGitHubToken, string agentModel) : IFixRunner
{
    public async Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["GH_TOKEN"] = agentGitHubToken;
        startInfo.Environment.Remove("GITHUB_TOKEN");
        startInfo.Environment.Remove("COPILOT_GITHUB_TOKEN");
        startInfo.Environment.Remove("GITHUB_REPOSITORY_TOKEN");
        foreach (var name in startInfo.Environment.Keys.Where(n => n.StartsWith("JIRA_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(prompt);
        if (agentModel.Length > 0)
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(agentModel);
        }

        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--trust-folder");
        startInfo.ArgumentList.Add("--secret-env-vars=GH_TOKEN,GITHUB_TOKEN");
        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--deny-tool=shell(git push)");
        startInfo.ArgumentList.Add("--deny-tool=shell(git commit)");
        startInfo.ArgumentList.Add("--deny-tool=shell(gh)");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Copilot CLI.");
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

        await stdout;
        var error = (await stderr).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Copilot CLI failed with exit code {process.ExitCode}." +
                (error.Length == 0 ? string.Empty : $" Details: {(error.Length > 2_000 ? error[..2_000] + "..." : error)}"));
        }
    }
}
