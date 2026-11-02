using Microsoft.Extensions.Configuration;
using AiTeam.Configuration;

namespace CodingAgent.Tests;

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

internal sealed class SettingsScope : IDisposable
{
    private static SettingsScope? _current;
    private readonly Dictionary<string, string?> _values = [];

    public SettingsScope() => _current = this;

    public static IntegrationSettings Current
    {
        get
        {
            var settings = new IntegrationSettings();
            new ConfigurationBuilder().AddInMemoryCollection(
                (_current ?? throw new InvalidOperationException("No test settings scope."))._values)
                .Build().Bind(settings);
            return settings;
        }
    }

    public SettingsScope WithValidDefaults() => Set("Jira:JiraBaseUrl", "https://example.atlassian.net")
        .Set("Jira:JiraEmail", "bot@example.com")
        .Set("Jira:JiraApiToken", "jira-token")
        .Set("GitHub:AgentGitHubToken", "gh-token")
        .Set("GitHub:GitHubRepositoryToken", "repo-token")
        .Set("GitHub:TargetRepositoryUrl", "https://github.com/owner/repo");

    public SettingsScope Set(string name, string? value)
    {
        _values[name] = value;
        return this;
    }

    public void Dispose() => _current = null;
}
