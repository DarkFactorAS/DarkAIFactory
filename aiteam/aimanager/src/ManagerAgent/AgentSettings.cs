using DFCommonLib.Config;

namespace ManagerAgent;

public sealed class AgentSettings : AppSettings
{
    public string JiraBaseUrl { get; set; } = string.Empty;
    public string JiraEmail { get; set; } = string.Empty;
    public string JiraApiToken { get; set; } = string.Empty;
    public string AgentGitHubToken { get; set; } = string.Empty;
    public string GitHubRepositoryToken { get; set; } = string.Empty;
    public string JiraStatusName { get; set; } = "Selected for Development";
    public string JiraJql { get; set; } = string.Empty;
    public string JiraPickupLabel { get; set; } = "ai-manager-planned";
    public string JiraFailureLabel { get; set; } = "ai-manager-failed";
    public string JiraAnalyzedLabel { get; set; } = "ai-analyzed";
    public int PollIntervalSeconds { get; set; } = 30;
}
