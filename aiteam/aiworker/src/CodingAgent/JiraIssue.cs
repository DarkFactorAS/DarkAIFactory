using AiTeam.Configuration;

namespace CodingAgent;

internal sealed record JiraIssue(string Key, string Summary, string Description, string StatusName = "")
{
    public string UpdatedAt { get; init; } = string.Empty;
    public IReadOnlyList<string> Labels { get; init; } = [];
    public IReadOnlyList<JiraBlocker> Blockers { get; init; } = [];
    public string? ParentKey { get; init; }
    public string IssueTypeName { get; init; } = string.Empty;

    private bool? _isSubTask;

    /// <summary>True when Jira flags the issue type as a sub-task; falls back to the 'Sub-Task' type name when unset.</summary>
    public bool IsSubTask
    {
        get => _isSubTask ?? string.Equals(IssueTypeName, "Sub-Task", StringComparison.OrdinalIgnoreCase);
        init => _isSubTask = value;
    }
}
