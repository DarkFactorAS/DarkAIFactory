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

    public static AgentConfiguration Load(AgentSettings settings, ManagerProject project)
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
        var failureLabel = Get(settings.Jira.JiraFailureLabel, "ai-manager-failed");
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
            AddPickupLabelExclusion(jql, pickupLabel),
            pickupLabel,
            failureLabel,
            "In Progress",
            "In Review",
            LoadPollInterval(settings))
        {
            AnalyzedLabel = analyzedLabel
        };
    }

    public static TimeSpan LoadPollInterval(AgentSettings settings)
    {
        var pollSeconds = settings.PollIntervalSeconds;
        if (pollSeconds < 5)
        {
            throw new InvalidOperationException("PollIntervalSeconds must be at least 5 seconds.");
        }

        return TimeSpan.FromSeconds(pollSeconds);
    }

    private static string ScopeToProject(string jql, string projectKey)
    {
        var orderByIndex = FindOrderByIndex(jql);
        var filter = (orderByIndex < 0 ? jql : jql[..orderByIndex]).TrimEnd();
        var orderBy = orderByIndex < 0 ? string.Empty : $" {jql[orderByIndex..].TrimStart()}";
        return $"project = \"{EscapeJql(projectKey)}\" AND ({filter}){orderBy}";
    }

    private static string Required(string? value, string name)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required JSON setting {name} is not set.")
            : value.Trim();
    }

    private static string Get(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : value.Trim();

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
