using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AiAgent.GitHub;

/// <summary>
/// Runs git with a scrubbed environment, no hooks, no credential helper and https only.
/// The repository metadata (config, hooks, refs, submodule repositories) lives under
/// <paramref name="metadataRoot"/>, outside the runner-writable workspace, and every command selects it
/// explicitly through GIT_DIR/GIT_WORK_TREE. The runner can therefore not plant executable settings such
/// as core.fsmonitor or filter drivers that a later orchestrator-side git command would run.
/// </summary>
/// <param name="metadataRoot">Orchestrator-private directory; defaults to a folder in the local temp path.</param>
public sealed class GitRunner(string token, string? metadataRoot = null) : IGitRunner
{
    private readonly string metadataRoot = Path.GetFullPath(
        metadataRoot ?? Path.Combine(Path.GetTempPath(), "aiagent-git-metadata"));

    public async Task<GitResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, bool withCredentials,
        CancellationToken cancellationToken, bool allowExitCodeOne = false)
    {
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var isClone = arguments.Count > 0 && arguments[0] == "clone";
        string gitDirectory;
        if (isClone)
        {
            // Target is the last argument (callers pass "--" before the URL and path).
            var target = Path.GetFullPath(arguments[^1], fullWorkingDirectory);
            gitDirectory = MetadataDirectoryFor(target);
            if (Directory.Exists(gitDirectory))
            {
                Directory.Delete(gitDirectory, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(gitDirectory)!);
        }
        else
        {
            gitDirectory = MetadataDirectoryFor(fullWorkingDirectory);
            if (!Directory.Exists(gitDirectory))
            {
                // Never fall back to the .git entry in the workspace: the runner can rewrite it.
                throw new InvalidOperationException(
                    $"No trusted Git metadata exists for '{fullWorkingDirectory}'; clone it with this runner first.");
            }
        }

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
        if (!isClone)
        {
            startInfo.Environment["GIT_DIR"] = gitDirectory;
            startInfo.Environment["GIT_WORK_TREE"] = fullWorkingDirectory;
        }

        if (withCredentials)
        {
            startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
            startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            startInfo.Environment["GIT_CONFIG_VALUE_0"] =
                $"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"))}";
        }

        foreach (var fixedArgument in new[]
                 { "-c", "core.hooksPath=/dev/null", "-c", "credential.helper=", "-c", "protocol.allow=never",
                   "-c", "protocol.https.allow=always", "-c", "core.fsmonitor=false", "-c", "submodule.recurse=false" })
        {
            startInfo.ArgumentList.Add(fixedArgument);
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            startInfo.ArgumentList.Add(arguments[index]);
            if (isClone && index == 0)
            {
                // Keeps config, hooks and submodule repositories out of the shared workspace;
                // the checkout only gets a .git file, which is never trusted afterwards.
                startInfo.ArgumentList.Add($"--separate-git-dir={gitDirectory}");
            }
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

    private string MetadataDirectoryFor(string checkoutPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(checkoutPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
        return Path.Combine(metadataRoot, hash);
    }
}
