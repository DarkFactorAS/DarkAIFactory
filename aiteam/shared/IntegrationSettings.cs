namespace AiTeam.Configuration;

public sealed class IntegrationSettings
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
    public string JiraProjectKey { get; set; } = "DFAI";
    public string JiraStatusName { get; set; } = "Selected for Development";
    public string JiraJql { get; set; } = string.Empty;
    public string JiraPickupLabel { get; set; } = "ai-agent-picked";
    public string JiraFailureLabel { get; set; } = "ai-agent-failed";
    public string JiraAnalyzedLabel { get; set; } = "ai-analyzed";
    public string JiraInProgressStatus { get; set; } = "In Progress";
    public string JiraInReviewStatus { get; set; } = "In Review";
    public string JiraAssigneeAccountId { get; set; } = string.Empty;
}

public sealed class GitHubSettings
{
    public string AgentGitHubToken { get; set; } = string.Empty;
    public string GitHubRepositoryToken { get; set; } = string.Empty;
    public string TargetRepositoryUrl { get; set; } = string.Empty;
}
