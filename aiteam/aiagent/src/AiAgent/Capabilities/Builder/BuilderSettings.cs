using AiAgent.Core;

namespace AiAgent.Capabilities.Builder;

/// <summary>Jira, GitHub and label configuration of the Builder for one cycle.</summary>
internal sealed record BuilderSettings(
    Uri JiraBaseUrl,
    string JiraEmail,
    string JiraApiToken,
    string GitHubRepositoryToken,
    string AgentGitHubToken,
    string JiraJql,
    string JiraStatusName,
    string PickupLabel,
    string FailureLabel,
    string AnalyzedLabel,
    string IgnoredLabel,
    string InProgressStatus,
    string InReviewStatus,
    string DoneStatus,
    string AssigneeAccountId,
    string? BuildCommand,
    string? TestCommand)
{
    /// <summary>A custom JQL replaces the planned-issue gate: issues without a stored plan get a local plan.</summary>
    public bool AllowLocalPlan => !string.IsNullOrWhiteSpace(JiraJql);

    public static BuilderSettings Load(IntegrationSettings settings, WorkflowLabels labels)
    {
        if (!Uri.TryCreate(settings.Jira.JiraBaseUrl, UriKind.Absolute, out var jiraUri) ||
            jiraUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(jiraUri.UserInfo))
        {
            throw new InvalidOperationException("JiraBaseUrl must be an https URL without embedded credentials.");
        }

        string Label(string key)
        {
            var label = labels.Get(key);
            return label.Any(char.IsWhiteSpace)
                ? throw new InvalidOperationException($"{key} must be a Jira label without whitespace.")
                : label;
        }

        string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        string Required(string? value, string name) => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required database setting {name} is not set.")
            : value.Trim();
        string? Env(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

        return new BuilderSettings(
            jiraUri,
            Required(settings.Jira.JiraEmail, "Jira.JiraEmail"),
            Required(settings.Jira.JiraApiToken, "Jira.JiraApiToken"),
            Required(settings.GitHub.GitHubRepositoryToken, "GitHub.GitHubRepositoryToken"),
            settings.GitHub.AgentGitHubToken,
            settings.Jira.JiraJql.Trim(),
            Or(settings.Jira.JiraStatusName, "Selected for Development"),
            Label("PickupLabel"), Label("FailureLabel"), Label("AnalyzedLabel"), Label("IgnoredLabel"),
            Or(settings.Jira.JiraInProgressStatus, "In Progress"),
            Or(settings.Jira.JiraInReviewStatus, "In Review"),
            Or(settings.Jira.JiraDoneStatus, "Done"),
            Or(settings.Jira.JiraAssigneeAccountId, string.Empty),
            Env("AIAGENT_BUILD_COMMAND"),
            Env("AIAGENT_TEST_COMMAND"));
    }
}
