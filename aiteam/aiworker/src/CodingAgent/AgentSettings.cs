using DFCommonLib.Config;

namespace CodingAgent;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
}
