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

    public enum PullRequestState
    {
        Open,
        Merged,
        Closed
    }

    public static async Task<PullRequestState> GetPullRequestStateAsync(
        AgentConfiguration configuration,
        long pullRequestNumber,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var document = await GetPullRequestDocumentAsync(
            configuration, pullRequestNumber, cancellationToken, handler);
        var root = document.RootElement;
        if (root.TryGetProperty("merged", out var merged) && merged.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            return PullRequestState.Merged;
        }

        var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
        return string.Equals(state, "open", StringComparison.OrdinalIgnoreCase)
            ? PullRequestState.Open
            : PullRequestState.Closed;
    }

    public static async Task<bool> BranchExistsAsync(
        AgentConfiguration configuration,
        string branchName,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var httpClient = CreateApiClient(configuration, handler);
        using var response = await httpClient.GetAsync(
            $"{RepositoryApiPath(configuration)}/git/ref/heads/{EscapeBranchPath(branchName)}",
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, $"Checking whether branch {branchName} exists", cancellationToken);
        return true;
    }

    /// <summary>Creates <paramref name="branchName"/> at the tip of the default branch and returns the commit SHA.</summary>
    public static async Task<string> CreateBranchFromDefaultBranchAsync(
        AgentConfiguration configuration,
        string branchName,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var httpClient = CreateApiClient(configuration, handler);
        var repositoryPath = RepositoryApiPath(configuration);
        var defaultBranch = await GetDefaultBranchAsync(httpClient, repositoryPath, cancellationToken);
        using var refResponse = await httpClient.GetAsync(
            $"{repositoryPath}/git/ref/heads/{EscapeBranchPath(defaultBranch)}", cancellationToken);
        await EnsureSuccessAsync(refResponse, $"Reading default branch {defaultBranch}", cancellationToken);
        using var refDocument = System.Text.Json.JsonDocument.Parse(
            await refResponse.Content.ReadAsStringAsync(cancellationToken));
        var sha = refDocument.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("GitHub did not return the default branch commit.");

        using var response = await httpClient.PostAsJsonAsync(
            $"{repositoryPath}/git/refs",
            new { @ref = $"refs/heads/{branchName}", sha },
            cancellationToken);
        await EnsureSuccessAsync(response, $"Creating branch {branchName}", cancellationToken);
        return sha;
    }

    public static async Task AddPullRequestCommentAsync(
        AgentConfiguration configuration,
        long pullRequestNumber,
        string comment,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var httpClient = CreateApiClient(configuration, handler);
        using var response = await httpClient.PostAsJsonAsync(
            $"{RepositoryApiPath(configuration)}/issues/{pullRequestNumber}/comments",
            new { body = comment },
            cancellationToken);
        await EnsureSuccessAsync(response, $"Commenting on pull request {pullRequestNumber}", cancellationToken);
    }

    public static async Task ClosePullRequestWithCommentAsync(
        AgentConfiguration configuration,
        long pullRequestNumber,
        string comment,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        await AddPullRequestCommentAsync(configuration, pullRequestNumber, comment, cancellationToken, handler);
        using var httpClient = CreateApiClient(configuration, handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Patch, $"{RepositoryApiPath(configuration)}/pulls/{pullRequestNumber}")
        {
            Content = JsonContent.Create(new { state = "closed" })
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"Closing pull request {pullRequestNumber}", cancellationToken);
    }

    /// <summary>Creates a pull request from <paramref name="headBranch"/> into an explicit base branch.</summary>
    public static async Task<string> CreatePullRequestWithBaseAsync(
        AgentConfiguration configuration,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var httpClient = CreateApiClient(configuration, handler);
        using var response = await httpClient.PostAsJsonAsync(
            $"{RepositoryApiPath(configuration)}/pulls",
            new { title, head = headBranch, @base = baseBranch, body },
            cancellationToken);
        await EnsureSuccessAsync(response, "Creating the pull request", cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("GitHub did not return a pull request URL.");
    }

    /// <summary>
    /// Reverts a merged pull request on a new branch of the cloned workspace, pushes it and opens a pull request
    /// against the branch the original pull request was merged into. Returns the new pull request URL.
    /// </summary>
    public static async Task<string> CreateRevertPullRequestAsync(
        AgentConfiguration configuration,
        string workspacePath,
        long pullRequestNumber,
        string revertBranchName,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var document = await GetPullRequestDocumentAsync(
            configuration, pullRequestNumber, cancellationToken, handler);
        var root = document.RootElement;
        if (!root.TryGetProperty("merged", out var merged) || merged.ValueKind != System.Text.Json.JsonValueKind.True)
        {
            throw new InvalidOperationException($"Pull request {pullRequestNumber} is not merged and cannot be reverted.");
        }

        var mergeSha = root.TryGetProperty("merge_commit_sha", out var shaElement) ? shaElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(mergeSha))
        {
            throw new InvalidOperationException($"Pull request {pullRequestNumber} has no merge commit to revert.");
        }

        var baseBranch = root.GetProperty("base").GetProperty("ref").GetString()
            ?? throw new InvalidOperationException("GitHub did not return the pull request base branch.");
        var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;

        await RunGitAsync(
            configuration,
            workspacePath,
            ["fetch", "--quiet", "origin", $"+refs/heads/{baseBranch}:refs/remotes/origin/{baseBranch}"],
            cancellationToken,
            requiresGitHubCredentials: true);
        await RunGitAsync(
            configuration,
            workspacePath,
            ["switch", "--no-track", "--create", revertBranchName, $"origin/{baseBranch}"],
            cancellationToken);
        var commit = await RunGitAsync(
            configuration, workspacePath, ["rev-list", "--parents", "-n", "1", mergeSha], cancellationToken);
        var isMergeCommit = commit.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2;
        await RunGitAsync(
            configuration,
            workspacePath,
            isMergeCommit
                ? ["revert", "--no-edit", "-m", "1", mergeSha]
                : ["revert", "--no-edit", mergeSha],
            cancellationToken);
        await RunGitAsync(
            configuration,
            workspacePath,
            ["push", "--quiet", "origin", $"HEAD:refs/heads/{revertBranchName}"],
            cancellationToken,
            requiresGitHubCredentials: true);

        return await CreatePullRequestWithBaseAsync(
            configuration,
            revertBranchName,
            baseBranch,
            $"Revert \"{Regex.Replace(title ?? $"pull request {pullRequestNumber}", @"[\r\n\t]+", " ").Trim()}\"",
            $"Reverts pull request #{pullRequestNumber}.\n\nCreated automatically.",
            cancellationToken,
            handler);
    }

    /// <summary>
    /// Creates <paramref name="newBranchName"/> from <paramref name="startPoint"/> (a ref or commit in the cloned
    /// workspace), cherry-picks the commits in order and pushes the branch.
    /// </summary>
    public static async Task CherryPickToNewBranchAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string newBranchName,
        string startPoint,
        IReadOnlyList<string> commits,
        CancellationToken cancellationToken)
    {
        if (commits.Count == 0)
        {
            throw new ArgumentException("At least one commit is required.", nameof(commits));
        }

        await RunGitAsync(
            configuration,
            workspacePath,
            ["switch", "--no-track", "--create", newBranchName, startPoint],
            cancellationToken);
        try
        {
            await RunGitAsync(
                configuration, workspacePath, ["cherry-pick", "--allow-empty", .. commits], cancellationToken);
        }
        catch (InvalidOperationException)
        {
            try
            {
                await RunGitAsync(configuration, workspacePath, ["cherry-pick", "--abort"], cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Nothing to abort; surface the original failure.
            }

            throw;
        }

        await RunGitAsync(
            configuration,
            workspacePath,
            ["push", "--quiet", "origin", $"HEAD:refs/heads/{newBranchName}"],
            cancellationToken,
            requiresGitHubCredentials: true);
    }

    public sealed record PullRequestInfo(
        long Number,
        string Url,
        PullRequestState State,
        string BaseBranch,
        string HeadBranch,
        string Title);

    public static async Task<PullRequestInfo> GetPullRequestInfoAsync(
        AgentConfiguration configuration,
        long pullRequestNumber,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var document = await GetPullRequestDocumentAsync(
            configuration, pullRequestNumber, cancellationToken, handler);
        return ReadPullRequestInfo(document.RootElement);
    }

    /// <summary>
    /// Finds the newest pull request (any state) from <paramref name="headBranch"/> into <paramref name="baseBranch"/>,
    /// or null when none exists. Used to make PR creation idempotent.
    /// </summary>
    public static async Task<PullRequestInfo?> FindPullRequestAsync(
        AgentConfiguration configuration,
        string headBranch,
        string baseBranch,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        var (owner, _) = ParseOwnerAndRepository(configuration.GitHubRepositoryUrl);
        using var httpClient = CreateApiClient(configuration, handler);
        using var response = await httpClient.GetAsync(
            $"{RepositoryApiPath(configuration)}/pulls?state=all&per_page=1&sort=created&direction=desc" +
            $"&head={Uri.EscapeDataString($"{owner}:{headBranch}")}&base={Uri.EscapeDataString(baseBranch)}",
            cancellationToken);
        await EnsureSuccessAsync(response, $"Looking for a pull request from {headBranch}", cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetArrayLength() == 0
            ? null
            : ReadPullRequestInfo(document.RootElement[0]);
    }

    private static PullRequestInfo ReadPullRequestInfo(System.Text.Json.JsonElement root)
    {
        var merged = root.TryGetProperty("merged", out var mergedElement)
            && mergedElement.ValueKind == System.Text.Json.JsonValueKind.True;
        // The list endpoint has no "merged" flag; merged_at identifies merged pull requests there.
        merged |= root.TryGetProperty("merged_at", out var mergedAt)
            && mergedAt.ValueKind == System.Text.Json.JsonValueKind.String;
        var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
        return new PullRequestInfo(
            root.GetProperty("number").GetInt64(),
            root.GetProperty("html_url").GetString() ?? string.Empty,
            merged
                ? PullRequestState.Merged
                : string.Equals(state, "open", StringComparison.OrdinalIgnoreCase)
                    ? PullRequestState.Open
                    : PullRequestState.Closed,
            root.GetProperty("base").GetProperty("ref").GetString() ?? string.Empty,
            root.GetProperty("head").GetProperty("ref").GetString() ?? string.Empty,
            root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty);
    }

    /// <summary>Clones the repository into an empty <paramref name="workspacePath"/> without creating a branch.</summary>
    public static async Task CloneRepositoryAsync(
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

    /// <summary>
    /// Cherry-picks the commits of a (merged) pull request onto a new branch started from
    /// <paramref name="targetBranch"/> and pushes it. Works for squash, rebase and merge-commit merges because the
    /// commits are read from the pull request itself.
    /// </summary>
    public static async Task CherryPickPullRequestToNewBranchAsync(
        AgentConfiguration configuration,
        string workspacePath,
        long pullRequestNumber,
        string targetBranch,
        string newBranchName,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        using var httpClient = CreateApiClient(configuration, handler);
        var commits = new List<string>();
        for (var page = 1; ; page++)
        {
            using var response = await httpClient.GetAsync(
                $"{RepositoryApiPath(configuration)}/pulls/{pullRequestNumber}/commits?per_page=100&page={page}",
                cancellationToken);
            await EnsureSuccessAsync(response, $"Reading commits of pull request {pullRequestNumber}", cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var items = document.RootElement.EnumerateArray().ToArray();
            commits.AddRange(items.Select(item => item.GetProperty("sha").GetString()!));
            if (items.Length < 100)
            {
                break;
            }
        }

        await RunGitAsync(
            configuration,
            workspacePath,
            [
                "fetch", "--quiet", "origin",
                $"+refs/heads/{targetBranch}:refs/remotes/origin/{targetBranch}",
                $"+refs/pull/{pullRequestNumber}/head:refs/remotes/origin/pr/{pullRequestNumber}"
            ],
            cancellationToken,
            requiresGitHubCredentials: true);
        await CherryPickToNewBranchAsync(
            configuration, workspacePath, newBranchName, $"origin/{targetBranch}", commits, cancellationToken);
    }

    private static async Task<System.Text.Json.JsonDocument> GetPullRequestDocumentAsync(
        AgentConfiguration configuration,
        long pullRequestNumber,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler)
    {
        using var httpClient = CreateApiClient(configuration, handler);
        using var response = await httpClient.GetAsync(
            $"{RepositoryApiPath(configuration)}/pulls/{pullRequestNumber}", cancellationToken);
        await EnsureSuccessAsync(response, $"Reading pull request {pullRequestNumber}", cancellationToken);
        return System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task<string> GetDefaultBranchAsync(
        HttpClient httpClient, string repositoryPath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(repositoryPath, cancellationToken);
        await EnsureSuccessAsync(response, "Reading the repository", cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidOperationException("GitHub did not return a default branch.");
    }

    private static HttpClient CreateApiClient(AgentConfiguration configuration, HttpMessageHandler? handler)
    {
        var httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        httpClient.BaseAddress = new Uri("https://api.github.com");
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configuration.GitHubRepositoryToken);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-ManagerAgent/1.0");
        return httpClient;
    }

    private static string RepositoryApiPath(AgentConfiguration configuration)
    {
        var (owner, repositoryName) = ParseOwnerAndRepository(configuration.GitHubRepositoryUrl);
        return $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}";
    }

    private static string EscapeBranchPath(string branchName) =>
        string.Join('/', branchName.Split('/').Select(Uri.EscapeDataString));

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = Limit(await response.Content.ReadAsStringAsync(cancellationToken), 500);
        throw new InvalidOperationException(
            $"{action} failed with status {(int)response.StatusCode}. Details: {details}");
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
        startInfo.Environment["GIT_AUTHOR_NAME"] = "AI Manager Agent";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "ai-manager-agent@users.noreply.github.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "AI Manager Agent";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "ai-manager-agent@users.noreply.github.com";
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
