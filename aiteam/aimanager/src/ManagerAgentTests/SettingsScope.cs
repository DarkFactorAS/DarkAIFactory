using Microsoft.Extensions.Configuration;

namespace ManagerAgent.Tests;

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

internal sealed class SettingsScope : IDisposable
{
    private static SettingsScope? _current;
    private readonly Dictionary<string, string?> _values = [];

    public SettingsScope() => _current = this;

    public static AgentSettings Current
    {
        get
        {
            var settings = new AgentSettings();
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
        .Set("GitHub:GitHubRepositoryToken", "repo-token");

    public SettingsScope Set(string name, string? value)
    {
        _values[name] = value;
        return this;
    }

    public void Dispose() => _current = null;
}
