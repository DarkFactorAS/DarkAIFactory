using AiAgent.Core;

namespace AiAgent.Capabilities.Jira;

public sealed record JiraIssue(string Key, string Summary, string Description, string StatusName = "")
{
    public string UpdatedAt { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    internal IReadOnlyList<JiraBlocker> Blockers { get; init; } = [];
    public string? ParentKey { get; init; }
    public string IssueTypeName { get; init; } = string.Empty;
    public string ProjectKey => Key.Contains('-', StringComparison.Ordinal) ? Key[..Key.LastIndexOf('-')] : Key;
}
