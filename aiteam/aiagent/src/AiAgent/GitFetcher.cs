using System.Diagnostics;

namespace AiAgent;

internal static class GitFetcher
{
    public static async Task<int> RunAsync(WorkspaceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RepositoryUrl))
        {
            Console.Error.WriteLine("AIAGENT_REPOSITORY_URL is not set.");
            return 2;
        }

        Directory.CreateDirectory(options.Root);
        var marker = Path.Combine(options.Root, WorkspaceOptions.MarkerFileName);
        File.Delete(marker);

        List<string> args;
        if (Directory.Exists(Path.Combine(options.CheckoutPath, ".git")))
        {
            args = ["-C", options.CheckoutPath, "pull", "--ff-only"];
        }
        else
        {
            args = ["clone", "--depth", "1"];
            if (!string.IsNullOrWhiteSpace(options.Branch))
            {
                args.AddRange(["--branch", options.Branch]);
            }
            args.AddRange([options.RepositoryUrl, options.CheckoutPath]);
        }

        var exit = await GitAsync(args);
        if (exit != 0)
        {
            return exit;
        }

        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"));
        Console.WriteLine($"Repository available in {options.CheckoutPath}");
        return 0;
    }

    private static async Task<int> GitAsync(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start git.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
