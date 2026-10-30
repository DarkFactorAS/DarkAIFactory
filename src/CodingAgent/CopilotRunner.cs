using System.Diagnostics;

namespace CodingAgent;

internal sealed class CopilotRunner
{
    private readonly AgentConfiguration _configuration;

    public CopilotRunner(AgentConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("--version");
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Copilot CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = stderr.Trim();
            throw new InvalidOperationException(
                $"Copilot CLI version check failed with exit code {process.ExitCode}. " +
                (detail.Length == 0 ? string.Empty : Limit(detail, 2_000)));
        }
    }

    public async Task<string> RunAsync(
        string prompt,
        bool allowChanges,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo();
        startInfo.ArgumentList.Add("--prompt");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--trust-folder");
        startInfo.ArgumentList.Add("--secret-env-vars=GH_TOKEN,GITHUB_TOKEN");
        startInfo.ArgumentList.Add("--allow-all-tools");
        if (allowChanges)
        {
            startInfo.ArgumentList.Add("--deny-tool=shell(git push)");
            startInfo.ArgumentList.Add("--deny-tool=shell(gh pr)");
        }
        else
        {
            startInfo.ArgumentList.Add("--deny-tool=shell");
            startInfo.ArgumentList.Add("--deny-tool=write");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Copilot CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
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

        var output = (await stdoutTask).Trim();
        var error = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            var detail = error.Length == 0 ? string.Empty : $" Details: {Limit(error, 2_000)}";
            throw new InvalidOperationException($"Copilot CLI failed with exit code {process.ExitCode}.{detail}");
        }

        return output.Length > 0
            ? output
            : throw new InvalidOperationException("Copilot CLI returned no response.");
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            WorkingDirectory = _configuration.WorkspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        foreach (var name in startInfo.Environment.Keys.Where(name => name.StartsWith("JIRA_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        return startInfo;
    }

    private static string Limit(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength] + "...";
}
