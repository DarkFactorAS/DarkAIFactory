namespace AiAgent.Core;

public sealed record AgentDefinition(
    long Id,
    string Name,
    string Role,
    string Behavior,
    string LlmProvider,
    string? LlmModel,
    bool Enabled,
    bool Deleted);

public sealed record AgentPullRequestRecord(
    long AgentId, string JiraKey, string RepositoryUrl, long PullRequestNumber, string PullRequestUrl);
