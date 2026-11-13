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
    public string AnalyzedLabel { get; init; } = string.Empty;
    public string WorkerPickupLabel { get; init; } = string.Empty;
    public string ClarificationLabel { get; init; } = string.Empty;
    public string ApprovalLabel { get; init; } = string.Empty;
    public string ApprovedLabel { get; init; } = string.Empty;
    public string AnalysisOnlyStatus { get; init; } = "Code Review";
    public string DoneStatus { get; init; } = "Done";
    public string SubtaskIssueType { get; init; } = "Sub-task";
    public string TaskIssueType { get; init; } = "Task";
    public string JiraStatusName { get; init; } = "Selected for Development";
    public bool AutoApproveSmallChanges { get; init; } = true;

    public static AgentConfiguration Load(
        IntegrationSettings settings, ManagerProject project, WorkflowLabels labels, WorkflowLabels workerLabels)
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
        var pickupLabel = labels.Get("PickupLabel");
        var failureLabel = labels.Get("FailureLabel");
        var analyzedLabel = labels.Get("AnalyzedLabel");
        ValidateLabel(pickupLabel, "PickupLabel");
        ValidateLabel(failureLabel, "FailureLabel");
        ValidateLabel(analyzedLabel, "AnalyzedLabel");
        var clarificationLabel = labels.Get("ClarificationLabel");
        var approvalLabel = labels.Get("ApprovalLabel");
        var approvedLabel = labels.Get("ApprovedLabel");
        ValidateLabel(clarificationLabel, "ClarificationLabel");
        ValidateLabel(approvalLabel, "ApprovalLabel");
        ValidateLabel(approvedLabel, "ApprovedLabel");
        var autoApproveText = Get(settings.Jira.JiraAutoApproveSmallChanges, "true");
        if (!bool.TryParse(autoApproveText, out var autoApproveSmallChanges))
        {
            throw new InvalidOperationException("Jira.JiraAutoApproveSmallChanges must be true or false.");
        }
        var workerPickupLabel = workerLabels.Get("PickupLabel");
        ValidateLabel(workerPickupLabel, "aiworker PickupLabel");
        if (new[]
            {
                clarificationLabel, approvalLabel, approvedLabel, pickupLabel, analyzedLabel, failureLabel, workerPickupLabel
            }.Distinct(StringComparer.Ordinal).Count() != 7)
        {
            throw new InvalidOperationException(
                "Clarification, approval, approved, manager pickup, analyzed, failure and worker pickup labels must all differ.");
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
            JiraStatusName = jiraStatusName,
            AnalysisOnlyStatus = Get(settings.Jira.JiraAnalysisOnlyStatus, "Code Review"),
            DoneStatus = Get(settings.Jira.JiraDoneStatus, "Done"),
            SubtaskIssueType = Get(settings.Jira.JiraSubtaskIssueType, "Sub-task"),
            TaskIssueType = Get(settings.Jira.JiraTaskIssueType, "Task"),
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
