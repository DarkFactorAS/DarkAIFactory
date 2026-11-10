using AiTeam.Configuration;

namespace ManagerAgent;

internal sealed record JiraIssue(string Key, string Summary, string Description, string StatusName = "")
{
    public string UpdatedAt { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<JiraBlocker> Blockers { get; init; } = [];
    public string? ParentKey { get; init; }
    public string IssueTypeName { get; init; } = string.Empty;

    /// <summary>True when the Jira issue type is 'Sub-Task' (case-insensitive).</summary>
    public bool IsSubTask => string.Equals(IssueTypeName, "Sub-Task", StringComparison.OrdinalIgnoreCase);
}
