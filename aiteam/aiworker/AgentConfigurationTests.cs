namespace CodingAgent.Tests;

[Collection("Environment")]
public sealed class AgentConfigurationTests
{
    private static AgentConfiguration Load() => AgentConfiguration.Load(SettingsScope.Current);

    [Fact]
    public void Load_AppliesDefaults()
    {
        using var env = new SettingsScope().WithValidDefaults();

        var config = Load();

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
    [InlineData("JiraBaseUrl")]
    [InlineData("JiraEmail")]
    [InlineData("JiraApiToken")]
    [InlineData("GitHubRepositoryToken")]
    [InlineData("TargetRepositoryUrl")]
    public void Load_RequiresVariable(string name)
    {
        using var env = new SettingsScope().WithValidDefaults().Set(name, null);

        var exception = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains(name, exception.Message);
    }

    [Fact]
    public void Load_RequiresSomeGitHubToken()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("GitHubToken", null);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_TrimsGitHubToken()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("GitHubToken", " fallback ");

        Assert.Equal("fallback", Load().GitHubToken);
    }

    [Theory]
    [InlineData("http://example.atlassian.net")]
    [InlineData("https://user:pw@example.atlassian.net")]
    [InlineData("not a url")]
    public void Load_RejectsInvalidJiraUrl(string url)
    {
        using var env = new SettingsScope().WithValidDefaults().Set("JiraBaseUrl", url);

        Assert.Throws<InvalidOperationException>(Load);
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
        using var env = new SettingsScope().WithValidDefaults().Set("TargetRepositoryUrl", url);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_AcceptsStatusOverrides()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("JiraInProgressStatus", "Doing").Set("JiraInReviewStatus", "Code Review");

        var config = Load();

        Assert.Equal("Doing", config.InProgressStatus);
        Assert.Equal("Code Review", config.InReviewStatus);
    }

    [Fact]
    public void Load_AcceptsRepositoryUrlWithGitSuffix()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("TargetRepositoryUrl", "https://github.com/owner/repo.git");

        Assert.Equal("github.com", Load().RepositoryUrl.Host);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("-1")]
    public void Load_RejectsInvalidPollInterval(string value)
    {
        using var env = new SettingsScope().WithValidDefaults().Set("PollIntervalSeconds", value);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void JsonBinding_RejectsNonNumericPollInterval()
    {
        using var settings = new SettingsScope().WithValidDefaults().Set("PollIntervalSeconds", "abc");

        Assert.Throws<InvalidOperationException>(() => SettingsScope.Current);
    }

    [Fact]
    public void Load_AcceptsMinimumPollInterval()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("PollIntervalSeconds", "5");

        Assert.Equal(TimeSpan.FromSeconds(5), Load().PollInterval);
    }

    [Theory]
    [InlineData("JiraPickupLabel")]
    [InlineData("JiraFailureLabel")]
    public void Load_RejectsLabelsWithWhitespace(string name)
    {
        using var env = new SettingsScope().WithValidDefaults().Set(name, "two words");

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_EscapesDefaultJqlValues()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("JiraProjectKey", "A\"B").Set("JiraStatusName", "C\\D");

        var jql = Load().JiraJql;

        Assert.Contains("project = \"A\\\"B\"", jql);
        Assert.Contains("status = \"C\\\\D\"", jql);
    }

    [Fact]
    public void Load_InsertsPickupExclusionBeforeOrderBy()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("JiraJql", "project = X order  by created DESC");

        Assert.Equal(
            "project = X AND (labels is EMPTY OR labels not in (\"ai-agent-picked\")) order  by created DESC",
            Load().JiraJql);
    }

    [Fact]
    public void Load_AppendsPickupExclusionWithoutOrderBy()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("JiraJql", "project = X");

        Assert.Equal(
            "project = X AND (labels is EMPTY OR labels not in (\"ai-agent-picked\"))",
            Load().JiraJql);
    }

    [Fact]
    public void Load_IgnoresOrderByInsideQuotedString()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("JiraJql", "summary ~ \"x ORDER BY y\"");

        Assert.Equal(
            "summary ~ \"x ORDER BY y\" AND (labels is EMPTY OR labels not in (\"ai-agent-picked\"))",
            Load().JiraJql);
    }
}
