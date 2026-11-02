using AiTeam.Configuration;
using static AiTeam.Configuration.JiraQuery;

namespace ManagerAgent;

internal sealed record AgentConfiguration(
    Uri JiraBaseUrl,
    string JiraEmail,
    string JiraApiToken,
    string AgentGitHubToken,
    string GitHubRepositoryToken,
    Uri RepositoryUrl,
    string JiraJql,
    string PickupLabel,
    string FailureLabel,
    string InProgressStatus,
    string InReviewStatus,
    TimeSpan PollInterval)
{
    public string AnalyzedLabel { get; init; } = "ai-analyzed";

    public static AgentConfiguration Load(IntegrationSettings settings, ManagerProject project)
    {
        var jiraBaseUrl = Required(settings.Jira.JiraBaseUrl, "Jira.JiraBaseUrl");
        if (!Uri.TryCreate(jiraBaseUrl, UriKind.Absolute, out var jiraUri) ||
            jiraUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(jiraUri.UserInfo))
        {
            throw new InvalidOperationException("JiraBaseUrl must be an https URL without embedded credentials.");
        }

        var repositoryUrl = project.TargetRepositoryUrl.Trim();
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) ||
            repositoryUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(repositoryUri.UserInfo) ||
            !string.IsNullOrEmpty(repositoryUri.Query) ||
            !string.IsNullOrEmpty(repositoryUri.Fragment) ||
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
        var jiraStatusName = Get(settings.Jira.JiraStatusName, "Selected for Development");
        var pickupLabel = Get(settings.Jira.JiraPickupLabel, "ai-manager-planned");
        var failureLabel = Get(settings.Jira.JiraFailureLabel, "ai-agent-failed");
        var analyzedLabel = Get(settings.Jira.JiraAnalyzedLabel, "ai-analyzed");
        ValidateLabel(pickupLabel, "Jira.JiraPickupLabel");
        ValidateLabel(failureLabel, "Jira.JiraFailureLabel");
        ValidateLabel(analyzedLabel, "Jira.JiraAnalyzedLabel");

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
            repositoryUri,
            AddPickupLabelExclusion(AddPickupLabelExclusion(jql, failureLabel), pickupLabel),
            pickupLabel,
            failureLabel,
            Get(settings.Jira.JiraInProgressStatus, "In Progress"),
            Get(settings.Jira.JiraInReviewStatus, "In Review"),
            LoadPollInterval(settings.PollIntervalSeconds))
        {
            AnalyzedLabel = analyzedLabel
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
