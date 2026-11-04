using AiTeam.Configuration;
using static AiTeam.Configuration.JiraQuery;

namespace CodingAgent;

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
    TimeSpan PollInterval,
    string AssigneeAccountId = "")
{
    public string AnalyzedLabel { get; init; } = "ai-analyzed";
    public string JiraProjectKey { get; init; } = string.Empty;
    public long? ManagerProjectId { get; init; }
    public bool RequiresManagerPlan { get; init; } = true;
    public string AgentModel { get; init; } = string.Empty;

    public static AgentConfiguration Load(IntegrationSettings settings, ManagerProject? project = null)
    {
        var jiraBaseUrl = Required(settings.Jira.JiraBaseUrl, "Jira.JiraBaseUrl");
        if (!Uri.TryCreate(jiraBaseUrl, UriKind.Absolute, out var jiraUri) ||
            jiraUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(jiraUri.UserInfo))
        {
            throw new InvalidOperationException("JiraBaseUrl must be an https URL without embedded credentials.");
        }

        var gitHubRepositoryUrl = Required(project?.TargetRepositoryUrl ?? settings.GitHub.TargetRepositoryUrl,
            project is null ? "GitHub.TargetRepositoryUrl" : $"Manager project {project.Id}: target_repository_url");
        if (!Uri.TryCreate(gitHubRepositoryUrl, UriKind.Absolute, out var gitHubRepositoryUri) ||
            gitHubRepositoryUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(gitHubRepositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(gitHubRepositoryUri.UserInfo) ||
            !string.IsNullOrEmpty(gitHubRepositoryUri.Query) ||
            !string.IsNullOrEmpty(gitHubRepositoryUri.Fragment) ||
            gitHubRepositoryUri.AbsolutePath.Trim('/').Split('/').Length != 2)
        {
            throw new InvalidOperationException(
                "TargetRepositoryUrl must be an https GitHub repository URL in the form https://github.com/owner/repository.");
        }

        var jiraProjectKey = project is null
            ? Get(settings.Jira.JiraProjectKey, "DFAI")
            : Required(project.JiraProjectKey, $"Manager project {project.Id}: jira_project_key");
        var jiraStatusName = Get(settings.Jira.JiraStatusName, "Selected for Development");
        var pickupLabel = Get(settings.Jira.JiraPickupLabel, "ai-agent-picked");
        var failureLabel = Get(settings.Jira.JiraFailureLabel, "ai-agent-failed");
        var analyzedLabel = Get(settings.Jira.JiraAnalyzedLabel, "ai-analyzed");
        ValidateLabel(pickupLabel, "Jira.JiraPickupLabel");
        ValidateLabel(failureLabel, "Jira.JiraFailureLabel");
        ValidateLabel(analyzedLabel, "Jira.JiraAnalyzedLabel");

        // The default query only picks up issues the manager has already planned
        // (the manager stores the plan before adding the analyzed label).
        // A JiraJql override replaces this gate only when there are no project mappings.
        var defaultJql =
            $"project = \"{EscapeJql(jiraProjectKey)}\" " +
            $"AND status = \"{EscapeJql(jiraStatusName)}\" " +
            $"AND issuetype != Epic " +
            $"AND labels in (\"{EscapeJql(analyzedLabel)}\") " +
            "ORDER BY priority DESC, created ASC";

        var agentGitHubToken = Required(settings.GitHub.AgentGitHubToken, "GitHub.AgentGitHubToken");
        var pollSeconds = settings.PollIntervalSeconds;
        if (pollSeconds < 5)
        {
            throw new InvalidOperationException("PollIntervalSeconds must be at least 5 seconds.");
        }

        var jql = Get(settings.Jira.JiraJql, defaultJql);
        if (project is not null && !string.IsNullOrWhiteSpace(settings.Jira.JiraJql))
        {
            jql = ScopeToProject(jql, jiraProjectKey);
            jql = RequireLabel(jql, analyzedLabel);
        }

        return new AgentConfiguration(
            jiraUri,
            Required(settings.Jira.JiraEmail, "Jira.JiraEmail"),
            Required(settings.Jira.JiraApiToken, "Jira.JiraApiToken"),
            agentGitHubToken,
            Required(settings.GitHub.GitHubRepositoryToken, "GitHub.GitHubRepositoryToken"),
            gitHubRepositoryUri,
            AddPickupLabelExclusion(jql, pickupLabel),
            pickupLabel,
            failureLabel,
            Get(settings.Jira.JiraInProgressStatus, "In Progress"),
            Get(settings.Jira.JiraInReviewStatus, "In Review"),
            TimeSpan.FromSeconds(pollSeconds),
            Get(settings.Jira.JiraAssigneeAccountId, string.Empty))
        {
            AnalyzedLabel = analyzedLabel,
            JiraProjectKey = jiraProjectKey,
            ManagerProjectId = project?.Id,
            AgentModel = Get(settings.GitHub.AgentModel, string.Empty),
            RequiresManagerPlan = project is not null || string.IsNullOrWhiteSpace(settings.Jira.JiraJql)
        };
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
