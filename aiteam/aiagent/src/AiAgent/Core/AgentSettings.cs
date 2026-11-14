using DFCommonLib.Config;

namespace AiAgent.Core;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Maximum concurrent runner jobs (runner reads AIAGENT_RUNNER_MAX_JOBS; default 3).</summary>
    public int MaxConcurrentRunnerJobs { get; set; } = 3;

    /// <summary>Max minutes one Review runner call (LLM run, build and test for one comment) may take.</summary>
    public int RunnerTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Agents one aiagent container runs at a time (default 1: one agent per container). Containers share the
    /// enabled agents through a database lock per agent, so scale the service to run more agents.
    /// </summary>
    public int AgentsPerInstance { get; set; } = 1;
}
