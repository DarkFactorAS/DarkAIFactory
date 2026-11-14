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
    /// Agents one aiagent container runs at a time (range 1-3, default 1). Separate from the container count
    /// (<c>AIAGENT_INSTANCES</c>). Overridden by the environment variable <c>AIAGENT_AGENTS_PER_INSTANCE</c>.
    /// Containers share the enabled agents through a database lock per agent.
    /// </summary>
    public int AgentsPerInstance { get; set; } = 1;

    /// <summary>Hard maximum for <see cref="AgentsPerInstance"/>.</summary>
    public const int MaxAgentsPerInstance = 3;
}
