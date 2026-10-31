namespace CodingAgent;

internal sealed record AgentConfiguration(
    Uri JiraBaseUrl,
    string JiraEmail,
    string JiraApiToken,
    string GitHubToken,
    string GitHubRepositoryToken,
    Uri RepositoryUrl,
    string JiraJql,
    string PickupLabel,
    string FailureLabel,
    string InProgressStatus,
    string InReviewStatus,
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

        var repositoryUrl = Required("TARGET_REPOSITORY_URL");
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) ||
            repositoryUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(repositoryUri.UserInfo) ||
            !string.IsNullOrEmpty(repositoryUri.Query) ||
            !string.IsNullOrEmpty(repositoryUri.Fragment) ||
            repositoryUri.AbsolutePath.Trim('/').Split('/').Length != 2)
        {
            throw new InvalidOperationException(
                "TARGET_REPOSITORY_URL must be an https GitHub repository URL in the form https://github.com/owner/repository.");
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
            Required("GITHUB_REPOSITORY_TOKEN"),
            repositoryUri,
            AddPickupLabelExclusion(Get("JIRA_JQL", defaultJql), pickupLabel),
            pickupLabel,
            failureLabel,
            Get("JIRA_IN_PROGRESS_STATUS", "In Progress"),
            Get("JIRA_IN_REVIEW_STATUS", "In Review"),
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

    private static string AddPickupLabelExclusion(string jql, string pickupLabel)
    {
        var orderByIndex = FindOrderByIndex(jql);
        var filter = (orderByIndex < 0 ? jql : jql[..orderByIndex]).TrimEnd();
        var orderBy = orderByIndex < 0 ? string.Empty : $" {jql[orderByIndex..].TrimStart()}";

        return $"{filter} AND (labels is EMPTY OR labels not in (\"{EscapeJql(pickupLabel)}\")){orderBy}";
    }

    private static int FindOrderByIndex(string jql)
    {
        var inString = false;
        for (var index = 0; index < jql.Length; index++)
        {
            if (inString)
            {
                if (jql[index] == '\\')
                {
                    index++;
                }
                else if (jql[index] == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (jql[index] == '"')
            {
                inString = true;
                continue;
            }

            if (index > 0 && !char.IsWhiteSpace(jql[index - 1]))
            {
                continue;
            }

            if (string.Compare(jql, index, "ORDER", 0, 5, StringComparison.OrdinalIgnoreCase) != 0 ||
                index + 5 >= jql.Length ||
                !char.IsWhiteSpace(jql[index + 5]))
            {
                continue;
            }

            var byIndex = index + 5;
            while (byIndex < jql.Length && char.IsWhiteSpace(jql[byIndex]))
            {
                byIndex++;
            }

            if (string.Compare(jql, byIndex, "BY", 0, 2, StringComparison.OrdinalIgnoreCase) == 0 &&
                (byIndex + 2 == jql.Length || char.IsWhiteSpace(jql[byIndex + 2])))
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateLabel(string label, string variableName)
    {
        if (label.Length == 0 || label.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException($"{variableName} must be a non-empty Jira label without whitespace.");
        }
    }
}
