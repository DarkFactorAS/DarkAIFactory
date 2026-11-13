using AiAgent.Core;
using static AiAgent.Core.JiraQuery;

namespace AiAgent.Capabilities.Planning;

/// <summary>Per-project settings of the Planning capability (counterpart of the manager AgentConfiguration).</summary>
internal sealed record PlanningConfiguration(
    Uri GitHubRepositoryUrl,
    string JiraJql,
    string PickupLabel,
    string AnalyzedLabel,
    string WorkerPickupLabel,
    string FailureLabel,
    string ClarificationLabel,
    string ApprovalLabel,
    string ApprovedLabel)
{
    public const string DefaultClarificationLabel = "aiagent-needs-clarification";
    public const string DefaultApprovalLabel = "aiagent-awaiting-approval";
    public const string DefaultApprovedLabel = "aiagent-approved";

    public string AnalysisOnlyStatus { get; init; } = "Code Review";
    public string DoneStatus { get; init; } = "Done";
    public string JiraStatusName { get; init; } = "Selected for Development";
    public bool AutoApproveSmallChanges { get; init; } = true;

    /// <summary>
    /// Labels: <c>PlannedLabel</c> marks an issue as planned, <c>AnalyzedLabel</c> hands it to the Builder,
    /// <c>PickupLabel</c> is set by the Builder (so Planning leaves the issue alone) and <c>FailureLabel</c>
    /// pauses it. <c>IgnoredLabel</c> (set by humans) excludes an issue from the query.
    /// </summary>
    public static PlanningConfiguration Load(
        IntegrationSettings settings, ManagerProject project, WorkflowLabels labels)
    {
        var repositoryUrl = project.TargetRepositoryUrl.Trim();
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) ||
            repositoryUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(repositoryUri.UserInfo) ||
            repositoryUri.AbsolutePath.Trim('/').Split('/').Length != 2)
        {
            throw new InvalidOperationException(
                $"Manager project {project.Id}: target_repository_url must be an https GitHub repository URL in the form https://github.com/owner/repository.");
        }

        var jiraProjectKey = project.JiraProjectKey.Trim();
        if (jiraProjectKey.Length == 0)
        {
            throw new InvalidOperationException($"Manager project {project.Id}: jira_project_key must not be empty.");
        }

        var statusName = Get(settings.Jira.JiraStatusName, "Selected for Development");
        var planned = labels.Get("PlannedLabel");
        var analyzed = labels.Get("AnalyzedLabel");
        var picked = labels.Get("PickupLabel");
        var failed = labels.Get("FailureLabel");
        var ignored = labels.GetOrDefault("IgnoredLabel", "aiagent-ignored");
        var clarification = labels.GetOrDefault("ClarificationLabel", DefaultClarificationLabel);
        var approval = labels.GetOrDefault("ApprovalLabel", DefaultApprovalLabel);
        var approved = labels.GetOrDefault("ApprovedLabel", DefaultApprovedLabel);
        var all = new[] { planned, analyzed, picked, failed, ignored, clarification, approval, approved };
        if (all.Any(label => label.Length == 0 || label.Any(char.IsWhiteSpace)) ||
            all.Distinct(StringComparer.Ordinal).Count() != all.Length)
        {
            throw new InvalidOperationException(
                "The aiagent workflow labels must be non-empty, without whitespace, and all differ.");
        }

        if (!bool.TryParse(Get(settings.Jira.JiraAutoApproveSmallChanges, "true"), out var autoApprove))
        {
            throw new InvalidOperationException("Jira.JiraAutoApproveSmallChanges must be true or false.");
        }

        var jql = string.IsNullOrWhiteSpace(settings.Jira.JiraJql)
            ? $"project = \"{EscapeJql(jiraProjectKey)}\" AND status = \"{EscapeJql(statusName)}\" " +
              "AND issuetype != Epic ORDER BY priority DESC, created ASC"
            : ScopeToProject(settings.Jira.JiraJql.Trim(), jiraProjectKey);
        foreach (var excluded in new[] { failed, picked, ignored })
        {
            jql = AddPickupLabelExclusion(jql, excluded);
        }

        return new PlanningConfiguration(
            repositoryUri, jql, planned, analyzed, picked, failed, clarification, approval, approved)
        {
            AnalysisOnlyStatus = Get(settings.Jira.JiraAnalysisOnlyStatus, "Code Review"),
            DoneStatus = Get(settings.Jira.JiraDoneStatus, "Done"),
            JiraStatusName = statusName,
            AutoApproveSmallChanges = autoApprove
        };
    }

    private static string Get(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}
