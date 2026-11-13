using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiAgent.Core;

/// <summary>An issue that blocks another issue ("is blocked by" link) and its current Jira status.</summary>
internal sealed record JiraBlocker(string Key, string StatusName);

/// <summary>Workflow rules shared by the manager and both workers.</summary>
internal static partial class JiraWorkflowRules
{
    /// <summary>Jira fields the agents need to apply the blocked-by and sub-task branching rules.</summary>
    public const string RelationFields = "parent,issuetype,issuelinks";

    /// <summary>
    /// Name of the integration branch of a parent task. Sub-tasks branch off it and their pull requests
    /// target it, so the parent's changes are assembled on one branch and reviewed as a whole.
    /// </summary>
    public static string ParentBranchName(string parentKey) =>
        $"ai-agent/{BranchUnsafe().Replace(parentKey.Trim(), "-")}";

    /// <summary>Returns the blockers that are not in the done status. Unknown statuses count as open.</summary>
    public static IReadOnlyList<JiraBlocker> OpenBlockers(IEnumerable<JiraBlocker> blockers, string doneStatus) =>
        blockers
            .Where(blocker => !string.Equals(blocker.StatusName, doneStatus, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static string DescribeBlockers(IEnumerable<JiraBlocker> blockers) =>
        string.Join(", ", blockers.Select(blocker =>
            blocker.StatusName.Length == 0 ? blocker.Key : $"{blocker.Key} ({blocker.StatusName})"));

    /// <summary>Reads the issues this issue "is blocked by" from the Jira "issuelinks" field.</summary>
    public static IReadOnlyList<JiraBlocker> ReadBlockers(JsonElement fields)
    {
        if (!fields.TryGetProperty("issuelinks", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var blockers = new List<JiraBlocker>();
        foreach (var link in links.EnumerateArray())
        {
            // Seen from the blocked issue, the blocker is the link's inward issue ("is blocked by").
            if (!link.TryGetProperty("inwardIssue", out var inward) || inward.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = link.TryGetProperty("type", out var typeElement) ? typeElement : default;
            if (!string.Equals(ReadString(type, "name"), "Blocks", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ReadString(type, "inward"), "is blocked by", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = ReadString(inward, "key");
            if (key.Length == 0)
            {
                continue;
            }

            var status = inward.TryGetProperty("fields", out var inwardFields) &&
                inwardFields.ValueKind == JsonValueKind.Object &&
                inwardFields.TryGetProperty("status", out var statusElement)
                    ? ReadString(statusElement, "name")
                    : string.Empty;
            blockers.Add(new JiraBlocker(key, status));
        }

        return blockers;
    }

    /// <summary>Returns the parent key when the issue is a sub-task, otherwise null (epics are not parents here).</summary>
    public static string? ReadSubtaskParentKey(JsonElement fields)
    {
        if (!fields.TryGetProperty("issuetype", out var issueType) ||
            issueType.ValueKind != JsonValueKind.Object ||
            !issueType.TryGetProperty("subtask", out var subtask) ||
            subtask.ValueKind != JsonValueKind.True ||
            !fields.TryGetProperty("parent", out var parent))
        {
            return null;
        }

        var key = ReadString(parent, "key");
        return key.Length == 0 ? null : key;
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    [GeneratedRegex(@"[^\w.-]", RegexOptions.CultureInvariant)]
    private static partial Regex BranchUnsafe();
}
