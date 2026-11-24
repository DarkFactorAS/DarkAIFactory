using AiAgent.Core;
using AiAgent.Runner;
using DFCommonLib.Logger;

namespace AiAgent.Metrics;

public sealed class AgentRunRecorder(IAgentRunRepository repository, Func<DateTime>? clock = null) : IAgentRunRecorder
{
    private const int MaxReasonLength = 2000;
    private static readonly AsyncLocal<ActiveRun?> Active = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Adds runner usage to the run of the current async flow, if any. Used by <see cref="RecordingRunnerClient"/>.</summary>
    public static void AddUsageToCurrent(RunnerResponse response) => Active.Value?.AddUsage(response);

    public async Task RecordAsync(
        AgentContext context, AgentCapabilityKind capability, string? issueKey, Func<IAgentRun, Task> work,
        bool deferStart = false)
    {
        var run = new ActiveRun(() => StartRow(context, capability, issueKey));
        if (!deferStart)
        {
            run.Begin();
        }

        var previous = Active.Value;
        Active.Value = run;
        try
        {
            await work(run);
        }
        catch (Exception exception)
        {
            run.FailFromException(exception);
            throw;
        }
        finally
        {
            Active.Value = previous;
            FinishRow(run);
        }
    }

    private long? StartRow(AgentContext context, AgentCapabilityKind capability, string? issueKey)
    {
        try
        {
            var agent = context.Agent;
            return repository.Insert(new AgentRunStart(
                agent.Id, capability, issueKey, agent.LlmProvider, agent.LlmModel, _clock()));
        }
        catch (Exception exception)
        {
            Log($"Could not record the start of a {capability} run: {exception.Message}");
            return null;
        }
    }

    private void FinishRow(ActiveRun run)
    {
        if (run.RowId is not { } id)
        {
            return;
        }

        try
        {
            if (run.Discarded)
            {
                repository.Delete(id);
                return;
            }

            repository.Finish(id, run.ToFinish(_clock()));
        }
        catch (Exception exception)
        {
            Log($"Could not record the end of run {id}: {exception.Message}");
        }
    }

    private static void Log(string message) => AgentLog.LogOutput(DFLogLevel.WARNING, "AgentRuns", message);

    private sealed class ActiveRun(Func<long?> insert) : IAgentRun
    {
        private readonly object _sync = new();
        private bool _begun;
        private AgentRunOutcome? _outcome;
        private string? _reason;
        private long? _llmMs, _input, _output, _cacheRead, _cacheWrite;
        private decimal? _cost;
        private int? _toolCalls, _turns;

        public long? RowId { get; private set; }
        public bool Discarded { get; private set; }

        public void Begin()
        {
            lock (_sync)
            {
                if (_begun)
                {
                    return;
                }

                _begun = true;
                RowId = insert();
            }
        }

        public void AddUsage(RunnerResponse response)
        {
            lock (_sync)
            {
                _llmMs = (_llmMs ?? 0) + response.LlmDurationMs;
                if (response.Usage is not { } usage)
                {
                    return;
                }

                _input = (_input ?? 0) + usage.InputTokens;
                _output = (_output ?? 0) + usage.OutputTokens;
                _cacheRead = Sum(_cacheRead, usage.CacheReadTokens);
                _cacheWrite = Sum(_cacheWrite, usage.CacheWriteTokens);
                _cost = _cost is null && usage.CostUsd is null ? null : (_cost ?? 0) + (usage.CostUsd ?? 0);
                _toolCalls = usage.ToolCalls is null ? _toolCalls : (_toolCalls ?? 0) + usage.ToolCalls;
                _turns = usage.NumTurns is null ? _turns : (_turns ?? 0) + usage.NumTurns;
            }
        }

        public void Succeed()
        {
            lock (_sync)
            {
                // A recorded failure is final and is never masked by a later success.
                if (_outcome != AgentRunOutcome.Failed)
                {
                    _outcome = AgentRunOutcome.Succeeded;
                }
            }
        }

        public void Fail(string reason)
        {
            lock (_sync)
            {
                if (_outcome != AgentRunOutcome.Failed)
                {
                    _outcome = AgentRunOutcome.Failed;
                    _reason = reason;
                }
            }
        }

        public void Discard()
        {
            lock (_sync)
            {
                Discarded = true;
            }
        }

        public void FailFromException(Exception exception)
        {
            lock (_sync)
            {
                // A busy runner is deferred work, not a failure.
                if (exception is RunnerBusyException)
                {
                    Discarded = true;
                    return;
                }

                if (_outcome != AgentRunOutcome.Failed)
                {
                    _outcome = AgentRunOutcome.Failed;
                    _reason = exception is OperationCanceledException
                        ? "Cancelled: " + exception.Message
                        : exception.Message;
                }
            }
        }

        public AgentRunFinish ToFinish(DateTime now)
        {
            lock (_sync)
            {
                var outcome = _outcome ?? AgentRunOutcome.Failed;
                var reason = outcome == AgentRunOutcome.Failed
                    ? _reason ?? "The work ended without reporting an outcome."
                    : null;
                if (reason is { Length: > MaxReasonLength })
                {
                    reason = reason[..MaxReasonLength];
                }

                return new AgentRunFinish(
                    outcome, reason, now, _llmMs, _input, _output, _cacheRead, _cacheWrite, _cost, _toolCalls, _turns);
            }
        }

        private static long? Sum(long? total, long? value) => value is null ? total : (total ?? 0) + value;
    }
}
