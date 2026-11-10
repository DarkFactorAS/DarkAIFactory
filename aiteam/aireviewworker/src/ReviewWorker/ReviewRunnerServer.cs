using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ReviewWorker;

internal static class ReviewRunnerServer
{
    private static readonly string WorkspaceRoot = Path.GetFullPath(
        Environment.GetEnvironmentVariable("REVIEW_WORKSPACE_ROOT") ?? "/workspaces");

    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:8080");
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        app.MapPost("/run", async (RunRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var (exitCode, output, error) = await RunProcessAsync(
                    "copilot",
                    BuildCopilotArguments(request),
                    ResolveWorkspacePath(request.WorkspaceId),
                    request.GitHubToken,
                    cancellationToken);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Copilot CLI failed with exit code {exitCode}. {Limit(error.Length > 0 ? error : output, 2_000)}");
                }

                return Results.Ok();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        app.MapPost("/command", async (CommandRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var startInfo = CreateProcessStartInfo(
                    "/bin/sh", ["-c", request.Command], ResolveWorkspacePath(request.WorkspaceId), null);
                var (exitCode, output, error) = await ExecuteAsync(startInfo, cancellationToken);
                if (exitCode != 0)
                {
                    var detail = Limit(error.Length > 0 ? error : output, 2_000);
                    return Results.Text($"exit code {exitCode}. {detail}", statusCode: StatusCodes.Status400BadRequest);
                }

                return Results.Ok();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        await app.RunAsync();
    }

    private static string ResolveWorkspacePath(string workspaceId)
    {
        if (!Guid.TryParseExact(workspaceId, "N", out var id))
        {
            throw new InvalidOperationException("The sandbox workspace ID is invalid.");
        }

        var workspacePath = Path.Combine(WorkspaceRoot, id.ToString("N"), "checkout");
        return Directory.Exists(workspacePath)
            ? workspacePath
            : throw new InvalidOperationException("The sandbox workspace does not exist.");
    }

    private static IReadOnlyList<string> BuildCopilotArguments(RunRequest request)
    {
        var arguments = new List<string> { "--prompt", request.Prompt };
        if (!string.IsNullOrEmpty(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        arguments.AddRange([
            "--silent",
            "--trust-folder",
            "--secret-env-vars=GH_TOKEN,GITHUB_TOKEN",
            "--allow-all-tools",
            "--deny-tool=shell(git push)",
            "--deny-tool=shell(git commit)",
            "--deny-tool=shell(gh)"
        ]);
        return arguments;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? githubToken,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo(fileName, arguments, workingDirectory, githubToken);
        return await ExecuteAsync(startInfo, cancellationToken);
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? githubToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var path = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment.Clear();
        if (path is not null)
        {
            startInfo.Environment["PATH"] = path;
        }

        startInfo.Environment["HOME"] = "/tmp";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["TERM"] = "dumb";
        if (githubToken is not null)
        {
            startInfo.Environment["GH_TOKEN"] = githubToken;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
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

        return (process.ExitCode, await stdout, await stderr);
    }

    private static string Limit(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength] + "...";

    private sealed record RunRequest(string WorkspaceId, string Prompt, string Model, string GitHubToken);
    private sealed record CommandRequest(string WorkspaceId, string Command);
}
