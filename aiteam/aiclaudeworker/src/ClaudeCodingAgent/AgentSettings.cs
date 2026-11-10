using DFCommonLib.Config;

namespace ClaudeCodingAgent;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
}
