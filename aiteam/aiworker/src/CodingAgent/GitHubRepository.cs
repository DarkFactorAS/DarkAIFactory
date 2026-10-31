using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodingAgent;

internal static partial class GitHubRepository
{
    public static string CreateWorkspacePath()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "aiworker-workspaces");
        Directory.CreateDirectory(workspaceRoot);
        return Path.Combine(workspaceRoot, Guid.NewGuid().ToString("N"));
    }

    public static async Task<string> CloneForIssueAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string issueKey,
        CancellationToken cancellationToken)
    {
        await RunGitAsync(
            configuration,
            null,
            ["clone", "--quiet", "--", configuration.RepositoryUrl.AbsoluteUri, workspacePath],
            cancellationToken);

        var issueBranch = IssueBranchPart().Replace(issueKey, "-");
        var branchName = $"ai-agent/{issueBranch}-{Guid.NewGuid():N}";
        await RunGitAsync(
            configuration,
            workspacePath,
            ["switch", "--create", branchName],
            cancellationToken);
        return branchName;
    }

    public static async Task<string> CommitAndPushAsync(
        AgentConfiguration configuration,
        string workspacePath,
        string branchName,
        string issueKey,
        string summary,
        CancellationToken cancellationToken)
    {
        var status = await RunGitAsync(
            configuration,
            workspacePath,
            ["status", "--porcelain", "--untracked-files=all"],
            cancellationToken);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return "The agent made no repository changes, so nothing was committed or pushed.";
        }

        await RunGitAsync(configuration, workspacePath, ["add", "--all"], cancellationToken);
        var stagedChanges = await RunGitAsync(
            configuration,
            workspacePath,
            ["diff", "--cached", "--quiet", "--exit-code"],
            cancellationToken,
            allowExitCodeOne: true);
        if (stagedChanges.ExitCode == 0)
        {
            return "The agent made no committable repository changes, so nothing was committed or pushed.";
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
                "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
            ],
            cancellationToken);
        await RunGitAsync(
            configuration,
            workspacePath,
            ["push", "--quiet", "--set-upstream", "origin", branchName],
            cancellationToken);

        return $"Changes were committed and pushed to branch `{branchName}`. No pull request was created.";
    }

    public static void DeleteWorkspace(string workspacePath)
    {
        if (Directory.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static async Task<GitResult> RunGitAsync(
        AgentConfiguration configuration,
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowExitCodeOne = false)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] =
            $"AUTHORIZATION: basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{configuration.GitHubRepositoryToken}"))}";
        startInfo.Environment["GIT_AUTHOR_NAME"] = "AI Coding Agent";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "AI Coding Agent";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "ai-coding-agent@users.noreply.github.com";

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

    private static string Limit(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength] + "...";

    [GeneratedRegex(@"[^\w.-]", RegexOptions.CultureInvariant)]
    private static partial Regex IssueBranchPart();

    [GeneratedRegex(@"[\r\n\t]+", RegexOptions.CultureInvariant)]
    private static partial Regex SummaryForCommit();

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
