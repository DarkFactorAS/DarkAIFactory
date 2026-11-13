using AiAgent.GitHub;

namespace AiAgent.Capabilities.Review;

/// <summary>Git operations of the review flow on top of <see cref="IGitRunner"/>.</summary>
public sealed class ReviewGit(IGitRunner git)
{
    private static readonly string[] ProtectedBranches = ["main", "master", "develop", "development", "trunk"];

    public static bool IsPushableBranch(string? branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        branch.Length <= 200 &&
        !branch.StartsWith('-') && !branch.StartsWith('/') && !branch.EndsWith('/') &&
        !branch.Contains("..", StringComparison.Ordinal) &&
        branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/') &&
        !ProtectedBranches.Contains(branch, StringComparer.OrdinalIgnoreCase);

    public async Task CloneAsync(Uri repositoryUrl, string branch, string workspacePath, CancellationToken cancellationToken)
    {
        if (!IsPushableBranch(branch))
        {
            throw new InvalidOperationException($"Refusing to check out branch '{branch}'.");
        }

        var parent = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(workspacePath))!).FullName;
        await git.RunAsync(
            parent,
            ["clone", "--quiet", "--recurse-submodules", "--branch", branch, "--single-branch", "--",
                repositoryUrl.AbsoluteUri, Path.GetFullPath(workspacePath)],
            true, cancellationToken);
    }

    public async Task<string> GetRevisionAsync(string workspacePath, CancellationToken cancellationToken) =>
        (await git.RunAsync(workspacePath, ["rev-parse", "--verify", "HEAD^{commit}"], false, cancellationToken))
        .StandardOutput.Trim();

    /// <summary>Paths changed (committed, modified or untracked) since the baseline revision.</summary>
    public async Task<IReadOnlyList<string>> ChangedFilesAsync(
        string workspacePath, string baselineRevision, CancellationToken cancellationToken)
    {
        await EnsureBaselineIsAncestorAsync(workspacePath, baselineRevision, cancellationToken);
        var tracked = await git.RunAsync(
            workspacePath, ["diff", "--no-renames", "--name-only", "-z", baselineRevision], false, cancellationToken);
        var untracked = await git.RunAsync(
            workspacePath, ["ls-files", "--others", "--exclude-standard", "-z"], false, cancellationToken);
        return tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(untracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task DiscardChangesAsync(string workspacePath, string baselineRevision, CancellationToken cancellationToken)
    {
        await git.RunAsync(workspacePath, ["reset", "--hard", baselineRevision], false, cancellationToken);
        await git.RunAsync(workspacePath, ["clean", "-fd"], false, cancellationToken);
    }

    public async Task CommitAsync(string workspacePath, string message, CancellationToken cancellationToken)
    {
        await git.RunAsync(workspacePath, ["add", "--all"], false, cancellationToken);
        var diff = await git.RunAsync(
            workspacePath, ["diff", "--cached", "--quiet"], false, cancellationToken, allowExitCodeOne: true);
        if (diff.ExitCode == 0)
        {
            return;
        }

        await git.RunAsync(workspacePath, ["commit", "--no-verify", "--message", message], false, cancellationToken);
    }

    /// <summary>Pushes HEAD to the PR head branch. Never forced: a non-fast-forward push is rejected.</summary>
    public async Task PushAsync(
        string workspacePath, string branch, string baselineRevision, CancellationToken cancellationToken)
    {
        if (!IsPushableBranch(branch))
        {
            throw new InvalidOperationException($"Refusing to push to branch '{branch}'.");
        }

        await EnsureBaselineIsAncestorAsync(workspacePath, baselineRevision, cancellationToken);
        await git.RunAsync(
            workspacePath, ["push", "--quiet", "origin", $"HEAD:refs/heads/{branch}"], true, cancellationToken);
    }

    private async Task EnsureBaselineIsAncestorAsync(
        string workspacePath, string baselineRevision, CancellationToken cancellationToken)
    {
        if (baselineRevision.Length is not (40 or 64) || baselineRevision.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidOperationException("The baseline revision is invalid.");
        }

        var result = await git.RunAsync(
            workspacePath, ["merge-base", "--is-ancestor", baselineRevision, "HEAD"], false, cancellationToken,
            allowExitCodeOne: true);
        if (result.ExitCode == 1)
        {
            throw new InvalidOperationException("The checkout no longer contains its starting revision.");
        }
    }
}
