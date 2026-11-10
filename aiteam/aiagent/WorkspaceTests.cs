using System.Diagnostics;
using AiAgent;

namespace AiAgent.Tests;

public class WorkspaceTests
{
    [Fact]
    public void FromEnvironment_UsesDefaults()
    {
        var options = WorkspaceOptions.FromEnvironment(_ => null);
        Assert.Equal("/workspaces", options.Root);
        Assert.Equal(Path.Combine("/workspaces", "repo"), options.CheckoutPath);
    }

    [Fact]
    public async Task FetchThenInspect_SharesCheckoutThroughVolume()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "x");
            Git(source, "init", "-q");
            Git(source, "add", ".");
            Git(source, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "i");

            var options = new WorkspaceOptions(Path.Combine(root, "ws"), source, "");
            Assert.False(RunnerServer.Inspect(options).Ready);
            Assert.Equal(0, await GitFetcher.RunAsync(options));
            var status = RunnerServer.Inspect(options);
            Assert.True(status.Ready);
            Assert.True(status.EntryCount >= 2);

            await File.WriteAllTextAsync(Path.Combine(source, "a.txt"), "updated");
            Git(source, "add", ".");
            Git(source, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "update");
            Assert.Equal(0, await GitFetcher.RunAsync(options));
            Assert.Equal("updated", await File.ReadAllTextAsync(Path.Combine(options.CheckoutPath, "a.txt")));
            Assert.True(RunnerServer.Inspect(options).Ready);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("branch")]
    [InlineData("detached")]
    public async Task Fetch_WithMismatchedCheckout_FailsAndClearsReadiness(string mismatch)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            Git(source, "init", "-q", "--initial-branch=main");
            Git(source, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "initial");

            var options = new WorkspaceOptions(Path.Combine(root, "ws"), source, "main");
            Assert.Equal(0, await GitFetcher.RunAsync(options));
            Assert.True(RunnerServer.Inspect(options).Ready);

            if (mismatch == "repository")
            {
                options = options with { RepositoryUrl = Path.Combine(root, "other") };
            }
            else if (mismatch == "branch")
            {
                Git(source, "branch", "other");
                options = options with { Branch = "other" };
            }
            else
            {
                Git(options.CheckoutPath, "checkout", "--detach");
            }

            Assert.Equal(2, await GitFetcher.RunAsync(options));
            Assert.False(RunnerServer.Inspect(options).Ready);
            Assert.False(File.Exists(Path.Combine(options.Root, WorkspaceOptions.MarkerFileName)));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Fetch_WithoutUrl_Fails()
    {
        var options = new WorkspaceOptions(Directory.CreateTempSubdirectory().FullName, "", "");
        Assert.Equal(2, await GitFetcher.RunAsync(options));
    }

    private static void Git(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }
        using var p = Process.Start(info)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
