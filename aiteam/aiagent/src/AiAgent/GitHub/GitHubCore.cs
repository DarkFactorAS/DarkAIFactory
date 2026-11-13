using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiAgent.Core;
using AiAgent.Repositories;

namespace AiAgent.GitHub;

public sealed record PushResult(bool Pushed, string Message);

/// <summary>Clone, branch, commit, push (never forced), pull request and its bookkeeping.</summary>
public sealed partial class GitHubCore(
    IGitRunner git,
    Uri repositoryUrl,
    string token,
    IAgentPullRequestRepository pullRequests,
    HttpMessageHandler? httpHandler = null)
{
    // Base revision recorded right after branching, before the runner touches the checkout.
    private readonly ConcurrentDictionary<string, string> baseRevisions = new(StringComparer.Ordinal);

    public static string BranchName(string issueKey, string summary, string? suffix = null)
    {
        var key = NonBranchCharacters().Replace(issueKey.Trim(), "-").Trim('-');
        var slug = NonBranchCharacters().Replace(summary.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }

        suffix ??= Guid.NewGuid().ToString("N")[..8];
        return slug.Length == 0 ? $"aiagent/{key}-{suffix}" : $"aiagent/{key}-{slug}-{suffix}";
    }

    public static string PullRequestTitle(string issueKey, string summary) =>
        $"aiagent: {issueKey} {WhiteSpace().Replace(summary, " ").Trim()}".TrimEnd();

    /// <summary>Clones with submodules into <paramref name="checkoutPath"/> and creates the work branch.</summary>
    public async Task<string> CloneAndBranchAsync(
        string checkoutPath, string issueKey, string summary, string? baseBranch, CancellationToken cancellationToken)
    {
        checkoutPath = Path.GetFullPath(checkoutPath);
        var parent = Directory.CreateDirectory(Path.GetDirectoryName(checkoutPath)!).FullName;
        await git.RunAsync(
            parent,
            ["clone", "--quiet", "--recurse-submodules", "--", repositoryUrl.AbsoluteUri, checkoutPath],
            true, cancellationToken);
        if (baseBranch is not null)
        {
            await git.RunAsync(checkoutPath, ["fetch", "--quiet", "origin", baseBranch], true, cancellationToken);
        }

        var branch = BranchName(issueKey, summary);
        await git.RunAsync(
            checkoutPath,
            baseBranch is null
                ? ["switch", "--create", branch]
                : ["switch", "--no-track", "--create", branch, $"origin/{baseBranch}"],
            false, cancellationToken);
        // The recursive clone registered submodule URLs from the default branch; resync them so a
        // base branch that changes a submodule URL fetches from the right repository.
        await git.RunAsync(checkoutPath, ["submodule", "sync", "--quiet", "--recursive"], false, cancellationToken);
        await git.RunAsync(checkoutPath, ["submodule", "update", "--init", "--recursive"], true, cancellationToken);
        var head = await git.RunAsync(checkoutPath, ["rev-parse", "--verify", "HEAD^{commit}"], false, cancellationToken);
        var baseRevision = head.StandardOutput.Trim();
        if (baseRevision.Length > 0)
        {
            baseRevisions[Path.GetFullPath(checkoutPath)] = baseRevision;
        }

        return branch;
    }

    public async Task<PushResult> CommitAndPushAsync(
        string checkoutPath, string branch, string issueKey, string summary, CancellationToken cancellationToken)
    {
        await git.RunAsync(
            checkoutPath, ["add", "--all", "--", ".", ":(exclude,glob)UserSettings/**"], false, cancellationToken);
        var staged = await git.RunAsync(
            checkoutPath, ["diff", "--cached", "--name-only", "-z"], false, cancellationToken);
        if (staged.StandardOutput.Split('\0').Any(path => path.StartsWith("UserSettings/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Refusing to commit files under UserSettings/.");
        }

        var diff = await git.RunAsync(
            checkoutPath, ["diff", "--cached", "--quiet", "--exit-code"], false, cancellationToken,
            allowExitCodeOne: true);
        if (diff.ExitCode == 0)
        {
            return new PushResult(false, "No committable changes; nothing was committed or pushed.");
        }

        await git.RunAsync(
            checkoutPath, ["commit", "--message", $"{issueKey}: {WhiteSpace().Replace(summary, " ").Trim()}"],
            false, cancellationToken);
        await EnsureOutgoingCommitsAreAllowedAsync(checkoutPath, cancellationToken);
        // Never force: a rejected push must surface as an error.
        await git.RunAsync(checkoutPath, ["push", "--quiet", "origin", $"HEAD:refs/heads/{branch}"], true, cancellationToken);
        return new PushResult(true, $"Pushed branch `{branch}`.");
    }

    /// <summary>
    /// Rejects the push when any commit between the trusted pre-run revision and HEAD touches UserSettings/.
    /// The runner may have committed on its own, so the index alone is not sufficient.
    /// </summary>
    private async Task EnsureOutgoingCommitsAreAllowedAsync(string checkoutPath, CancellationToken cancellationToken)
    {
        string[] range = baseRevisions.TryGetValue(Path.GetFullPath(checkoutPath), out var baseRevision)
            ? [$"{baseRevision}..HEAD"]
            : ["HEAD", "--not", "--remotes"];
        var changed = await git.RunAsync(
            checkoutPath,
            ["log", "--no-renames", "--diff-merges=first-parent", "--name-only", "--format=", "-z", .. range, "--"],
            false, cancellationToken);
        if (changed.StandardOutput.Split('\0', '\n').Any(path => path.StartsWith("UserSettings/", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Refusing to push commits that change files under UserSettings/.");
        }
    }

    /// <summary>Opens the PR (title 'aiagent: KEY summary') and records it in agent_pull_requests.</summary>
    public async Task<AgentPullRequestRecord> OpenPullRequestAsync(
        long agentId, string issueKey, string summary, string branch, string? baseBranch, string body,
        CancellationToken cancellationToken)
    {
        var (owner, repo) = ParseOwnerAndRepository(repositoryUrl);
        var path = $"/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        using var client = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-AiAgent/1.0");

        if (baseBranch is null)
        {
            using var repoResponse = await client.GetAsync(path, cancellationToken);
            await EnsureSuccessAsync(repoResponse, "Reading the repository", cancellationToken);
            using var repoDoc = JsonDocument.Parse(await repoResponse.Content.ReadAsStringAsync(cancellationToken));
            baseBranch = repoDoc.RootElement.GetProperty("default_branch").GetString()
                ?? throw new InvalidOperationException("GitHub did not return a default branch.");
        }

        using var response = await client.PostAsJsonAsync(
            $"{path}/pulls",
            new { title = PullRequestTitle(issueKey, summary), head = branch, @base = baseBranch, body },
            cancellationToken);
        await EnsureSuccessAsync(response, "Creating the pull request", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var url = document.RootElement.GetProperty("html_url").GetString()
            ?? throw new InvalidOperationException("GitHub did not return a pull request URL.");
        var number = document.RootElement.GetProperty("number").GetInt64();

        var record = new AgentPullRequestRecord(agentId, issueKey, repositoryUrl.AbsoluteUri, number, url);
        pullRequests.Record(record);
        return record;
    }

    public static (string Owner, string Repository) ParseOwnerAndRepository(Uri url)
    {
        var parts = url.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2)
        {
            throw new InvalidOperationException("The repository URL must be https://github.com/owner/repository.");
        }

        return (parts[0], parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1]);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string action, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"{action} failed with status {(int)response.StatusCode}. Details: {(details.Length > 500 ? details[..500] : details)}");
    }

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex NonBranchCharacters();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
