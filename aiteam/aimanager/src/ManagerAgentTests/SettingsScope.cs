using Microsoft.Extensions.Configuration;
using AiTeam.Configuration;

namespace ManagerAgent.Tests;

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

internal sealed class SettingsScope : IDisposable
{
    private static SettingsScope? _current;
    private readonly Dictionary<string, string?> _values = [];
    private readonly Dictionary<string, string?> _labels = new()
    {
        ["PickupLabel"] = "ai-manager-planned",
        ["FailureLabel"] = "ai-agent-failed",
        ["AnalyzedLabel"] = "ai-analyzed",
        ["ClarificationLabel"] = "ai-needs-clarification",
        ["ApprovalLabel"] = "ai-awaiting-approval",
        ["ApprovedLabel"] = "ai-approved"
    };
    private string? _workerPickupLabel = "ai-agent-picked";

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
        .Set("GitHub:GitHubRepositoryToken", "repo-token");

    public static WorkflowLabels CurrentLabels =>
        new("aimanager", (_current ?? throw new InvalidOperationException("No test settings scope."))._labels
            .Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!));

    public static WorkflowLabels CurrentWorkerLabels
    {
        get
        {
            var scope = _current ?? throw new InvalidOperationException("No test settings scope.");
            var labels = new Dictionary<string, string>();
            if (scope._workerPickupLabel is not null)
            {
                labels["PickupLabel"] = scope._workerPickupLabel;
            }
            return new WorkflowLabels("aiworker", labels);
        }
    }

    public SettingsScope SetLabel(string codeKey, string? value)
    {
        _labels[codeKey] = value;
        return this;
    }

    public SettingsScope SetWorkerPickupLabel(string? value)
    {
        _workerPickupLabel = value;
        return this;
    }

    public SettingsScope Set(string name, string? value)
    {
        _values[name] = value;
        return this;
    }

    public void Dispose() => _current = null;
}
