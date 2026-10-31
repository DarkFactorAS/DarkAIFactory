namespace ManagerAgent.Tests;

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

internal sealed class EnvironmentScope : IDisposable
{
    private static readonly string[] Names =
    [
        "JIRA_BASE_URL", "JIRA_EMAIL", "JIRA_API_TOKEN", "GH_TOKEN", "GITHUB_TOKEN",
        "GITHUB_REPOSITORY_TOKEN", "TARGET_REPOSITORY_URL", "JIRA_PROJECT_KEY",
        "JIRA_STATUS_NAME", "JIRA_JQL", "JIRA_PICKUP_LABEL", "JIRA_FAILURE_LABEL", "JIRA_ANALYZED_LABEL",
        "JIRA_IN_PROGRESS_STATUS", "JIRA_IN_REVIEW_STATUS", "POLL_INTERVAL_SECONDS"
    ];

    private readonly Dictionary<string, string?> _original = new();

    public EnvironmentScope()
    {
        foreach (var name in Names)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public EnvironmentScope WithValidDefaults()
    {
        Set("JIRA_BASE_URL", "https://example.atlassian.net");
        Set("JIRA_EMAIL", "bot@example.com");
        Set("JIRA_API_TOKEN", "jira-token");
        Set("GH_TOKEN", "gh-token");
        Set("GITHUB_REPOSITORY_TOKEN", "repo-token");
        Set("TARGET_REPOSITORY_URL", "https://github.com/owner/repo");
        return this;
    }

    public EnvironmentScope Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
