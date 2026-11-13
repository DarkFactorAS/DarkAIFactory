using DFCommonLib.Config;

namespace AiAgent.Core;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Maximum concurrent runner jobs (runner reads AIAGENT_RUNNER_MAX_JOBS; default 2).</summary>
    public int MaxConcurrentRunnerJobs { get; set; } = 2;
}
