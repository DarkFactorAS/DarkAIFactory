using System.Diagnostics;
using System.Text;

namespace ReviewWorker;

/// <summary>Git operations backed by real processes.</summary>
internal sealed class ProcessGit(string repositoryToken) : IReviewGit
{
    public static string CreateWorkspacePath()
    {
        var workspaceRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable("REVIEW_WORKSPACE_ROOT") ?? Path.Combine(Path.GetTempPath(), "review-workspaces"));
        var checkoutPath = Path.Combine(workspaceRoot, Guid.NewGuid().ToString("N"), "checkout");
        Directory.CreateDirectory(checkoutPath);
        return checkoutPath;
    }

    /// <summary>Deletes the workspace folder and its Git metadata.</summary>
    public static void DeleteWorkspace(string workspacePath)
    {
        foreach (var path in new[] { GitDirectory(workspacePath), Path.GetDirectoryName(Path.GetFullPath(workspacePath))! })
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    public async Task CloneAsync(Uri repositoryUrl, string branch, string workspacePath, CancellationToken cancellationToken)
    {
        if (!FixDecision.IsPushableBranch(branch))
        {
            throw new InvalidOperationException($"Refusing to check out branch '{branch}'.");
        }

        Directory.CreateDirectory(workspacePath);
        // Keep Git metadata outside the shared checkout so sandboxed code cannot rewrite refs or configuration.
        var gitDirectory = GitDirectory(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(gitDirectory)!);
        await GitAsync(workspacePath,
            ["clone", "--separate-git-dir", gitDirectory, "--branch", branch, "--single-branch", "--", repositoryUrl.AbsoluteUri, "."],
            cancellationToken, credentials: true, useWorkspaceRepository: false);
        File.Delete(Path.Combine(workspacePath, ".git"));
        CopyDirectory(gitDirectory, Path.Combine(workspacePath, ".git"));
    }

    public async Task<string> GetRevisionAsync(string workspacePath, CancellationToken cancellationToken) =>
        (await GitAsync(workspacePath, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken)).Trim();

    public Task RefreshWorkspaceMetadataAsync(string workspacePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.Combine(workspacePath, ".git");
        var staging = Path.Combine(workspacePath, $".git-refresh-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(GitDirectory(workspacePath), staging);
            cancellationToken.ThrowIfCancellationRequested();
            DeletePath(destination);
            Directory.Move(staging, destination);
            return Task.CompletedTask;
        }
        finally
        {
            DeletePath(staging);
        }
    }

    public async Task<IReadOnlyList<string>> ChangedFilesAsync(
        string workspacePath, string baselineRevision, CancellationToken cancellationToken)
    {
        await EnsureBaselineIsAncestorAsync(workspacePath, baselineRevision, cancellationToken);
        var tracked = await GitAsync(workspacePath, ["diff", "--name-only", "-z", baselineRevision], cancellationToken);
        var untracked = await GitAsync(workspacePath, ["ls-files", "--others", "--exclude-standard", "-z"], cancellationToken);
        return tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task DiscardChangesAsync(
        string workspacePath, string baselineRevision, CancellationToken cancellationToken)
    {
        await GitAsync(workspacePath, ["reset", "--hard", baselineRevision], cancellationToken);
        await GitAsync(workspacePath, ["clean", "-fd"], cancellationToken);
    }

    public async Task CommitAsync(string workspacePath, string message, CancellationToken cancellationToken)
    {
        await GitAsync(workspacePath, ["add", "--all"], cancellationToken);
        if (await GitAsync(workspacePath, ["diff", "--cached", "--quiet"], cancellationToken, allowExitCodeOne: true) == "0")
        {
            return;
        }

        await GitAsync(workspacePath, ["commit", "--no-verify", "-m", message], cancellationToken);
    }

    public async Task PushAsync(
        string workspacePath, Uri repositoryUrl, string branch, string baselineRevision, CancellationToken cancellationToken)
    {
        if (!FixDecision.IsPushableBranch(branch))
        {
            throw new InvalidOperationException($"Refusing to push to branch '{branch}'.");
        }

        await EnsureBaselineIsAncestorAsync(workspacePath, baselineRevision, cancellationToken);
        var pushRepositoryPath = Path.Combine(Path.GetTempPath(), $"review-worker-push-{Guid.NewGuid():N}");
        try
        {
            await GitAsync(
                workspacePath, ["init", "--bare", pushRepositoryPath], cancellationToken, useWorkspaceRepository: false);
            await GitAsync(workspacePath,
                ["--git-dir", pushRepositoryPath, "fetch", "--no-tags", "--no-recurse-submodules", "--", GitDirectory(workspacePath), "HEAD"],
                cancellationToken, allowFileProtocol: true, useWorkspaceRepository: false);

            // Use an isolated Git config and the trusted URL so checkout configuration cannot redirect the token.
            // Plain (non-forced, no '+') refspec: a non-fast-forward push is rejected by GitHub.
            await GitAsync(workspacePath,
                ["--git-dir", pushRepositoryPath, "push", "--no-recurse-submodules", "--",
                    repositoryUrl.AbsoluteUri, $"FETCH_HEAD:refs/heads/{branch}"],
                cancellationToken, credentials: true, useWorkspaceRepository: false);
        }
        finally
        {
            if (Directory.Exists(pushRepositoryPath))
            {
                Directory.Delete(pushRepositoryPath, recursive: true);
            }
        }
    }

    private async Task EnsureBaselineIsAncestorAsync(
        string workspacePath, string baselineRevision, CancellationToken cancellationToken)
    {
        if (baselineRevision.Length is not (40 or 64) ||
            baselineRevision.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("The baseline revision is invalid.");
        }

        var result = await GitAsync(
            workspacePath, ["merge-base", "--is-ancestor", baselineRevision, "HEAD"], cancellationToken, allowExitCodeOne: true);
        if (result == "1")
        {
            throw new InvalidOperationException("The checkout no longer contains its starting revision.");
        }
    }

    private async Task<string> GitAsync(
        string workspacePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool credentials = false,
        bool allowFileProtocol = false,
        bool allowExitCodeOne = false,
        bool useWorkspaceRepository = true)
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
        if (useWorkspaceRepository)
        {
            startInfo.ArgumentList.Add($"--git-dir={GitDirectory(workspacePath)}");
            startInfo.ArgumentList.Add($"--work-tree={Path.GetFullPath(workspacePath)}");
        }

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
        if (allowFileProtocol)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("protocol.file.allow=always");
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var (exitCode, output, error) = await ExecuteAsync(startInfo, cancellationToken);
        if (exitCode != 0 && !(allowExitCodeOne && exitCode == 1))
        {
            var detail = error.Trim();
            throw new InvalidOperationException(
                $"Git {arguments[0]} failed with exit code {exitCode}." +
                (detail.Length == 0 ? string.Empty : $" Details: {(detail.Length > 2_000 ? detail[..2_000] + "..." : detail)}"));
        }

        return allowExitCodeOne ? exitCode.ToString() : output;
    }

    private static string GitDirectory(string workspacePath)
    {
        var checkout = new DirectoryInfo(Path.GetFullPath(workspacePath));
        var workspaceId = checkout.Parent?.Name;
        if (checkout.Name != "checkout" || !Guid.TryParseExact(workspaceId, "N", out var id))
        {
            throw new InvalidOperationException("The workspace is not in the shared sandbox volume.");
        }

        return Path.Combine(Path.GetTempPath(), "review-worker-git", id.ToString("N"));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void DeletePath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
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
