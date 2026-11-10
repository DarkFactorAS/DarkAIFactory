using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ManagerAgent;

internal static class GitHubRepository
{
    public static string CreateWorkspacePath()
    {
        var workspaceRoot = Directory.CreateTempSubdirectory("aitechlead-");
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
                configuration.GitHubRepositoryUrl.AbsoluteUri,
                workspacePath
            ],
            cancellationToken,
            useRepository: false,
            requiresGitHubCredentials: true);

        ProtectGitConfiguration(workspacePath);
    }

    public static async Task<bool> IsPullRequestMergedAsync(
        AgentConfiguration configuration,
        Uri repositoryUrl,
        long pullRequestNumber,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        var parts = repositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 ||
            !string.Equals(repositoryUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The stored repository URL \"{repositoryUrl}\" is not in the form https://github.com/owner/repository.");
        }

        var repositoryName = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        using var httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        httpClient.BaseAddress = new Uri("https://api.github.com");
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configuration.GitHubRepositoryToken);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-ManagerAgent/1.0");

        using var response = await httpClient.GetAsync(
            $"/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(repositoryName)}/pulls/{pullRequestNumber}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Reading pull request {pullRequestNumber} failed with status {(int)response.StatusCode}. " +
                "Verify that GitHub.GitHubRepositoryToken has the Pull requests: read permission.");
        }

        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("merged", out var merged) &&
            merged.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    public static async Task<string> CreateOrFindPullRequestAsync(
        AgentConfiguration configuration,
        string headBranch,
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
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-ManagerAgent/1.0");

        using var repositoryResponse = await httpClient.GetAsync(repositoryPath, cancellationToken);
        repositoryResponse.EnsureSuccessStatusCode();
        using var repositoryDocument = System.Text.Json.JsonDocument.Parse(
            await repositoryResponse.Content.ReadAsStringAsync(cancellationToken));
        var defaultBranch = repositoryDocument.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidOperationException("GitHub did not return a default branch.");

        var query =
            $"?state=open&head={Uri.EscapeDataString($"{owner}:{headBranch}")}&base={Uri.EscapeDataString(defaultBranch)}";
        using var existingResponse = await httpClient.GetAsync($"{repositoryPath}/pulls{query}", cancellationToken);
        existingResponse.EnsureSuccessStatusCode();
        using var existingDocument = System.Text.Json.JsonDocument.Parse(
            await existingResponse.Content.ReadAsStringAsync(cancellationToken));
        if (existingDocument.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub returned an invalid pull request list.");
        }

        foreach (var pullRequest in existingDocument.RootElement.EnumerateArray())
        {
            if (pullRequest.TryGetProperty("html_url", out var url) &&
                url.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return url.GetString()!;
            }
        }

        var title = Regex.Replace(summary, @"[\r\n\t]+", " ").Trim();
        var body = new
        {
            title = $"{issueKey}: {title}",
            head = headBranch,
            @base = defaultBranch,
            body = $"All sub-tasks for Jira task {issueKey} are complete.\n\nCreated automatically by the manager."
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
            Path.Combine(Path.GetTempPath(), $"aitechlead-{Guid.NewGuid():N}.gitconfig");
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

    private static (string Owner, string RepositoryName) ParseOwnerAndRepository(Uri repositoryUrl)
    {
        var parts = repositoryUrl.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 ||
            !string.Equals(repositoryUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The repository URL \"{repositoryUrl}\" is not in the form https://github.com/owner/repository.");
        }

        var repositoryName = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        return (parts[0], repositoryName);
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

}
