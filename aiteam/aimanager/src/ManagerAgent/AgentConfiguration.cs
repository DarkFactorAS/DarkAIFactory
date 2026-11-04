using AiTeam.Configuration;
using static AiTeam.Configuration.JiraQuery;

namespace ManagerAgent;

internal sealed record AgentConfiguration(
    Uri JiraBaseUrl,
    string JiraEmail,
    string JiraApiToken,
    string AgentGitHubToken,
    string GitHubRepositoryToken,
    Uri GitHubRepositoryUrl,
    string JiraJql,
    string PickupLabel,
    string FailureLabel,
    string InProgressStatus,
    string InReviewStatus,
    TimeSpan PollInterval)
{
    public string AnalyzedLabel { get; init; } = "ai-analyzed";
    public string WorkerPickupLabel { get; init; } = "ai-agent-picked";
    public string ClarificationLabel { get; init; } = "ai-needs-clarification";
    public string ApprovalLabel { get; init; } = "ai-awaiting-approval";
    public string ApprovedLabel { get; init; } = "ai-approved";
    public string AnalysisOnlyStatus { get; init; } = "Code Review";
    public string DoneStatus { get; init; } = "Done";
    public string SubtaskIssueType { get; init; } = "Sub-task";
    public bool AutoApproveSmallChanges { get; init; } = true;

    public static AgentConfiguration Load(IntegrationSettings settings, ManagerProject project, string workerPickupLabel = "ai-agent-picked")
    {
        var jiraBaseUrl = Required(settings.Jira.JiraBaseUrl, "Jira.JiraBaseUrl");
        if (!Uri.TryCreate(jiraBaseUrl, UriKind.Absolute, out var jiraUri) ||
            jiraUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(jiraUri.UserInfo))
        {
            throw new InvalidOperationException("JiraBaseUrl must be an https URL without embedded credentials.");
        }

        var gitHubRepositoryUrl = project.TargetRepositoryUrl.Trim();
        if (!Uri.TryCreate(gitHubRepositoryUrl, UriKind.Absolute, out var gitHubRepositoryUri) ||
            gitHubRepositoryUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(gitHubRepositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(gitHubRepositoryUri.UserInfo) ||
            !string.IsNullOrEmpty(gitHubRepositoryUri.Query) ||
            !string.IsNullOrEmpty(gitHubRepositoryUri.Fragment) ||
            gitHubRepositoryUri.AbsolutePath.Trim('/').Split('/').Length != 2)
        {
            throw new InvalidOperationException(
                $"Manager project {project.Id}: target_repository_url must be an https GitHub repository URL in the form https://github.com/owner/repository.");
        }

        var jiraProjectKey = project.JiraProjectKey.Trim();
        if (jiraProjectKey.Length == 0)
        {
            throw new InvalidOperationException($"Manager project {project.Id}: jira_project_key must not be empty.");
        }
        var jiraStatusName = Get(settings.Jira.JiraStatusName, "Selected for Development");
        var pickupLabel = Get(settings.Jira.JiraPickupLabel, "ai-manager-planned");
        var failureLabel = Get(settings.Jira.JiraFailureLabel, "ai-agent-failed");
        var analyzedLabel = Get(settings.Jira.JiraAnalyzedLabel, "ai-analyzed");
        ValidateLabel(pickupLabel, "Jira.JiraPickupLabel");
        ValidateLabel(failureLabel, "Jira.JiraFailureLabel");
        ValidateLabel(analyzedLabel, "Jira.JiraAnalyzedLabel");
        var clarificationLabel = Get(settings.Jira.JiraClarificationLabel, "ai-needs-clarification");
        var approvalLabel = Get(settings.Jira.JiraApprovalLabel, "ai-awaiting-approval");
        var approvedLabel = Get(settings.Jira.JiraApprovedLabel, "ai-approved");
        ValidateLabel(clarificationLabel, "Jira.JiraClarificationLabel");
        ValidateLabel(approvalLabel, "Jira.JiraApprovalLabel");
        ValidateLabel(approvedLabel, "Jira.JiraApprovedLabel");
        if (new[] { clarificationLabel, approvalLabel, approvedLabel, pickupLabel, analyzedLabel, failureLabel }
                .Distinct(StringComparer.Ordinal).Count() != 6)
        {
            throw new InvalidOperationException("Clarification, approval, approved, pickup, analyzed and failure labels must all differ.");
        }
        var autoApproveText = Get(settings.Jira.JiraAutoApproveSmallChanges, "true");
        if (!bool.TryParse(autoApproveText, out var autoApproveSmallChanges))
        {
            throw new InvalidOperationException("Jira.JiraAutoApproveSmallChanges must be true or false.");
        }
        workerPickupLabel = Get(workerPickupLabel, "ai-agent-picked");
        ValidateLabel(workerPickupLabel, "aiworker Jira.JiraPickupLabel");
        if (workerPickupLabel == pickupLabel || workerPickupLabel == analyzedLabel)
        {
            throw new InvalidOperationException("Worker pickup label must differ from manager completion and analyzed labels.");
        }

        var defaultJql =
            $"project = \"{EscapeJql(jiraProjectKey)}\" " +
            $"AND status = \"{EscapeJql(jiraStatusName)}\" " +
            $"AND issuetype != Epic " +
            "ORDER BY priority DESC, created ASC";

        var agentGitHubToken = Required(settings.GitHub.AgentGitHubToken, "GitHub.AgentGitHubToken");

        var jqlOverride = settings.Jira.JiraJql;
        var jql = string.IsNullOrWhiteSpace(jqlOverride)
            ? defaultJql
            : ScopeToProject(jqlOverride.Trim(), jiraProjectKey);

        return new AgentConfiguration(
            jiraUri,
            Required(settings.Jira.JiraEmail, "Jira.JiraEmail"),
            Required(settings.Jira.JiraApiToken, "Jira.JiraApiToken"),
            agentGitHubToken,
            Required(settings.GitHub.GitHubRepositoryToken, "GitHub.GitHubRepositoryToken"),
            gitHubRepositoryUri,
            AddPickupLabelExclusion(AddPickupLabelExclusion(jql, failureLabel), workerPickupLabel),
            pickupLabel,
            failureLabel,
            Get(settings.Jira.JiraInProgressStatus, "In Progress"),
            Get(settings.Jira.JiraInReviewStatus, "In Review"),
            LoadPollInterval(settings.PollIntervalSeconds))
        {
            AnalyzedLabel = analyzedLabel,
            WorkerPickupLabel = workerPickupLabel,
            ClarificationLabel = clarificationLabel,
            ApprovalLabel = approvalLabel,
            ApprovedLabel = approvedLabel,
            AnalysisOnlyStatus = Get(settings.Jira.JiraAnalysisOnlyStatus, "Code Review"),
            DoneStatus = Get(settings.Jira.JiraDoneStatus, "Done"),
            SubtaskIssueType = Get(settings.Jira.JiraSubtaskIssueType, "Sub-task"),
            AutoApproveSmallChanges = autoApproveSmallChanges
        };
    }

    public static TimeSpan LoadPollInterval(int pollSeconds)
    {
        if (pollSeconds < 5)
        {
            throw new InvalidOperationException("PollIntervalSeconds must be at least 5 seconds.");
        }

        return TimeSpan.FromSeconds(pollSeconds);
    }

    private static string Required(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required database setting {name} is not set.")
            : value.Trim();
    }

    private static string Get(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value.Trim();

    private static void ValidateLabel(string label, string variableName)
    {
        if (label.Length == 0 || label.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{variableName} must be a non-empty Jira label without whitespace.");
        }
    }
}
