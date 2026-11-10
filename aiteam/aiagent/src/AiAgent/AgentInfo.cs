using Microsoft.Extensions.Options;

namespace AiAgent;

public sealed class AgentOptions
{
    public string Name { get; set; } = "aiagent";
}

public interface IAgentInfo
{
    string Name { get; }
}

public sealed class AgentInfo(IOptions<AgentOptions> options) : IAgentInfo
{
    public string Name => options.Value.Name;
}
