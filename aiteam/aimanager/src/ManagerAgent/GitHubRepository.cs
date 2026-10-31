using System.Diagnostics;
using System.Text;

namespace ManagerAgent;

internal static class GitHubRepository
{
    public static string CreateWorkspacePath()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aimanager-");
        var workspacePath = Path.Combine(workspaceRoot.FullName, "checkout");
        Directory.CreateDirectory(workspacePath);
        var hooksPath = Path.Combine(workspaceRoot.FullName, "disabled-hooks");
        Directory.CreateDirectory(hooksPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return workspacePath;
    }

    public static async Task CloneForPlanningAsync(
        AgentConfiguration configuration,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        await RunGitAsync(
            configuration,
            workspacePath,
            [
                "clone",
                "--quiet",
                "--recurse-submodules",
                "--separate-git-dir",
                GitDirectory(workspacePath),
                "--",
                configuration.RepositoryUrl.AbsoluteUri,
                workspacePath
            ],
            cancellationToken,
            useRepository: false,
            requiresGitHubCredentials: true);

        ProtectGitConfiguration(workspacePath);
    }

    public static void DeleteWorkspace(string workspacePath)
    {
        var workspaceRoot = Directory.GetParent(workspacePath)
            ?? throw new InvalidOperationException("The temporary workspace has no parent directory.");
        if (Directory.Exists(workspaceRoot.FullName))
        {
            var configurationPath = Path.Combine(GitDirectory(workspacePath), "config");
            if (File.Exists(configurationPath))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(
                        configurationPath,
                        File.GetAttributes(configurationPath) & ~FileAttributes.ReadOnly);
                }
                else
                {
                    File.SetUnixFileMode(
                        configurationPath,
                        File.GetUnixFileMode(configurationPath) | UnixFileMode.UserWrite);
                }
            }

            Directory.Delete(workspaceRoot.FullName, recursive: true);
        }
    }

    private static async Task<GitResult> RunGitAsync(
        AgentConfiguration configuration,
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool useRepository = true,
        bool requiresGitHubCredentials = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var workspacePath = workingDirectory
            ?? throw new InvalidOperationException("A workspace is required for Git operations.");
        startInfo.Environment.Clear();
        CopyEnvironmentVariable("PATH", startInfo);
        CopyEnvironmentVariable("SYSTEMROOT", startInfo);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] =
            Path.Combine(Path.GetTempPath(), $"aimanager-{Guid.NewGuid():N}.gitconfig");
        if (requiresGitHubCredentials)
        {
            startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
            startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
            startInfo.Environment["GIT_CONFIG_VALUE_0"] =
                $"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{configuration.GitHubRepositoryToken}"))}";
        }

        if (useRepository)
        {
            startInfo.ArgumentList.Add($"--git-dir={GitDirectory(workspacePath)}");
            startInfo.ArgumentList.Add($"--work-tree={workspacePath}");
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.hooksPath={Path.Combine(WorkspaceRoot(workspacePath), "disabled-hooks")}");
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Git.");
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

        var output = await stdoutTask;
        var error = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git {arguments[0]} failed with exit code {process.ExitCode}." +
                (error.Length == 0 ? string.Empty : $" Details: {Limit(error, 2_000)}"));
        }

        return new GitResult(process.ExitCode, output, error);
    }

    private static void CopyEnvironmentVariable(string name, ProcessStartInfo startInfo)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is not null)
        {
            startInfo.Environment[name] = value;
        }
    }

    private static void ProtectGitConfiguration(string workspacePath)
    {
        var configurationPath = Path.Combine(GitDirectory(workspacePath), "config");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(configurationPath, File.GetAttributes(configurationPath) | FileAttributes.ReadOnly);
            return;
        }

        var mode = File.GetUnixFileMode(configurationPath);
        File.SetUnixFileMode(
            configurationPath,
            mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }

    private static string GitDirectory(string workspacePath) =>
        Path.Combine(WorkspaceRoot(workspacePath), "git");

    private static string WorkspaceRoot(string workspacePath) =>
        Directory.GetParent(workspacePath)?.FullName
        ?? throw new InvalidOperationException("The temporary workspace has no parent directory.");

    private static string Limit(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength] + "...";

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

}
