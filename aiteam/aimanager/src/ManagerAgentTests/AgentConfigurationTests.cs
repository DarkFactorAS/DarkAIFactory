using AiTeam.Configuration;

namespace ManagerAgent.Tests;

[Collection("Environment")]
public sealed class AgentConfigurationTests
{
    private static readonly ManagerProject DefaultProject = new(1, "DFAI", "https://github.com/owner/repo");

    private static AgentConfiguration Load() => AgentConfiguration.Load(SettingsScope.Current, DefaultProject);

    [Fact]
    public void Load_AppliesDefaults()
    {
        using var env = new SettingsScope().WithValidDefaults();

        var config = Load();

        Assert.Equal("ai-manager-planned", config.PickupLabel);
        Assert.Equal("ai-agent-failed", config.FailureLabel);
        Assert.Equal("ai-analyzed", config.AnalyzedLabel);
        Assert.Equal("In Progress", config.InProgressStatus);
        Assert.Equal("In Review", config.InReviewStatus);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PollInterval);
        Assert.Equal("gh-token", config.AgentGitHubToken);
        Assert.Equal(
            "project = \"DFAI\" AND status = \"Selected for Development\" AND issuetype != Epic " +
            "AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) " +
            "AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) ORDER BY priority DESC, created ASC",
            config.JiraJql);
    }

    [Theory]
    [InlineData("Jira:JiraBaseUrl")]
    [InlineData("Jira:JiraEmail")]
    [InlineData("Jira:JiraApiToken")]
    [InlineData("GitHub:GitHubRepositoryToken")]
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
        using var env = new SettingsScope().WithValidDefaults();

        Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.Load(SettingsScope.Current, DefaultProject with { TargetRepositoryUrl = url }));
    }

    [Fact]
    public void Load_ReadsSharedStatusesEvenThoughManagerDoesNotTransitionIssues()
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
        using var env = new SettingsScope().WithValidDefaults();

        Assert.Equal("github.com", AgentConfiguration.Load(SettingsScope.Current,
            DefaultProject with { TargetRepositoryUrl = "https://github.com/owner/repo.git" }).RepositoryUrl.Host);
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
    public void Load_AcceptsAnalyzedLabelOverride()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraAnalyzedLabel", " custom-analyzed ");

        Assert.Equal("custom-analyzed", Load().AnalyzedLabel);
    }

    [Fact]
    public void Load_EscapesDefaultJqlValues()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraStatusName", "C\\D");

        var jql = AgentConfiguration.Load(SettingsScope.Current, DefaultProject with { JiraProjectKey = "A\"B" }).JiraJql;

        Assert.Contains("project = \"A\\\"B\"", jql);
        Assert.Contains("status = \"C\\\\D\"", jql);
    }

    [Fact]
    public void Load_InsertsPickupExclusionBeforeOrderBy()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "project = X order  by created DESC");

        Assert.Equal(
            "project = \"DFAI\" AND (project = X) AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) order  by created DESC",
            Load().JiraJql);
    }

    [Fact]
    public void Load_ScopesOrderingOnlyOverrideWithoutEmptyFilter()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "ORDER BY created DESC");

        Assert.Equal(
            "project = \"DFAI\" AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) ORDER BY created DESC",
            Load().JiraJql);
    }

    [Fact]
    public void Load_AppendsPickupExclusionWithoutOrderBy()
    {
        using var env = new SettingsScope().WithValidDefaults().Set("Jira:JiraJql", "project = X");

        Assert.Equal(
            "project = \"DFAI\" AND (project = X) AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) AND (labels is EMPTY OR labels not in (\"ai-manager-planned\"))",
            Load().JiraJql);
    }

    [Fact]
    public void Load_IgnoresOrderByInsideQuotedString()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "summary ~ \"x ORDER BY y\"");

        Assert.Equal(
            "project = \"DFAI\" AND (summary ~ \"x ORDER BY y\") AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) AND (labels is EMPTY OR labels not in (\"ai-manager-planned\"))",
            Load().JiraJql);
    }

    [Fact]
    public void Load_IgnoresOrderByInsideSingleQuotedString()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "summary ~ 'x ORDER BY y' ORDER BY created");

        Assert.Equal(
            "project = \"DFAI\" AND (summary ~ 'x ORDER BY y') " +
            "AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) " +
            "AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) ORDER BY created",
            Load().JiraJql);
    }

    [Theory]
    [InlineData("")]
    [InlineData("summary ~ 'ORDER BY' OR priority = High ORDER BY created")]
    public void Load_AlwaysExcludesFailedPlans(string jql)
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", jql)
            .Set("Jira:JiraFailureLabel", "failed\"plan");

        Assert.Contains("AND (labels is EMPTY OR labels not in (\"failed\\\"plan\"))", Load().JiraJql);
    }

    [Fact]
    public void Load_UsesDatabaseMappingsInsteadOfEnvironment()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraProjectKey", "IGNORED")
            .Set("GitHub:TargetRepositoryUrl", null);

        var first = AgentConfiguration.Load(SettingsScope.Current, new ManagerProject(1, "FIRST", "https://github.com/owner/first"));
        var second = AgentConfiguration.Load(SettingsScope.Current, new ManagerProject(2, "SECOND", "https://github.com/owner/second"));

        Assert.Equal("https://github.com/owner/first", first.RepositoryUrl.AbsoluteUri);
        Assert.StartsWith("project = \"FIRST\" AND ", first.JiraJql);
        Assert.Equal("https://github.com/owner/second", second.RepositoryUrl.AbsoluteUri);
        Assert.StartsWith("project = \"SECOND\" AND ", second.JiraJql);
    }

    [Fact]
    public void Load_ScopesOrQueryToMappedProject()
    {
        using var env = new SettingsScope().WithValidDefaults()
            .Set("Jira:JiraJql", "priority = High OR priority = Highest ORDER BY created");

        Assert.Equal(
            "project = \"DFAI\" AND (priority = High OR priority = Highest) " +
            "AND (labels is EMPTY OR labels not in (\"ai-agent-failed\")) " +
            "AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) ORDER BY created",
            Load().JiraJql);
    }

    [Fact]
    public async Task MappedProjects_SendDistinctProjectQueriesToJira()
    {
        using var env = new SettingsScope().WithValidDefaults();
        var projects = new[]
        {
            new ManagerProject(1, "FIRST", "https://github.com/owner/first"),
            new ManagerProject(2, "SECOND", "https://github.com/owner/second")
        };

        foreach (var project in projects)
        {
            var configuration = AgentConfiguration.Load(SettingsScope.Current, project);
            using var client = new JiraClient(configuration, new ProjectQueryHandler(project.JiraProjectKey));

            Assert.Empty(await client.FindWorkAsync(CancellationToken.None));
        }
    }

    private sealed class ProjectQueryHandler(string projectKey) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Assert.Contains($"project = \"{projectKey}\"", query);
            Assert.Contains("labels not in (\"ai-manager-planned\")", query);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"issues":[]}""")
            });
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Load_RejectsEmptyProjectKey(string key)
    {
        using var env = new SettingsScope().WithValidDefaults();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.Load(SettingsScope.Current, DefaultProject with { JiraProjectKey = key }));
        Assert.Contains("Manager project 1", exception.Message);
    }
}
