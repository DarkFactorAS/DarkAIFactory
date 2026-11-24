using AiAgent.Core;
using AiAgent.Runner;

namespace AiAgent.Metrics;

public enum AgentRunOutcome
{
    Succeeded,
    Failed
}

/// <summary>Values written when the run starts.</summary>
public sealed record AgentRunStart(
    long AgentId, AgentCapabilityKind Capability, string? IssueKey, string Provider, string? Model, DateTime StartedAt);

/// <summary>Values written when the run ends. Usage columns stay null when the provider reported nothing.</summary>
public sealed record AgentRunFinish(
    AgentRunOutcome Outcome,
    string? FailureReason,
    DateTime FinishedAt,
    long? LlmDurationMs,
    long? InputTokens,
    long? OutputTokens,
    long? CacheReadTokens,
    long? CacheWriteTokens,
    decimal? CostUsd,
    int? ToolCalls,
    int? NumTurns);

/// <summary>One unit of work being recorded; the handle passed to the work delegate.</summary>
public interface IAgentRun
{
    /// <summary>Adds the usage of one runner (LLM) call; call it once per call to accumulate.</summary>
    void AddUsage(RunnerResponse response);

    /// <summary>Starts a run recorded with deferred start: inserts the row now. No-op when already started.</summary>
    void Begin();

    /// <summary>Marks the unit of work as succeeded. A failure that was already recorded is not overridden.</summary>
    void Succeed();

    /// <summary>Marks the unit of work as failed with a reason.</summary>
    void Fail(string reason);

    /// <summary>Removes the row: the work was deferred and nothing was done (for example nothing needed doing).</summary>
    void Discard();
}

/// <summary>Records one agent_runs row per unit of work.</summary>
public interface IAgentRunRecorder
{
    /// <summary>
    /// Inserts the row, runs <paramref name="work"/> and finishes the row in a finally block. An exception from the
    /// work is recorded as Failed and rethrown (a busy runner discards the row instead). A work that sets no
    /// outcome counts as Failed. A recorder failure is logged and never breaks the work. With
    /// <paramref name="deferStart"/> the row is only inserted when the work calls <see cref="IAgentRun.Begin"/>,
    /// so polling that finds nothing to do writes no row.
    /// </summary>
    Task RecordAsync(
        AgentContext context, AgentCapabilityKind capability, string? issueKey, Func<IAgentRun, Task> work,
        bool deferStart = false);
}

/// <summary>Records one run; bound to an agent and capability by <see cref="AgentRunRecorderExtensions.Bind"/>.</summary>
public delegate Task RecordRun(string? issueKey, Func<IAgentRun, Task> work, bool deferStart = false);

public static class AgentRunRecorderExtensions
{
    public static RecordRun Bind(this IAgentRunRecorder recorder, AgentContext context, AgentCapabilityKind capability) =>
        (issueKey, work, deferStart) => recorder.RecordAsync(context, capability, issueKey, work, deferStart);
}

/// <summary>Recorder that records nothing; used when no recorder is configured.</summary>
public sealed class NullAgentRunRecorder : IAgentRunRecorder
{
    public static readonly NullAgentRunRecorder Instance = new();

    public static RecordRun Record { get; } = (_, work, _) => work(NullAgentRun.Instance);

    public Task RecordAsync(
        AgentContext context, AgentCapabilityKind capability, string? issueKey, Func<IAgentRun, Task> work,
        bool deferStart = false) =>
        work(NullAgentRun.Instance);
}

internal sealed class NullAgentRun : IAgentRun
{
    public static readonly NullAgentRun Instance = new();
    public void AddUsage(RunnerResponse response) { }
    public void Begin() { }
    public void Succeed() { }
    public void Fail(string reason) { }
    public void Discard() { }
}
