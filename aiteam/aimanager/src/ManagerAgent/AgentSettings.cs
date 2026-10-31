using DFCommonLib.Config;

namespace ManagerAgent;

public sealed class AgentSettings : AppSettings
{
    public JiraSettings Jira { get; set; } = new();
    public GitHubSettings GitHub { get; set; } = new();
    public int PollIntervalSeconds { get; set; } = 30;
}

public sealed class JiraSettings
{
    public string JiraBaseUrl { get; set; } = string.Empty;
    public string JiraEmail { get; set; } = string.Empty;
    public string JiraApiToken { get; set; } = string.Empty;
    public string JiraStatusName { get; set; } = "Selected for Development";
    public string JiraJql { get; set; } = string.Empty;
    public string JiraPickupLabel { get; set; } = "ai-manager-planned";
    public string JiraFailureLabel { get; set; } = "ai-manager-failed";
    public string JiraAnalyzedLabel { get; set; } = "ai-analyzed";
}

public sealed class GitHubSettings
{
    public string AgentGitHubToken { get; set; } = string.Empty;
    public string GitHubRepositoryToken { get; set; } = string.Empty;
}
