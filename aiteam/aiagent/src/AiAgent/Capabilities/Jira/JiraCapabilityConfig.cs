using AiAgent.Core;

namespace AiAgent.Capabilities.Jira;

/// <summary>Configuration of the Jira capability, built from the settings and workflow_labels tables.</summary>
public sealed record JiraCapabilityConfig(
    Uri BaseUrl,
    string Email,
    string ApiToken,
    string TaskIssueType,
    string SelectedStatus,
    string InProgressStatus,
    string InReviewStatus,
    string DoneStatus,
    string AssigneeAccountId,
    string JqlOverride,
    IReadOnlyList<string> UnblockSourceStatuses,
    bool IntakeEnabled,
    string GitHubToken,
    string DefaultRepositoryUrl,
    string PickupLabel,
    string FailureLabel,
    string IgnoredLabel)
{
    public const int MaxIntakeAttempts = 3;
    public static readonly string[] RequiredLabelKeys = ["PickupLabel", "FailureLabel", "IgnoredLabel"];

    public static JiraCapabilityConfig From(IntegrationSettings settings, WorkflowLabels labels)
    {
        var jira = settings.Jira;
        var baseUrl = Required(jira.JiraBaseUrl, "Jira.JiraBaseUrl");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("JiraBaseUrl must be an https URL without embedded credentials.");
        }

        var pickup = labels.Get("PickupLabel");
        var failure = labels.Get("FailureLabel");
        var ignored = labels.Get("IgnoredLabel");
        foreach (var (label, name) in new[] { (pickup, "PickupLabel"), (failure, "FailureLabel"), (ignored, "IgnoredLabel") })
        {
            if (label.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException($"{name} must be a Jira label without whitespace.");
            }
        }

        if (new[] { pickup, failure, ignored }.Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw new InvalidOperationException("Pickup, failure and ignored labels must all differ.");
        }

        var sources = Get(jira.JiraUnblockSourceStatuses, "To Do,Backlog")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!bool.TryParse(Get(jira.JiraIntakeEnabled, "true"), out var intake))
        {
            throw new InvalidOperationException("Jira.JiraIntakeEnabled must be true or false.");
        }

        return new JiraCapabilityConfig(
            uri, Required(jira.JiraEmail, "Jira.JiraEmail"), Required(jira.JiraApiToken, "Jira.JiraApiToken"),
            Get(jira.JiraTaskIssueType, "Task"),
            Get(jira.JiraStatusName, "Selected for Development"),
            Get(jira.JiraInProgressStatus, "In Progress"),
            Get(jira.JiraInReviewStatus, "In Review"),
            Get(jira.JiraDoneStatus, "Done"),
            jira.JiraAssigneeAccountId.Trim(),
            jira.JiraJql.Trim(),
            sources, intake,
            settings.GitHub.GitHubRepositoryToken.Trim(),
            settings.GitHub.TargetRepositoryUrl.Trim(),
            pickup, failure, ignored);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required database setting {name} is not set.")
            : value.Trim();

    private static string Get(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}
