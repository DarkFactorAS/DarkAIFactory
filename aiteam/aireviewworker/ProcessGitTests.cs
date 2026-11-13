namespace ReviewWorker.Tests;

public sealed class ProcessGitTests
{
    [Fact]
    public void DeleteWorkspaceRemovesCheckoutAndGitMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"review-test-{Guid.NewGuid():N}");
        var id = Guid.NewGuid().ToString("N");
        var checkout = Path.Combine(root, id, "checkout");
        var gitDirectory = Path.Combine(Path.GetTempPath(), "review-worker-git", id);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(checkout, "a.cs"), "x");
        try
        {
            ProcessGit.DeleteWorkspace(checkout);

            Assert.False(Directory.Exists(Path.Combine(root, id)));
            Assert.False(Directory.Exists(gitDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteWorkspaceRejectsPathsOutsideTheSandboxLayout() =>
        Assert.Throws<InvalidOperationException>(() => ProcessGit.DeleteWorkspace(Path.GetTempPath()));

    [Fact]
    public async Task RefreshWorkspaceMetadataReplacesTheSandboxSnapshotFromTrustedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"review-test-{Guid.NewGuid():N}");
        var id = Guid.NewGuid().ToString("N");
        var checkout = Path.Combine(root, id, "checkout");
        var gitDirectory = Path.Combine(Path.GetTempPath(), "review-worker-git", id);
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        Directory.CreateDirectory(gitDirectory);
        File.WriteAllText(Path.Combine(checkout, ".git", "config"), "stale");
        File.WriteAllText(Path.Combine(checkout, ".git", "stale"), "remove");
        File.WriteAllText(Path.Combine(gitDirectory, "config"), "trusted");

        try
        {
            await new ProcessGit("").RefreshWorkspaceMetadataAsync(checkout, CancellationToken.None);

            Assert.Equal("trusted", File.ReadAllText(Path.Combine(checkout, ".git", "config")));
            Assert.False(File.Exists(Path.Combine(checkout, ".git", "stale")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (Directory.Exists(gitDirectory))
            {
                Directory.Delete(gitDirectory, recursive: true);
            }
        }
    }
}
