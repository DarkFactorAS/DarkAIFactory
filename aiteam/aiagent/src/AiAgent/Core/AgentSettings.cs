using DFCommonLib.Config;

namespace AiAgent.Core;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
}
