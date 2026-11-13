using AiAgent.Core;

namespace AiAgent.Capabilities.Builder;

internal sealed record BuilderIssue(string Key, string Summary, string Description, string StatusName = "")
{
    public string UpdatedAt { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<JiraBlocker> Blockers { get; init; } = [];
    public string? ParentKey { get; init; }
    public string IssueTypeName { get; init; } = string.Empty;
}
