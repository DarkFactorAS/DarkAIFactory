namespace AiTeam.Configuration;

public sealed record AgentCapability(string Capability, string LanguageModel, bool Enabled);

public sealed record AgentDefinition(
    long Id, string Name, string Role, string BehaviorPrompt, string LanguageModel, bool Enabled,
    IReadOnlyList<AgentCapability> Capabilities);

public enum AgentRunStatus
{
    Success,
    Failed
}

public sealed record AgentTaskMetric(
    long AgentId, string LanguageModel, long TokensIn, long? TokensOut, int ToolCalls, long DurationMs,
    AgentRunStatus Status, long? AgentTaskId = null, string? JiraKey = null);
