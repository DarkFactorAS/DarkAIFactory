namespace ManagerAgent.Tests;

[Collection("Environment")]
public sealed class AgentConfigurationTests
{
    private static readonly ManagerProject DefaultProject = new(1, "DFAI", "https://github.com/owner/repo");

    private static AgentConfiguration Load() => AgentConfiguration.Load(DefaultProject);

    [Fact]
    public void Load_AppliesDefaults()
    {
        using var env = new EnvironmentScope().WithValidDefaults();

        var config = Load();

        Assert.Equal("ai-manager-planned", config.PickupLabel);
        Assert.Equal("ai-manager-failed", config.FailureLabel);
        Assert.Equal("In Progress", config.InProgressStatus);
        Assert.Equal("In Review", config.InReviewStatus);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PollInterval);
        Assert.Equal("gh-token", config.GitHubToken);
        Assert.Equal(
            "project = \"DFAI\" AND status = \"Selected for Development\" AND issuetype != Epic " +
            "AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) ORDER BY priority DESC, created ASC",
            config.JiraJql);
    }

    [Theory]
    [InlineData("JIRA_BASE_URL")]
    [InlineData("JIRA_EMAIL")]
    [InlineData("JIRA_API_TOKEN")]
    [InlineData("GITHUB_REPOSITORY_TOKEN")]
    public void Load_RequiresVariable(string name)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set(name, null);

        var exception = Assert.Throws<InvalidOperationException>(Load);
        Assert.Contains(name, exception.Message);
    }

    [Fact]
    public void Load_RequiresSomeGitHubToken()
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("GH_TOKEN", null);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_FallsBackToGitHubToken()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("GH_TOKEN", null).Set("GITHUB_TOKEN", " fallback ");

        Assert.Equal("fallback", Load().GitHubToken);
    }

    [Theory]
    [InlineData("http://example.atlassian.net")]
    [InlineData("https://user:pw@example.atlassian.net")]
    [InlineData("not a url")]
    public void Load_RejectsInvalidJiraUrl(string url)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("JIRA_BASE_URL", url);

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
        using var env = new EnvironmentScope().WithValidDefaults();

        Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.Load(DefaultProject with { TargetRepositoryUrl = url }));
    }

    [Fact]
    public void Load_AcceptsStatusOverrides()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_IN_PROGRESS_STATUS", "Doing").Set("JIRA_IN_REVIEW_STATUS", "Code Review");

        var config = Load();

        Assert.Equal("Doing", config.InProgressStatus);
        Assert.Equal("Code Review", config.InReviewStatus);
    }

    [Fact]
    public void Load_AcceptsRepositoryUrlWithGitSuffix()
    {
        using var env = new EnvironmentScope().WithValidDefaults();

        Assert.Equal("github.com", AgentConfiguration.Load(
            DefaultProject with { TargetRepositoryUrl = "https://github.com/owner/repo.git" }).RepositoryUrl.Host);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("4")]
    [InlineData("-1")]
    public void Load_RejectsInvalidPollInterval(string value)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("POLL_INTERVAL_SECONDS", value);

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_AcceptsMinimumPollInterval()
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("POLL_INTERVAL_SECONDS", "5");

        Assert.Equal(TimeSpan.FromSeconds(5), Load().PollInterval);
    }

    [Theory]
    [InlineData("JIRA_PICKUP_LABEL")]
    [InlineData("JIRA_FAILURE_LABEL")]
    public void Load_RejectsLabelsWithWhitespace(string name)
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set(name, "two words");

        Assert.Throws<InvalidOperationException>(Load);
    }

    [Fact]
    public void Load_EscapesDefaultJqlValues()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_STATUS_NAME", "C\\D");

        var jql = AgentConfiguration.Load(DefaultProject with { JiraProjectKey = "A\"B" }).JiraJql;

        Assert.Contains("project = \"A\\\"B\"", jql);
        Assert.Contains("status = \"C\\\\D\"", jql);
    }

    [Fact]
    public void Load_InsertsPickupExclusionBeforeOrderBy()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_JQL", "project = X order  by created DESC");

        Assert.Equal(
            "project = \"DFAI\" AND (project = X) AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) order  by created DESC",
            Load().JiraJql);
    }

    [Fact]
    public void Load_AppendsPickupExclusionWithoutOrderBy()
    {
        using var env = new EnvironmentScope().WithValidDefaults().Set("JIRA_JQL", "project = X");

        Assert.Equal(
            "project = \"DFAI\" AND (project = X) AND (labels is EMPTY OR labels not in (\"ai-manager-planned\"))",
            Load().JiraJql);
    }

    [Fact]
    public void Load_IgnoresOrderByInsideQuotedString()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_JQL", "summary ~ \"x ORDER BY y\"");

        Assert.Equal(
            "project = \"DFAI\" AND (summary ~ \"x ORDER BY y\") AND (labels is EMPTY OR labels not in (\"ai-manager-planned\"))",
            Load().JiraJql);
    }

    [Fact]
    public void Load_UsesDatabaseMappingsInsteadOfEnvironment()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_PROJECT_KEY", "IGNORED")
            .Set("TARGET_REPOSITORY_URL", null);

        var first = AgentConfiguration.Load(new ManagerProject(1, "FIRST", "https://github.com/owner/first"));
        var second = AgentConfiguration.Load(new ManagerProject(2, "SECOND", "https://github.com/owner/second"));

        Assert.Equal("https://github.com/owner/first", first.RepositoryUrl.AbsoluteUri);
        Assert.StartsWith("project = \"FIRST\" AND ", first.JiraJql);
        Assert.Equal("https://github.com/owner/second", second.RepositoryUrl.AbsoluteUri);
        Assert.StartsWith("project = \"SECOND\" AND ", second.JiraJql);
    }

    [Fact]
    public void Load_ScopesOrQueryToMappedProject()
    {
        using var env = new EnvironmentScope().WithValidDefaults()
            .Set("JIRA_JQL", "priority = High OR priority = Highest ORDER BY created");

        Assert.Equal(
            "project = \"DFAI\" AND (priority = High OR priority = Highest) " +
            "AND (labels is EMPTY OR labels not in (\"ai-manager-planned\")) ORDER BY created",
            Load().JiraJql);
    }

    [Fact]
    public async Task MappedProjects_SendDistinctProjectQueriesToJira()
    {
        using var env = new EnvironmentScope().WithValidDefaults();
        var projects = new[]
        {
            new ManagerProject(1, "FIRST", "https://github.com/owner/first"),
            new ManagerProject(2, "SECOND", "https://github.com/owner/second")
        };

        foreach (var project in projects)
        {
            var configuration = AgentConfiguration.Load(project);
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
        using var env = new EnvironmentScope().WithValidDefaults();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.Load(DefaultProject with { JiraProjectKey = key }));
        Assert.Contains("Manager project 1", exception.Message);
    }
}
