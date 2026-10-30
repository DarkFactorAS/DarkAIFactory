namespace CodingAgent;

internal sealed record AgentConfiguration(
    Uri JiraBaseUrl,
    string JiraEmail,
    string JiraApiToken,
    string GitHubToken,
    string JiraJql,
    string PickupLabel,
    string FailureLabel,
    string WorkspacePath,
    TimeSpan PollInterval)
{
    public static AgentConfiguration Load()
    {
        var jiraBaseUrl = Required("JIRA_BASE_URL");
        if (!Uri.TryCreate(jiraBaseUrl, UriKind.Absolute, out var jiraUri) ||
            jiraUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(jiraUri.UserInfo))
        {
            throw new InvalidOperationException("JIRA_BASE_URL must be an https URL without embedded credentials.");
        }

        var jiraProjectKey = Get("JIRA_PROJECT_KEY", "DFAI");
        var jiraStatusName = Get("JIRA_STATUS_NAME", "Selected for Development");
        var pickupLabel = Get("JIRA_PICKUP_LABEL", "ai-agent-picked");
        var failureLabel = Get("JIRA_FAILURE_LABEL", "ai-agent-failed");
        ValidateLabel(pickupLabel, "JIRA_PICKUP_LABEL");
        ValidateLabel(failureLabel, "JIRA_FAILURE_LABEL");

        var defaultJql =
            $"project = \"{EscapeJql(jiraProjectKey)}\" " +
            $"AND status = \"{EscapeJql(jiraStatusName)}\" " +
            $"AND issuetype != Epic " +
            $"AND (labels is EMPTY OR labels not in (\"{EscapeJql(pickupLabel)}\")) " +
            "ORDER BY priority DESC, created ASC";

        var githubToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            githubToken = Required("GITHUB_TOKEN");
        }
        else
        {
            githubToken = githubToken.Trim();
        }

        var pollSeconds = int.TryParse(Get("POLL_INTERVAL_SECONDS", "30"), out var parsedSeconds)
            ? parsedSeconds
            : throw new InvalidOperationException("POLL_INTERVAL_SECONDS must be an integer.");
        if (pollSeconds < 5)
        {
            throw new InvalidOperationException("POLL_INTERVAL_SECONDS must be at least 5 seconds.");
        }

        return new AgentConfiguration(
            jiraUri,
            Required("JIRA_EMAIL"),
            Required("JIRA_API_TOKEN"),
            githubToken,
            Get("JIRA_JQL", defaultJql),
            pickupLabel,
            failureLabel,
            Path.GetFullPath(Get("WORKSPACE_PATH", "/workspace/repository")),
            TimeSpan.FromSeconds(pollSeconds));
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required environment variable {name} is not set.")
            : value.Trim();
    }

    private static string Get(string name, string defaultValue) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? defaultValue
            : Environment.GetEnvironmentVariable(name)!.Trim();

    private static string EscapeJql(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ValidateLabel(string label, string variableName)
    {
        if (label.Length == 0 || label.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{variableName} must be a non-empty Jira label without whitespace.");
        }
    }
}
