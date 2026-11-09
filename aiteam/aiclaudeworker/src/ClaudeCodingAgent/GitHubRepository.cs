using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeCodingAgent;

internal static partial class GitHubRepository
{
    public static string CreateWorkspacePath()
    {
        var workspaceBasePath = Environment.GetEnvironmentVariable("CLAUDE_WORKSPACE_ROOT");
        if (string.IsNullOrWhiteSpace(workspaceBasePath))
        {
            workspaceBasePath = null;
        }

        if (workspaceBasePath is not null)
        {
            Directory.CreateDirectory(workspaceBasePath);
        }

        var workspaceRoot = workspaceBasePath is null
            ? Directory.CreateTempSubdirectory("aiclaudeworker-")
            : Directory.CreateDirectory(Path.Combine(
                workspaceBasePath,
                $"aiclaudeworker-{Guid.NewGuid():N}"));
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

    public static async Task<string> CloneForIssueAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string issueKey,
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
                configuration.GitHubRepositoryUrl.AbsoluteUri,
                workspacePath
            ],
            cancellationToken,
            useRepository: false,
            requiresGitHubCredentials: true);

        var issueBranch = IssueBranchPart().Replace(issueKey, "-");
        var branchName = $"ai-agent/{issueBranch}-{Guid.NewGuid():N}";
        await RunGitAsync(
            configuration,
            workspacePath,
            ["switch", "--create", branchName],
            cancellationToken);
        ProtectGitConfiguration(workspacePath);
        return branchName;
    }

    public static async Task<PushResult> CommitAndPushAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string branchName,
        string issueKey,
        string summary,
        string initialRevision,
        CancellationToken cancellationToken)
    {
        var status = await RunGitAsync(
            configuration,
            workspacePath,
            ["status", "--porcelain", "--untracked-files=all"],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return await PushAgentCommitsAsync(
                configuration, workspacePath, branchName, initialRevision, cancellationToken)
                ?? new PushResult("The agent made no repository changes, so nothing was committed or pushed.", null);
        }

        await RunGitAsync(
            configuration,
            workspacePath,
            ["add", "--all", "--", ".", ":(exclude,glob)UserSettings/**"],
            cancellationToken);
        var stagedPaths = await RunGitAsync(
            configuration,
            workspacePath,
            ["diff", "--cached", "--name-only", "-z"],
            cancellationToken);
        if (stagedPaths.StandardOutput.Split('\0').Any(path =>
                path.Equals("UserSettings", StringComparison.Ordinal) ||
                path.StartsWith("UserSettings/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Refusing to commit files under UserSettings/.");
        }

        var stagedChanges = await RunGitAsync(
            configuration,
            workspacePath,
            ["diff", "--cached", "--quiet", "--exit-code"],
            cancellationToken,
            allowExitCodeOne: true);
        if (stagedChanges.ExitCode == 0)
        {
            return await PushAgentCommitsAsync(
                configuration, workspacePath, branchName, initialRevision, cancellationToken)
                ?? new PushResult(
                    "The agent made no committable repository changes, so nothing was committed or pushed.",
                    null);
        }

        var commitSummary = SummaryForCommit().Replace(summary, " ").Trim();
        if (commitSummary.Length > 100)
        {
            commitSummary = commitSummary[..100].TrimEnd();
        }

        await RunGitAsync(
            configuration,
            workspacePath,
            [
                "commit",
                "--message",
                $"{issueKey}: {commitSummary}",
                "--message",
                "Co-authored-by: Claude <noreply@anthropic.com>"
            ],
            cancellationToken);
        await EnsureNoUserSettingsChangesAsync(
            configuration, workspacePath, initialRevision, cancellationToken);
        await RunGitAsync(
            configuration,
            workspacePath,
            ["push", "--quiet", "origin", $"HEAD:refs/heads/{branchName}"],
            cancellationToken,
            requiresGitHubCredentials: true);

        return new PushResult(
            $"Changes were committed and pushed to branch `{branchName}`.",
            branchName);
    }

    public static async Task<string> GetCurrentRevisionAsync(
        AgentConfiguration configuration,
        string workspacePath,
        CancellationToken cancellationToken) =>
        (await RunGitAsync(
            configuration,
            workspacePath,
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken)).StandardOutput.Trim();

    public static (string Owner, string RepositoryName) ParseOwnerAndRepository(Uri gitHubRepositoryUrl)
    {
        var parts = gitHubRepositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("The GitHub repository URL must be in the form https://github.com/owner/repository.");
        }

        var repositoryName = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        return (parts[0], repositoryName);
    }

    public static async Task<string> CreatePullRequestAsync(
        AgentConfiguration configuration,
        string branchName,
        string issueKey,
        string summary,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        var (owner, repositoryName) = ParseOwnerAndRepository(configuration.GitHubRepositoryUrl);
        var repositoryPath =
            $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}";
        using var httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://api.github.com");
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configuration.GitHubRepositoryToken);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-ClaudeCodingAgent/1.0");

        string defaultBranch;
        using (var repositoryResponse = await httpClient.GetAsync(repositoryPath, cancellationToken))
        {
            repositoryResponse.EnsureSuccessStatusCode();
            using var repositoryDocument = System.Text.Json.JsonDocument.Parse(
                await repositoryResponse.Content.ReadAsStringAsync(cancellationToken));
            defaultBranch = repositoryDocument.RootElement.GetProperty("default_branch").GetString()
                ?? throw new InvalidOperationException("GitHub did not return a default branch.");
        }

        var title = SummaryForCommit().Replace(summary, " ").Trim();
        var body = new
        {
            title = $"{issueKey}: {title}",
            head = branchName,
            @base = defaultBranch,
            body = $"Implements Jira task {issueKey}: {title}\n\nCreated automatically by the Claude coding agent."
        };
        using var response = await httpClient.PostAsJsonAsync($"{repositoryPath}/pulls", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var details = Limit(await response.Content.ReadAsStringAsync(cancellationToken), 500);
            throw new InvalidOperationException(
                $"Creating the pull request failed with status {(int)response.StatusCode}. Details: {details}");
        }

        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("GitHub did not return a pull request URL.");
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
        bool allowExitCodeOne = false,
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
            Path.Combine(Path.GetTempPath(), $"aiclaudeworker-{Guid.NewGuid():N}.gitconfig");
        startInfo.Environment["GIT_AUTHOR_NAME"] = "AI Coding Agent";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "AI Coding Agent";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
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
        if (process.ExitCode != 0 && !(allowExitCodeOne && process.ExitCode == 1))
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

    private static async Task<PushResult?> PushAgentCommitsAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string branchName,
        string initialRevision,
        CancellationToken cancellationToken)
    {
        var currentRevision = await GetCurrentRevisionAsync(configuration, workspacePath, cancellationToken);
        if (string.Equals(currentRevision, initialRevision, StringComparison.Ordinal))
        {
            return null;
        }

        await EnsureNoUserSettingsChangesAsync(
            configuration, workspacePath, initialRevision, cancellationToken);
        await RunGitAsync(
            configuration,
            workspacePath,
            ["push", "--quiet", "origin", $"HEAD:refs/heads/{branchName}"],
            cancellationToken,
            requiresGitHubCredentials: true);
        return new PushResult(
            $"The agent's commits were pushed to branch `{branchName}`.",
            branchName);
    }

    private static async Task EnsureNoUserSettingsChangesAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string initialRevision,
        CancellationToken cancellationToken)
    {
        var changedPaths = await RunGitAsync(
            configuration,
            workspacePath,
            ["diff", "--name-only", "-z", initialRevision, "HEAD"],
            cancellationToken);
        if (changedPaths.StandardOutput.Split('\0').Any(path =>
                path.Equals("UserSettings", StringComparison.Ordinal) ||
                path.StartsWith("UserSettings/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Refusing to push commits that modify files under UserSettings/.");
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

    [GeneratedRegex(@"[^\w.-]", RegexOptions.CultureInvariant)]
    private static partial Regex IssueBranchPart();

    [GeneratedRegex(@"[\r\n\t]+", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryForCommit();

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

    public sealed record PushResult(string Message, string? PushedBranchName);
}
