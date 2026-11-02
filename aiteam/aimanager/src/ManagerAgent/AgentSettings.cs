using DFCommonLib.Config;

namespace ManagerAgent;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
}
