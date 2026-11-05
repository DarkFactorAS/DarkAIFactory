namespace JiraManagerAgent;

internal sealed record AgentConfiguration(
    Uri JiraBaseUrl,
    string JiraEmail,
    string JiraApiToken,
    string InProgressStatus,
    string IssueType,
    TimeSpan PollInterval,
    int MaxAttempts)
{
    public const int DefaultMaxAttempts = 3;
    private const string Client = "shared";

    public static AgentConfiguration FromValues(
        IReadOnlyDictionary<string, string> sharedValues, int pollSeconds, int maxAttempts = DefaultMaxAttempts)
    {
        var jiraBaseUrl = Required(sharedValues, "Jira.JiraBaseUrl");
        if (!Uri.TryCreate(jiraBaseUrl, UriKind.Absolute, out var jiraUri) ||
            jiraUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(jiraUri.UserInfo))
        {
            throw new InvalidOperationException("JiraBaseUrl must be an https URL without embedded credentials.");
        }

        if (maxAttempts < 1)
        {
            throw new InvalidOperationException("MaxAttempts must be at least 1.");
        }

        return new AgentConfiguration(
            jiraUri,
            Required(sharedValues, "Jira.JiraEmail"),
            Required(sharedValues, "Jira.JiraApiToken"),
            Optional(sharedValues, "Jira.JiraInProgressStatus", "In Progress"),
            Optional(sharedValues, "Jira.JiraTaskIssueType", "Task"),
            LoadPollInterval(pollSeconds),
            maxAttempts);
    }

    public static TimeSpan LoadPollInterval(int pollSeconds)
    {
        if (pollSeconds < 5)
        {
            throw new InvalidOperationException("PollIntervalSeconds must be at least 5 seconds.");
        }

        return TimeSpan.FromSeconds(pollSeconds);
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException($"Required database setting settings[{Client}, {name}] is not set.");

    private static string Optional(IReadOnlyDictionary<string, string> values, string name, string defaultValue) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : defaultValue;
}
