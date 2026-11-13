using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AiAgent.LanguageModel;

/// <summary>Runs the Claude Code CLI as <c>claude -p</c> with stream-json output.</summary>
public sealed class ClaudeLanguageModel(
    string? model = null,
    Func<ProcessStartInfo, CancellationToken, Task<ProcessResult>>? processRunner = null) : ILanguageModel
{
    public const string DefaultModel = "claude-sonnet-5-5";

    // Permission sets are intentionally hard-coded (copied from ClaudeRunner.cs).
    private static readonly string[] ReadOnlyTools = ["Read", "Glob", "Grep"];
    private static readonly string[] MutatingTools = ["Bash", "Edit", "MultiEdit", "Write", "NotebookEdit"];
    private static readonly string[] AlwaysDeniedTools = ["Bash(git push:*)", "Bash(gh:*)"];

    public string Provider => "claude";
    public string Model { get; } = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    public async Task<string> RunAsync(LanguageModelRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = "claude", WorkingDirectory = request.WorkspacePath };
        foreach (var argument in BuildArguments(Model, request.AllowChanges))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(request.Prompt);
        ApplyEnvironment(startInfo.Environment);
        var result = await (processRunner ?? CliProcess.RunAsync)(startInfo, cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            throw new InvalidOperationException(
                $"Claude CLI failed with exit code {result.ExitCode}." +
                (detail.Length == 0 ? string.Empty : $" Details: {CliProcess.Limit(detail, 2_000)}"));
        }

        var output = ParseStreamJson(result.StandardOutput);
        return output.Length > 0 ? output : throw new InvalidOperationException("Claude CLI returned no response.");
    }

    internal static IReadOnlyList<string> BuildArguments(string model, bool allowChanges)
    {
        var arguments = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        if (model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        arguments.Add("--permission-mode");
        if (allowChanges)
        {
            // Deny rules still take precedence over bypassPermissions.
            arguments.Add("bypassPermissions");
        }
        else
        {
            arguments.Add("default");
            foreach (var tool in ReadOnlyTools)
            {
                arguments.Add("--allowedTools");
                arguments.Add(tool);
            }

            foreach (var tool in MutatingTools)
            {
                arguments.Add("--disallowedTools");
                arguments.Add(tool);
            }
        }

        foreach (var tool in AlwaysDeniedTools)
        {
            arguments.Add("--disallowedTools");
            arguments.Add(tool);
        }

        return arguments;
    }

    internal static void ApplyEnvironment(IDictionary<string, string?> environment)
    {
        environment["NO_COLOR"] = "1";
        environment["TERM"] = "dumb";
        environment["DISABLE_AUTOUPDATER"] = "1";
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        CliProcess.RemoveSecrets(environment, "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN");
    }

    /// <summary>
    /// Extracts the final answer from stream-json lines: the <c>result</c> event if present,
    /// otherwise the concatenated assistant text blocks. Non-JSON lines are ignored.
    /// </summary>
    internal static string ParseStreamJson(string output)
    {
        var assistantText = new StringBuilder();
        string? result = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var type))
                {
                    continue;
                }

                switch (type.GetString())
                {
                    case "result":
                        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                        {
                            var message = root.TryGetProperty("result", out var err) ? err.GetString() : null;
                            throw new InvalidOperationException($"Claude CLI reported an error. {message}".Trim());
                        }

                        if (root.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            result = value.GetString();
                        }

                        break;
                    case "assistant":
                        if (root.TryGetProperty("message", out var message2) &&
                            message2.TryGetProperty("content", out var content) &&
                            content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                                    block.TryGetProperty("text", out var text))
                                {
                                    assistantText.Append(text.GetString());
                                }
                            }
                        }

                        break;
                }
            }
        }

        return (!string.IsNullOrWhiteSpace(result) ? result : assistantText.ToString()).Trim();
    }
}
