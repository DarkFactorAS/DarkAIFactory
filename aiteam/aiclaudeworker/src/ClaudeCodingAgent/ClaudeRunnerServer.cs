using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

namespace ClaudeCodingAgent;

internal static class ClaudeRunnerServer
{
    private static readonly string WorkspaceRoot = Path.GetFullPath(
        Environment.GetEnvironmentVariable("CLAUDE_WORKSPACE_ROOT") ?? "/workspaces");

    public static async Task RunAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://0.0.0.0:8080");
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        app.MapPost("/check", async (RunnerRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var (versionCode, _, versionError) = await RunCliAsync(
                    ["--version"], Path.GetTempPath(), request.AnthropicCredential, cancellationToken);
                if (versionCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Claude Code CLI version check failed with exit code {versionCode}. {Limit(versionError, 2_000)}");
                }

                if (request.AnthropicCredential.Length == 0)
                {
                    var (statusCode, statusOutput, _) = await RunCliAsync(
                        ["auth", "status"], Path.GetTempPath(), string.Empty, cancellationToken);
                    if (statusCode != 0)
                    {
                        throw new InvalidOperationException(
                            "The Claude Code CLI is not logged in. Log in once with " +
                            "`docker compose run --rm -it --entrypoint claude claude-cli auth login` " +
                            "(the login is kept in the claude_config volume), or set the aiclaudeworker " +
                            "Claude.AnthropicApiKey setting. " + Limit(statusOutput, 500));
                    }
                }

                return Results.Ok();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        app.MapPost("/run", async (RunnerRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var workspacePath = ValidateWorkspacePath(request.WorkspacePath);
                var (exitCode, output, error) = await RunCliAsync(
                    ClaudeRunner.BuildArguments(request.Model, request.AllowChanges),
                    workspacePath,
                    request.AnthropicCredential,
                    cancellationToken,
                    request.Prompt);
                if (exitCode != 0)
                {
                    var detail = error.Length == 0
                        ? output.Length == 0 ? string.Empty : $" Details: {Limit(output, 2_000)}"
                        : $" Details: {Limit(error, 2_000)}";
                    throw new InvalidOperationException($"Claude Code CLI failed with exit code {exitCode}.{detail}");
                }

                return Results.Json(new RunnerResponse(output.Length > 0
                    ? output
                    : throw new InvalidOperationException("Claude Code CLI returned no response.")));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        await app.RunAsync();
    }

    private static string ValidateWorkspacePath(string workspacePath)
    {
        var fullPath = Path.GetFullPath(workspacePath);
        var workspacePrefix = WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                workspacePrefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Directory.Exists(fullPath))
        {
            throw new InvalidOperationException("The Claude CLI workspace must be an existing directory under /workspaces.");
        }

        return fullPath;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string anthropicCredential,
        CancellationToken cancellationToken,
        string? prompt = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var path = Environment.GetEnvironmentVariable("PATH");
        var nugetUsername = Environment.GetEnvironmentVariable("NUGET_USERNAME");
        var nugetToken = Environment.GetEnvironmentVariable("NUGET_TOKEN");
        startInfo.Environment.Clear();
        if (path is not null)
        {
            startInfo.Environment["PATH"] = path;
        }

        startInfo.Environment["HOME"] = "/tmp";
        startInfo.Environment["GIT_AUTHOR_NAME"] = "AI Coding Agent";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "AI Coding Agent";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "ai-coding-agent@users.noreply.github.com";
        startInfo.Environment["CLAUDE_CONFIG_DIR"] =
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? "/claude-config";
        if (nugetUsername is not null)
        {
            startInfo.Environment["NUGET_USERNAME"] = nugetUsername;
        }

        if (nugetToken is not null)
        {
            startInfo.Environment["NUGET_TOKEN"] = nugetToken;
        }

        ClaudeRunner.ApplyEnvironment(startInfo.Environment, anthropicCredential);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Claude Code CLI.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (prompt is null)
            {
                process.StandardInput.Close();
            }
            else
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
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

        return (process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
    }

    private static string Limit(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[..maximumLength] + "...";

    internal sealed record RunnerRequest(
        string WorkspacePath = "",
        string Prompt = "",
        string Model = "",
        bool AllowChanges = false,
        string AnthropicCredential = "");

    internal sealed record RunnerResponse(string Output);
}
