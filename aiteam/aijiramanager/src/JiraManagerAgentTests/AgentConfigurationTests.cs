namespace JiraManagerAgent.Tests;

public sealed class AgentConfigurationTests
{
    private static Dictionary<string, string> Valid() => new()
    {
        ["Jira.JiraBaseUrl"] = "https://example.atlassian.net",
        ["Jira.JiraEmail"] = "bot@example.com",
        ["Jira.JiraApiToken"] = "token"
    };

    [Fact]
    public void FromValues_ReadsSharedJiraSettingsWithDefaults()
    {
        var configuration = AgentConfiguration.FromValues(Valid(), 30);

        Assert.Equal("bot@example.com", configuration.JiraEmail);
        Assert.Equal("In Progress", configuration.InProgressStatus);
        Assert.Equal("Task", configuration.IssueType);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.PollInterval);
    }

    [Theory]
    [InlineData("Jira.JiraBaseUrl")]
    [InlineData("Jira.JiraEmail")]
    [InlineData("Jira.JiraApiToken")]
    public void FromValues_RequiresCredentials(string name)
    {
        var values = Valid();
        values[name] = " ";

        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.FromValues(values, 30));
    }

    [Fact]
    public void FromValues_RejectsNonHttpsUrl()
    {
        var values = Valid();
        values["Jira.JiraBaseUrl"] = "http://example.atlassian.net";

        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.FromValues(values, 30));
    }

    [Fact]
    public void LoadPollInterval_RejectsTooShortInterval() =>
        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.LoadPollInterval(4));
}
