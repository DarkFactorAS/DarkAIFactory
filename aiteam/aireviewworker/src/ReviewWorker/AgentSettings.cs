using DFCommonLib.Config;

namespace ReviewWorker;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
    public string ReviewerLogin { get; set; } = string.Empty;
    public int MaxAttemptsPerPullRequest { get; set; } = 3;
}
