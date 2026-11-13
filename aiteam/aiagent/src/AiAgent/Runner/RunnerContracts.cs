namespace AiAgent.Runner;

public static class RunnerModes
{
    public const string Plan = "plan";
    public const string Implement = "implement";
    public const string Review = "review";

    public static bool IsValid(string? mode) => mode is Plan or Implement or Review;
}

/// <summary>Body of <c>POST /run</c>.</summary>
public sealed record RunnerRequest(
    string Provider = "claude",
    string Model = "",
    string Mode = RunnerModes.Plan,
    string Workspace = "",
    string Prompt = "",
    string? BuildCommand = null,
    string? TestCommand = null,
    string? CopilotToken = null);

public sealed record RunnerUsage(long InputTokens, long OutputTokens);

public sealed record RunnerToolCall(string Name, int Count);

/// <summary>Response of <c>POST /run</c>.</summary>
public sealed record RunnerResponse(
    int ExitCode,
    string Output,
    RunnerUsage? Usage,
    IReadOnlyList<RunnerToolCall>? ToolCalls,
    long LlmDurationMs);

/// <summary>Thrown by the client when the runner has no free job slot; retry in the next cycle.</summary>
public sealed class RunnerBusyException(string message) : Exception(message);
