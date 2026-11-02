using AiTeam.Configuration;

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
        Assert.Equal("ai-analyzed", config.AnalyzedLabel);
        Assert.Equal("In Progress", config.InProgressStatus);
        Assert.Equal("In Review", config.InReviewStatus);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PollInterval);
        Assert.Equal("gh-token", config.AgentGitHubToken);
        Assert.Equal(
            "project = \"DFAI\" AND status = \"Selected for Development\" AND issuetype != Epic " +
            "AND labels in (\"ai-analyzed\") " +
            "AND (labels is EMPTY OR labels not in (\"ai-agent-picked\")) ORDER BY priority DESC, created ASC",
            config.JiraJql);
    }

    [Fact]
    public void Load_UsesEachManagerProjectMapping()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("GitHub:TargetRepositoryUrl", null)
            .Set("Jira:JiraProjectKey", "IGNORED");
        var first = AgentConfiguration.Load(SettingsScope.Current,
            new ManagerProject(1, "FIRST", "https://github.com/owner/first"));
        var second = AgentConfiguration.Load(SettingsScope.Current,
            new ManagerProject(2, "SECOND", "https://github.com/owner/second"));

        Assert.Equal(1, first.ManagerProjectId);
        Assert.Equal("https://github.com/owner/first", first.RepositoryUrl.AbsoluteUri);
        Assert.StartsWith("project = \"FIRST\" AND ", first.JiraJql);
        Assert.Equal(2, second.ManagerProjectId);
        Assert.Equal("https://github.com/owner/second", second.RepositoryUrl.AbsoluteUri);
        Assert.StartsWith("project = \"SECOND\" AND ", second.JiraJql);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Load_RejectsEmptyMappedProject(string key)
    {
        using var env = new SettingsScope().WithValidDefaults();

        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.Load(SettingsScope.Current,
            new ManagerProject(1, key, "https://github.com/owner/repo")));
    }

    [Fact]
    public void Load_DoesNotFallBackFromInvalidMapping()
    {
        using var env = new SettingsScope().WithValidDefaults();

        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.Load(SettingsScope.Current,
            new ManagerProject(1, "FIRST", "https://gitlab.com/owner/repo")));
    }

    [Theory]
    [InlineData("priority = High OR priority = Highest", "project = \"FIRST\" AND (priority = High OR priority = Highest)")]
    [InlineData("summary ~ 'x ORDER BY y' ORDER BY created", "project = \"FIRST\" AND (summary ~ 'x ORDER BY y')")]
    [InlineData("ORDER BY created", "project = \"FIRST\"")]
    public void Load_ScopesCustomQueriesToMappedProject(string jql, string expectedPrefix)
    {
        using var env = new SettingsScope().WithValidDefaults().Set("Jira:JiraJql", jql);
        var config = AgentConfiguration.Load(SettingsScope.Current,
            new ManagerProject(1, "FIRST", "https://github.com/owner/first"));

        Assert.StartsWith(expectedPrefix + " AND (labels is EMPTY", config.JiraJql);
        if (jql.Contains("ORDER BY created", StringComparison.Ordinal))
        {
            Assert.EndsWith(" ORDER BY created", config.JiraJql);
        }
    }

    [Theory]
    [InlineData("Jira:JiraBaseUrl")]
    [InlineData("Jira:JiraEmail")]
    [InlineData("Jira:JiraApiToken")]
    [InlineData("GitHub:GitHubRepositoryToken")]
    [InlineData("GitHub:TargetRepositoryUrl")]
    public void Load_RequiresVariable(string name)
    {
        using var env = new SettingsScope().WithValidDefaults().Set(name, null);

        var exception = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains(name.Replace(':', '.'), exception.Message);
    }

    [Fact]
    public void Load_RequiresSomeAgentGitHubToken()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("GitHub:AgentGitHubToken", null);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_DoesNotUseLegacyFlatToken()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("GitHub:AgentGitHubToken", null)
            .Set("AgentGitHubToken", "legacy-token");

        var exception = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains("GitHub.AgentGitHubToken", exception.Message);
    }

    [Fact]
    public void Load_TrimsAgentGitHubToken()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("GitHub:AgentGitHubToken", " fallback ");

        Assert.Equal("fallback", Load().AgentGitHubToken);
    }

    [Theory]
    [InlineData("http://example.atlassian.net")]
    [InlineData("https://user:pw@example.atlassian.net")]
    [InlineData("not a url")]
    public void Load_RejectsInvalidJiraUrl(string url)
    {
        using var env = new SettingsScope().WithValidDefaults().Set("Jira:JiraBaseUrl", url);

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
        using var env = new SettingsScope().WithValidDefaults().Set("GitHub:TargetRepositoryUrl", url);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_AcceptsStatusOverrides()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraInProgressStatus", "Doing").Set("Jira:JiraInReviewStatus", "Code Review");

        var config = Load();

        Assert.Equal("Doing", config.InProgressStatus);
        Assert.Equal("Code Review", config.InReviewStatus);
    }

    [Fact]
    public void Load_AcceptsRepositoryUrlWithGitSuffix()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("GitHub:TargetRepositoryUrl", "https://github.com/owner/repo.git");

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
    [InlineData("Jira:JiraPickupLabel")]
    [InlineData("Jira:JiraFailureLabel")]
    [InlineData("Jira:JiraAnalyzedLabel")]
    public void Load_RejectsLabelsWithWhitespace(string name)
    {
        using var env = new SettingsScope().WithValidDefaults().Set(name, "two words");

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_EscapesDefaultJqlValues()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraProjectKey", "A\"B").Set("Jira:JiraStatusName", "C\\D");

        var jql = Load().JiraJql;

        Assert.Contains("project = \"A\\\"B\"", jql);
        Assert.Contains("status = \"C\\\\D\"", jql);
    }

    [Fact]
    public void Load_RequiresAnalyzedLabelInDefaultJql()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraAnalyzedLabel", "planned\"x");

        var config = Load();

        Assert.Equal("planned\"x", config.AnalyzedLabel);
        Assert.Contains("AND labels in (\"planned\\\"x\")", config.JiraJql);
    }

    [Fact]
    public void Load_JqlOverrideSkipsAnalyzedLabelRequirement()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("Jira:JiraJql", "project = X");

        Assert.DoesNotContain("ai-analyzed", Load().JiraJql);
    }

    [Fact]
    public void Load_InsertsPickupExclusionBeforeOrderBy()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "project = X order  by created DESC");

        Assert.Equal(
            "project = X AND (labels is EMPTY OR labels not in (\"ai-agent-picked\")) order  by created DESC",
            Load().JiraJql);
    }

    [Fact]
    public void Load_AppendsPickupExclusionWithoutOrderBy()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("Jira:JiraJql", "project = X");

        Assert.Equal(
            "project = X AND (labels is EMPTY OR labels not in (\"ai-agent-picked\"))",
            Load().JiraJql);
    }

    [Fact]
    public void Load_IgnoresOrderByInsideQuotedString()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "summary ~ \"x ORDER BY y\"");

        Assert.Equal(
            "summary ~ \"x ORDER BY y\" AND (labels is EMPTY OR labels not in (\"ai-agent-picked\"))",
            Load().JiraJql);
    }
}
