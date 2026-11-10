using AiTeam.Configuration;

namespace ReviewWorker;

internal sealed record AgentConfiguration(
    Uri GitHubRepositoryUrl,
    string AgentGitHubToken,
    string GitHubRepositoryToken,
    string ReviewerLogin,
    TimeSpan PollInterval,
    int MaxAttemptsPerPullRequest,
    string AgentModel = "")
{
    public const int MinimumPollIntervalSeconds = 5;

    public static AgentConfiguration Load(IntegrationSettings settings, AgentSettings appSettings)
    {
        var repositoryUrl = Required(settings.GitHub.TargetRepositoryUrl, "GitHub.TargetRepositoryUrl");
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) ||
            repositoryUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(repositoryUri.UserInfo))
        {
            throw new InvalidOperationException(
                "GitHub.TargetRepositoryUrl must be an https://github.com URL without embedded credentials.");
        }

        if (appSettings.PollIntervalSeconds < MinimumPollIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"PollIntervalSeconds must be at least {MinimumPollIntervalSeconds} seconds.");
        }

        if (appSettings.MaxAttemptsPerPullRequest < 1)
        {
            throw new InvalidOperationException("MaxAttemptsPerPullRequest must be at least 1.");
        }

        return new AgentConfiguration(
            repositoryUri,
            Required(settings.GitHub.AgentGitHubToken, "GitHub.AgentGitHubToken"),
            Required(settings.GitHub.GitHubRepositoryToken, "GitHub.GitHubRepositoryToken"),
            Required(appSettings.ReviewerLogin, "ReviewerLogin"),
            TimeSpan.FromSeconds(appSettings.PollIntervalSeconds),
            appSettings.MaxAttemptsPerPullRequest,
            settings.GitHub.AgentModel);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{name} is required.")
            : value.Trim();
}
