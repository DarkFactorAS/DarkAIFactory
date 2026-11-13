using System.Diagnostics;
using System.Text;

namespace AiAgent.GitHub;

/// <summary>Runs git with a scrubbed environment, no hooks, no credential helper and https only.</summary>
public sealed class GitRunner(string token) : IGitRunner
{
    public async Task<GitResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, bool withCredentials,
        CancellationToken cancellationToken, bool allowExitCodeOne = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        foreach (var name in new[] { "PATH", "SYSTEMROOT" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetTempPath(), $"aiagent-{Guid.NewGuid():N}.gitconfig");
        startInfo.Environment["GIT_AUTHOR_NAME"] = "aiAgent";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "aiagent@users.noreply.github.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "aiAgent";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "aiagent@users.noreply.github.com";
        if (withCredentials)
        {
            startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
            startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            startInfo.Environment["GIT_CONFIG_VALUE_0"] =
                $"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"))}";
        }

        foreach (var fixedArgument in new[]
                 { "-c", "core.hooksPath=/dev/null", "-c", "credential.helper=", "-c", "protocol.allow=never",
                   "-c", "protocol.https.allow=always" })
        {
            startInfo.ArgumentList.Add(fixedArgument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git.");
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

        var error = (await stderr).Trim();
        if (process.ExitCode != 0 && !(allowExitCodeOne && process.ExitCode == 1))
        {
            var details = error.Length > 2000 ? error[..2000] : error;
            throw new InvalidOperationException(
                $"Git {arguments[0]} failed with exit code {process.ExitCode}." +
                (details.Length == 0 ? string.Empty : $" Details: {details}"));
        }

        return new GitResult(process.ExitCode, await stdout, error);
    }
}
