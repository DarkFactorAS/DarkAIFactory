using AiTeam.Configuration;

namespace ReviewWorker.Tests;

public sealed class AgentConfigurationTests
{
    private static IntegrationSettings Settings() => new()
    {
        GitHub = new GitHubSettings
        {
            AgentGitHubToken = "gh-token",
            GitHubRepositoryToken = "repo-token",
            TargetRepositoryUrl = "https://github.com/owner/repo"
        }
    };

    private static AgentSettings App() => new() { ReviewerLogin = "reviewer" };

    [Fact]
    public void Load_AppliesDefaults()
    {
        var config = AgentConfiguration.Load(Settings(), App());

        Assert.Equal("reviewer", config.ReviewerLogin);
        Assert.Equal(TimeSpan.FromSeconds(30), config.PollInterval);
        Assert.Equal(3, config.MaxAttemptsPerPullRequest);
        Assert.Equal("gh-token", config.AgentGitHubToken);
        Assert.Equal("repo-token", config.GitHubRepositoryToken);
    }

    [Fact]
    public void Load_RequiresReviewerLogin() =>
        Assert.Throws<InvalidOperationException>(() =>
            AgentConfiguration.Load(Settings(), new AgentSettings()));

    [Theory]
    [InlineData(4, 3)]
    [InlineData(30, 0)]
    public void Load_RejectsOutOfRangeValues(int pollSeconds, int attempts) =>
        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.Load(
            Settings(),
            new AgentSettings { ReviewerLogin = "r", PollIntervalSeconds = pollSeconds, MaxAttemptsPerPullRequest = attempts }));

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://example.com/owner/repo")]
    [InlineData("https://user:pw@github.com/owner/repo")]
    [InlineData("")]
    public void Load_RejectsInvalidRepositoryUrl(string url)
    {
        var settings = Settings();
        settings.GitHub.TargetRepositoryUrl = url;
        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.Load(settings, App()));
    }

    [Fact]
    public void Load_RequiresTokens()
    {
        var settings = Settings();
        settings.GitHub.GitHubRepositoryToken = " ";
        Assert.Throws<InvalidOperationException>(() => AgentConfiguration.Load(settings, App()));
    }
}
