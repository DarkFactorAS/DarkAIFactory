using AiTeam.Configuration;

namespace ClaudeCodingAgent.Tests;

public sealed class IntegrationSettingsTests
{
    private static readonly string[] Clients = ["aimanager", "aiworker", "aiclaudeworker", "aidashboard"];

    [Fact]
    public void Load_AllClientsShareEverySettingExceptTheThreeExplicitExceptions()
    {
        var values = Values();
        foreach (var client in Clients)
        {
            // Even unexpected legacy per-client rows must not override shared settings.
            values[(client, "Jira.JiraApiToken")] = "incorrect-client-token";
            values[(client, "GitHub.AgentGitHubToken")] = "incorrect-client-token";
            var settings = IntegrationSettingsRepository.FromValues(client, values, 5);

            Assert.Equal("https://example.atlassian.net", settings.Jira.JiraBaseUrl);
            Assert.Equal("bot@example.com", settings.Jira.JiraEmail);
            Assert.Equal("shared-jira-token", settings.Jira.JiraApiToken);
            Assert.Equal("SHARED", settings.Jira.JiraProjectKey);
            Assert.Equal("Ready", settings.Jira.JiraStatusName);
            Assert.Equal("priority = High", settings.Jira.JiraJql);
            Assert.Equal("Doing", settings.Jira.JiraInProgressStatus);
            Assert.Equal("Review", settings.Jira.JiraInReviewStatus);
            Assert.Equal("shared-assignee", settings.Jira.JiraAssigneeAccountId);
            Assert.Equal("shared-copilot-token", settings.GitHub.AgentGitHubToken);
            Assert.Equal("https://github.com/owner/repo", settings.GitHub.TargetRepositoryUrl);
            Assert.Equal($"{client}-repository-token", settings.GitHub.GitHubRepositoryToken);
            Assert.Equal(5, settings.PollIntervalSeconds);
        }
    }

    [Theory]
    [InlineData("GitHub.GitHubRepositoryToken")]
    public void Load_MissingClientValueNeverFallsBackToSharedOrAnotherClient(string name)
    {
        var values = Values();
        values.Remove(("aiclaudeworker", name));
        values[("shared", name)] = "incorrect-fallback";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IntegrationSettingsRepository.FromValues("aiclaudeworker", values));

        Assert.Contains($"settings[aiclaudeworker, {name}]", exception.Message);
        Assert.DoesNotContain("incorrect-fallback", exception.Message);
    }

    [Fact]
    public void Load_MissingSharedValueNeverFallsBackToClientValue()
    {
        var values = Values();
        values.Remove(("shared", "Jira.JiraApiToken"));
        values[("aiworker", "Jira.JiraApiToken")] = "private-token";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IntegrationSettingsRepository.FromValues("aiworker", values));

        Assert.Contains("settings[shared, Jira.JiraApiToken]", exception.Message);
        Assert.DoesNotContain("private-token", exception.Message);
    }

    [Fact]
    public void Load_ReadsClaudeSettingsOnlyFromTheClaudeWorkerClient()
    {
        var values = Values();
        values[("aiclaudeworker", "Claude.AnthropicApiKey")] = "claude-key";
        values[("aiclaudeworker", "Claude.AgentModel")] = "claude-sonnet-5-5";
        values[("shared", "Claude.AnthropicApiKey")] = "incorrect-shared-key";

        var claude = IntegrationSettingsRepository.FromValues("aiclaudeworker", values);
        var copilot = IntegrationSettingsRepository.FromValues("aiworker", values);

        Assert.Equal("claude-key", claude.Claude.AnthropicApiKey);
        Assert.Equal("claude-sonnet-5-5", claude.Claude.AgentModel);
        Assert.Empty(copilot.Claude.AnthropicApiKey);
        Assert.Empty(copilot.Claude.AgentModel);
    }

    [Fact]
    public void Load_RejectsUnknownClient()
    {
        Assert.Throws<ArgumentException>(() => IntegrationSettingsRepository.FromValues("unknown", Values()));
    }

    private static Dictionary<(string Client, string Name), string> Values()
    {
        var values = new Dictionary<(string, string), string>
        {
            [("shared", "Jira.JiraBaseUrl")] = "https://example.atlassian.net",
            [("shared", "Jira.JiraEmail")] = "bot@example.com",
            [("shared", "Jira.JiraApiToken")] = "shared-jira-token",
            [("shared", "Jira.JiraProjectKey")] = "SHARED",
            [("shared", "Jira.JiraStatusName")] = "Ready",
            [("shared", "Jira.JiraJql")] = "priority = High",
            [("shared", "Jira.JiraInProgressStatus")] = "Doing",
            [("shared", "Jira.JiraInReviewStatus")] = "Review",
            [("shared", "GitHub.AgentGitHubToken")] = "shared-copilot-token",
            [("shared", "GitHub.TargetRepositoryUrl")] = "https://github.com/owner/repo",
            [("shared", "Jira.JiraAssigneeAccountId")] = "shared-assignee"
        };
        foreach (var client in Clients)
        {
            values[(client, "GitHub.GitHubRepositoryToken")] = $"{client}-repository-token";
        }

        return values;
    }
}
