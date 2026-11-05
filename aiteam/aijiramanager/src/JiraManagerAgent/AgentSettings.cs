using DFCommonLib.Config;

namespace JiraManagerAgent;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
}
