namespace ManagerAgent.Tests;

[Collection("Environment")]
public sealed class AgentConfigurationTests
{
    [Fact]
    public void Load_AppliesDefaults()
    {
        using var env = new EnvironmentScope().WithValidDefaults();

        var config = AgentConfiguration.Load();

        Assert.Equal("ai-agent-picked", config.PickupLabel);
        Assert.Equal("ai-agent-failed", config.FailureLabel);
        Assert.Equal("In Progress", config.InProgressStatus);
        Assert.Equal("In Review", config.InReviewStatus);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PollInterval);
        Assert.Equal("gh-token", config.GitHubToken);
        Assert.Equal(
            "project = \"DFAI\" AND status = \"Selected for Development\" AND issuetype != Epic " +
            "AND (labels is EMPTY OR labels not in (\"ai-agent-picked\")) ORDER BY priority DESC, created ASC",
            config.JiraJql);
    }

    [Theory]
    [InlineData("JIRA_BASE_URL")]
    [InlineData("JIRA_EMAIL")]
    [InlineData("JIRA_API_TOKEN")]
    [InlineData("GITHUB_REPOSITORY_TOKEN")]
    [InlineData("TARGET_REPOSITORY_URL")]
    public void Load_RequiresVariable(string name)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set(name, null);

        var exception = Assert.Throws<InvalidOperationException>(AgentConfiguration.Load);
        Assert.Contains(name, exception.Message);
    }

    [Fact]
    public void Load_RequiresSomeGitHubToken()
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("GH_TOKEN", null);

        Assert.Throws<InvalidOperationException>(AgentConfiguration.Load);
    }

    [Fact]
    public void Load_FallsBackToGitHubToken()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("GH_TOKEN", null).Set("GITHUB_TOKEN", " fallback ");

        Assert.Equal("fallback", AgentConfiguration.Load().GitHubToken);
    }

    [Theory]
    [InlineData("http://example.atlassian.net")]
    [InlineData("https://user:pw@example.atlassian.net")]
    [InlineData("not a url")]
    public void Load_RejectsInvalidJiraUrl(string url)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("JIRA_BASE_URL", url);

        Assert.Throws<InvalidOperationException>(AgentConfiguration.Load);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://user@github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo?x=1")]
    [InlineData("https://github.com/owner/repo#frag")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo/extra")]
    public void Load_RejectsInvalidRepositoryUrl(string url)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("TARGET_REPOSITORY_URL", url);

        Assert.Throws<InvalidOperationException>(AgentConfiguration.Load);
    }

    [Fact]
    public void Load_AcceptsStatusOverrides()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_IN_PROGRESS_STATUS", "Doing").Set("JIRA_IN_REVIEW_STATUS", "Code Review");

        var config = AgentConfiguration.Load();

        Assert.Equal("Doing", config.InProgressStatus);
        Assert.Equal("Code Review", config.InReviewStatus);
    }

    [Fact]
    public void Load_AcceptsRepositoryUrlWithGitSuffix()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("TARGET_REPOSITORY_URL", "https://github.com/owner/repo.git");

        Assert.Equal("github.com", AgentConfiguration.Load().RepositoryUrl.Host);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("4")]
    [InlineData("-1")]
    public void Load_RejectsInvalidPollInterval(string value)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("POLL_INTERVAL_SECONDS", value);

        Assert.Throws<InvalidOperationException>(AgentConfiguration.Load);
    }

    [Fact]
    public void Load_AcceptsMinimumPollInterval()
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("POLL_INTERVAL_SECONDS", "5");

        Assert.Equal(TimeSpan.FromSeconds(5), AgentConfiguration.Load().PollInterval);
    }

    [Theory]
    [InlineData("JIRA_PICKUP_LABEL")]
    [InlineData("JIRA_FAILURE_LABEL")]
    public void Load_RejectsLabelsWithWhitespace(string name)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set(name, "two words");

        Assert.Throws<InvalidOperationException>(AgentConfiguration.Load);
    }

    [Fact]
    public void Load_EscapesDefaultJqlValues()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_PROJECT_KEY", "A\"B").Set("JIRA_STATUS_NAME", "C\\D");

        var jql = AgentConfiguration.Load().JiraJql;

        Assert.Contains("project = \"A\\\"B\"", jql);
        Assert.Contains("status = \"C\\\\D\"", jql);
    }

    [Fact]
    public void Load_InsertsPickupExclusionBeforeOrderBy()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_JQL", "project = X order  by created DESC");

        Assert.Equal(
            "project = X AND (labels is EMPTY OR labels not in (\"ai-agent-picked\")) order  by created DESC",
            AgentConfiguration.Load().JiraJql);
    }

    [Fact]
    public void Load_AppendsPickupExclusionWithoutOrderBy()
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("JIRA_JQL", "project = X");

        Assert.Equal(
            "project = X AND (labels is EMPTY OR labels not in (\"ai-agent-picked\"))",
            AgentConfiguration.Load().JiraJql);
    }

    [Fact]
    public void Load_IgnoresOrderByInsideQuotedString()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_JQL", "summary ~ \"x ORDER BY y\"");

        Assert.Equal(
            "summary ~ \"x ORDER BY y\" AND (labels is EMPTY OR labels not in (\"ai-agent-picked\"))",
            AgentConfiguration.Load().JiraJql);
    }
}
