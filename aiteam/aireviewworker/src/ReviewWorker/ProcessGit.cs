using System.Diagnostics;
using System.Text;

namespace ReviewWorker;

/// <summary>Git and shell helpers backed by real processes.</summary>
internal sealed class ProcessGit(string repositoryToken) : IReviewGit, IProjectCommandRunner
{
    public async Task CloneAsync(Uri repositoryUrl, string branch, string workspacePath, CancellationToken cancellationToken)
    {
        if (!FixDecision.IsPushableBranch(branch))
        {
            throw new InvalidOperationException($"Refusing to check out branch '{branch}'.");
        }

        Directory.CreateDirectory(workspacePath);
        await GitAsync(workspacePath, ["clone", "--branch", branch, "--single-branch", "--", repositoryUrl.AbsoluteUri, "."],
            cancellationToken, credentials: true);
    }

    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var output = await GitAsync(workspacePath, ["status", "--porcelain", "-z", "--untracked-files=all"], cancellationToken);
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            paths.Add(entry[3..]);
            if (entry[0] is 'R' or 'C')
            {
                // Rename/copy entries are followed by the original path.
                i++;
                if (i < entries.Length)
                {
                    paths.Add(entries[i]);
                }
            }
        }

        return paths;
    }

    public async Task DiscardChangesAsync(string workspacePath, CancellationToken cancellationToken)
    {
        await GitAsync(workspacePath, ["reset", "--hard", "HEAD"], cancellationToken);
        await GitAsync(workspacePath, ["clean", "-fd"], cancellationToken);
    }

    public async Task CommitAsync(string workspacePath, string message, CancellationToken cancellationToken)
    {
        await GitAsync(workspacePath, ["add", "--all"], cancellationToken);
        await GitAsync(workspacePath, ["commit", "--no-verify", "-m", message], cancellationToken);
    }

    public async Task PushAsync(string workspacePath, string branch, CancellationToken cancellationToken)
    {
        if (!FixDecision.IsPushableBranch(branch))
        {
            throw new InvalidOperationException($"Refusing to push to branch '{branch}'.");
        }

        // Plain (non-forced, no '+') refspec: a non-fast-forward push is rejected by GitHub.
        await GitAsync(workspacePath, ["push", "origin", $"HEAD:refs/heads/{branch}"], cancellationToken, credentials: true);
    }

    public async Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GITHUB_REPOSITORY_TOKEN", "COPILOT_GITHUB_TOKEN" })
        {
            startInfo.Environment.Remove(name);
        }

        var (exitCode, output, error) = await ExecuteAsync(startInfo, cancellationToken);
        if (exitCode == 0)
        {
            return null;
        }

        var detail = (error.Length > 0 ? error : output).Trim();
        return $"exit code {exitCode}. {(detail.Length > 2_000 ? detail[^2_000..] : detail)}";
    }

    private async Task<string> GitAsync(
        string workspacePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool credentials = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workspacePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_AUTHOR_NAME"] = "AI Review Worker";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "AI Review Worker";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
        if (credentials)
        {
            startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
            startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            startInfo.Environment["GIT_CONFIG_VALUE_0"] =
                $"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{repositoryToken}"))}";
        }

        // Hooks and credential helpers are disabled; only https is allowed.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.hooksPath=/dev/null");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.allow=never");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("protocol.https.allow=always");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var (exitCode, output, error) = await ExecuteAsync(startInfo, cancellationToken);
        if (exitCode != 0)
        {
            var detail = error.Trim();
            throw new InvalidOperationException(
                $"Git {arguments[0]} failed with exit code {exitCode}." +
                (detail.Length == 0 ? string.Empty : $" Details: {(detail.Length > 2_000 ? detail[..2_000] + "..." : detail)}"));
        }

        return output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
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

        return (process.ExitCode, await stdout, await stderr);
    }
}
