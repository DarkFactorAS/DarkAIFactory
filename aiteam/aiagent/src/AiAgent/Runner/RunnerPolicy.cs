using System.Text.Json;

namespace AiAgent.Runner;

/// <summary>Workspace, permission and environment rules enforced by the runner.</summary>
internal static class RunnerPolicy
{
    public const string DefaultWorkspaceRoot = "/workspaces";
    public const int DefaultMaxConcurrentJobs = 3;

    private static readonly string[] ReadOnlyTools = ["Read", "Glob", "Grep"];
    private static readonly string[] MutatingTools = ["Bash", "Edit", "MultiEdit", "Write", "NotebookEdit"];
    // The orchestrator pushes and opens pull requests itself; the model must never do so.
    private static readonly string[] AlwaysDeniedTools = ["Bash(git push:*)", "Bash(gh:*)"];

    public static string WorkspaceRoot =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("AIAGENT_WORKSPACE_ROOT") ?? DefaultWorkspaceRoot);

    public static int MaxConcurrentJobs()
    {
        var value = Environment.GetEnvironmentVariable("AIAGENT_RUNNER_MAX_JOBS");
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : DefaultMaxConcurrentJobs;
    }

    /// <summary>
    /// Returns the workspace path with every symlink resolved; throws unless it is strictly below the
    /// (also symlink-resolved) root, so a link cannot redirect the CLI outside the workspace root.
    /// </summary>
    public static string ResolveWorkspace(string workspace, string root)
    {
        if (string.IsNullOrWhiteSpace(workspace) || workspace.Contains('\0'))
        {
            throw new ArgumentException("The workspace is required.");
        }

        var fullRoot = ResolveRealPath(root).TrimEnd('/');
        var full = ResolveRealPath(workspace).TrimEnd('/');
        if (!full.StartsWith(fullRoot + "/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The workspace must be located under {fullRoot}.");
        }

        return full;
    }

    /// <summary>Resolves symlinks in every component of <paramref name="path"/>; missing components are kept as-is.</summary>
    private static string ResolveRealPath(string path)
    {
        const int MaxLinkHops = 40;
        var remaining = new Queue<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
        var current = "/";
        var hops = 0;
        while (remaining.Count > 0)
        {
            var next = Path.Combine(current, remaining.Dequeue());
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                throw new ArgumentException("The workspace path contains too many symbolic links.");
            }

            // Restart from the link target followed by the components not yet visited.
            var resolved = Path.GetFullPath(target, current);
            remaining = new Queue<string>(
                resolved.Split('/', StringSplitOptions.RemoveEmptyEntries).Concat(remaining));
            current = "/";
        }

        return current;
    }

    public static IReadOnlyList<string> BuildClaudeArguments(string model, string mode)
    {
        var arguments = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        if (model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        arguments.Add("--permission-mode");
        if (mode == RunnerModes.Plan)
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
        else
        {
            // Headless inside the sandbox; deny rules still take precedence.
            arguments.Add("bypassPermissions");
        }

        foreach (var tool in AlwaysDeniedTools)
        {
            arguments.Add("--disallowedTools");
            arguments.Add(tool);
        }

        return arguments;
    }

    public static IReadOnlyList<string> BuildCopilotArguments(string model, string mode)
    {
        var arguments = new List<string>();
        if (model.Length > 0)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        arguments.Add("--silent");
        arguments.Add("--trust-folder");
        arguments.Add("--secret-env-vars=GH_TOKEN,GITHUB_TOKEN");
        arguments.Add("--allow-all-tools");
        arguments.Add("--deny-tool=shell(git push)");
        arguments.Add("--deny-tool=shell(gh)");
        if (mode == RunnerModes.Plan)
        {
            arguments.Add("--deny-tool=shell");
            arguments.Add("--deny-tool=write");
        }

        return arguments;
    }

    /// <summary>Strips GitHub/Jira credentials; read-only NuGet credentials are kept.</summary>
    public static void ApplyEnvironment(IDictionary<string, string?> environment, string provider, string? copilotToken)
    {
        environment["NO_COLOR"] = "1";
        environment["TERM"] = "dumb";
        environment["DISABLE_AUTOUPDATER"] = "1";
        environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        foreach (var name in environment.Keys.Where(IsSecretName).ToArray())
        {
            environment.Remove(name);
        }

        if (provider == "copilot" && !string.IsNullOrEmpty(copilotToken))
        {
            environment["GH_TOKEN"] = copilotToken;
        }
    }

    private static bool IsSecretName(string name) =>
        name.StartsWith("JIRA_", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("COPILOT_GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("GITHUB_REPOSITORY_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ANTHROPIC_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CLAUDE_CODE_OAUTH_TOKEN", StringComparison.OrdinalIgnoreCase);

    public static string ComposePrompt(RunnerRequest request)
    {
        var prompt = request.Prompt;
        if (request.Mode != RunnerModes.Plan)
        {
            prompt += "\n\nAfter you finish, the runner runs `dotnet build` for every project that contains a file you " +
                "changed and `dotnet test` for every test project that references those projects. The change is " +
                "rejected if any of them fails.";
        }

        return prompt;
    }

    /// <summary>
    /// Extracts result text, usage and duration from <c>claude -p --output-format stream-json --verbose</c>
    /// (one JSON event per line). Tool calls are the <c>tool_use</c> blocks of assistant events; tokens, cost, turns
    /// and duration come from the final <c>result</c> event. Malformed lines are skipped; without a result event
    /// the raw output is returned with no usage.
    /// </summary>
    public static (string Output, RunnerUsage? Usage, long DurationMs) ParseClaudeStreamJson(string stdout, long fallbackMs)
    {
        var toolCalls = 0;
        JsonElement? resultEvent = null;
        using var keepAlive = new CompositeDisposable();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            keepAlive.Add(document);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (type.GetString())
            {
                case "assistant":
                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        toolCalls += content.EnumerateArray().Count(block =>
                            block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var blockType) &&
                            blockType.ValueKind == JsonValueKind.String && blockType.GetString() == "tool_use");
                    }

                    break;
                case "result":
                    resultEvent = root;
                    break;
            }
        }

        if (resultEvent is not { } result)
        {
            return (stdout, null, fallbackMs);
        }

        var output = result.TryGetProperty("result", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? string.Empty
            : stdout;
        RunnerUsage? usage = null;
        if (result.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new RunnerUsage(
                Long(u, "input_tokens") ?? 0,
                Long(u, "output_tokens") ?? 0,
                Long(u, "cache_read_input_tokens"),
                Long(u, "cache_creation_input_tokens"),
                result.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number &&
                    cost.TryGetDecimal(out var dollars) ? dollars : null,
                toolCalls,
                Long(result, "num_turns") is { } turns ? (int)turns : null);
        }

        return (output, usage, Long(result, "duration_ms") ?? fallbackMs);

        static long? Long(JsonElement element, string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n
                : null;
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly List<IDisposable> _items = [];
        public void Add(IDisposable item) => _items.Add(item);
        public void Dispose() => _items.ForEach(item => item.Dispose());
    }
}
