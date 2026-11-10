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
            var origin = await GitAsync(["-C", options.CheckoutPath, "config", "--get", "remote.origin.url"]);
            var branch = await GitAsync(["-C", options.CheckoutPath, "symbolic-ref", "--quiet", "--short", "HEAD"]);
            if (origin.ExitCode != 0 || origin.Output.Trim() != options.RepositoryUrl ||
                branch.ExitCode != 0 ||
                (!string.IsNullOrWhiteSpace(options.Branch) && branch.Output.Trim() != options.Branch))
            {
                Console.Error.WriteLine("Existing checkout does not match the configured repository and branch. Use a fresh workspace.");
                return 2;
            }
            args = ["-C", options.CheckoutPath, "pull", "--ff-only", "origin", branch.Output.Trim()];
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

        var result = await GitAsync(args);
        Console.Write(result.Output);
        if (result.ExitCode != 0)
        {
            return result.ExitCode;
        }

        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"));
        Console.WriteLine($"Repository available in {options.CheckoutPath}");
        return 0;
    }

    private static async Task<(int ExitCode, string Output)> GitAsync(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start git.");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }
}
