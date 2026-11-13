using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace AiAgent.Runner;

/// <summary>Result of a CLI process.</summary>
internal sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Starts a CLI process; replaceable in tests.</summary>
internal delegate Task<ProcessResult> ProcessExecutor(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IDictionary<string, string?> environment,
    string stdin,
    CancellationToken cancellationToken);

/// <summary>Sandbox runner started with <c>--runner-server</c>.</summary>
internal static class RunnerServer
{
    public static WebApplication Build(
        string url = "http://0.0.0.0:8080",
        string? workspaceRoot = null,
        int? maxConcurrentJobs = null,
        ProcessExecutor? executor = null)
    {
        var root = workspaceRoot ?? RunnerPolicy.WorkspaceRoot;
        var slots = new SemaphoreSlim(maxConcurrentJobs ?? RunnerPolicy.MaxConcurrentJobs());
        executor ??= RunProcessAsync;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.MapGet("/health", () => Results.Ok());
        app.MapPost("/run", async (RunnerRequest request, CancellationToken cancellationToken) =>
        {
            string workspace;
            try
            {
                if (request.Provider is not ("claude" or "copilot"))
                {
                    return Results.BadRequest("Unknown provider.");
                }

                if (!RunnerModes.IsValid(request.Mode))
                {
                    return Results.BadRequest("Mode must be plan, implement or review.");
                }

                workspace = RunnerPolicy.ResolveWorkspace(request.Workspace, root);
                if (!Directory.Exists(workspace))
                {
                    return Results.BadRequest("The workspace does not exist.");
                }
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(exception.Message);
            }

            // Extra jobs are not queued: the orchestrator retries in its next cycle.
            if (!await slots.WaitAsync(0, cancellationToken))
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            try
            {
                var environment = new Dictionary<string, string?>();
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    environment[(string)entry.Key] = entry.Value as string;
                }

                environment["HOME"] = "/tmp";
                environment["GIT_AUTHOR_NAME"] = environment["GIT_COMMITTER_NAME"] = "AI Coding Agent";
                environment["GIT_AUTHOR_EMAIL"] = environment["GIT_COMMITTER_EMAIL"] =
                    "ai-coding-agent@users.noreply.github.com";
                RunnerPolicy.ApplyEnvironment(environment, request.Provider, request.CopilotToken);
                var claude = request.Provider == "claude";
                var arguments = claude
                    ? RunnerPolicy.BuildClaudeArguments(request.Model, request.Mode)
                    : RunnerPolicy.BuildCopilotArguments(request.Model, request.Mode);
                var stopwatch = Stopwatch.StartNew();
                var result = await executor(
                    claude ? "claude" : "copilot",
                    arguments,
                    workspace,
                    environment,
                    RunnerPolicy.ComposePrompt(request),
                    cancellationToken);
                var elapsed = stopwatch.ElapsedMilliseconds;
                if (!claude)
                {
                    return Results.Json(new RunnerResponse(
                        result.ExitCode, Combine(result), null, null, elapsed));
                }

                var (output, usage, duration) = RunnerPolicy.ParseClaudeJson(result.Output, elapsed);
                return Results.Json(new RunnerResponse(
                    result.ExitCode,
                    result.ExitCode == 0 ? output : Combine(result),
                    usage,
                    null,
                    duration));
            }
            finally
            {
                slots.Release();
            }
        });
        return app;
    }

    public static Task RunAsync() => Build().RunAsync();

    private static string Combine(ProcessResult result) =>
        result.ExitCode == 0 || result.Error.Length == 0 ? result.Output : $"{result.Output}\n{result.Error}".Trim();

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IDictionary<string, string?> environment,
        string stdin,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
            process.StandardInput.Close();
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

        return new ProcessResult(process.ExitCode, (await outputTask).Trim(), (await errorTask).Trim());
    }
}
