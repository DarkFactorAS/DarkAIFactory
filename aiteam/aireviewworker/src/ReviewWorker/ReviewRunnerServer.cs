using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace ReviewWorker;

internal static class ReviewRunnerServer
{
    private static readonly string[] DeniedTools = ["Bash(git push:*)", "Bash(git commit:*)", "Bash(gh:*)"];

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
                var startInfo = CreateProcessStartInfo(
                    "claude", BuildClaudeArguments(request.Model), ResolveWorkspacePath(request.WorkspaceId));
                startInfo.RedirectStandardInput = true;
                ApplyClaudeEnvironment(startInfo.Environment);
                var (exitCode, output, error) = await ExecuteAsync(startInfo, cancellationToken, request.Prompt);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Claude Code CLI failed with exit code {exitCode}. {Limit(error.Length > 0 ? error : output, 2_000)}");
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
                    "/bin/sh", ["-c", request.Command], ResolveWorkspacePath(request.WorkspaceId));
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

    /// <summary>
    /// Headless <c>claude -p</c> arguments. The prompt is written to standard input. The worker commits
    /// and pushes itself, so the agent may never commit, push or call the GitHub CLI; deny rules take
    /// precedence over <c>bypassPermissions</c>.
    /// </summary>
    internal static IReadOnlyList<string> BuildClaudeArguments(string? model)
    {
        var arguments = new List<string> { "-p", "--output-format", "text" };
        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model.Trim());
        }

        arguments.AddRange(["--permission-mode", "bypassPermissions"]);
        foreach (var tool in DeniedTools)
        {
            arguments.Add("--disallowedTools");
            arguments.Add(tool);
        }

        return arguments;
    }

    /// <summary>Claude Code uses the CLI login stored in <c>CLAUDE_CONFIG_DIR</c> (shared with claude-cli).</summary>
    internal static void ApplyClaudeEnvironment(IDictionary<string, string?> environment)
    {
        environment["CLAUDE_CONFIG_DIR"] =
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? "/claude-config";
        environment["DISABLE_AUTOUPDATER"] = "1";
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken, string? standardInput = null)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (startInfo.RedirectStandardInput)
            {
                await process.StandardInput.WriteAsync((standardInput ?? string.Empty).AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }

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

    private sealed record RunRequest(string WorkspaceId, string Prompt, string Model);
    private sealed record CommandRequest(string WorkspaceId, string Command);
}
