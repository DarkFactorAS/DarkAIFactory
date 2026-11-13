using DFCommonLib.DataAccess;

using AiAgent.Core;
namespace AiAgent.Repositories;

public sealed class IntegrationSettingsRepository(IDbConnectionFactory connectionFactory)
{
    public IntegrationSettings Load(string client, int pollIntervalSeconds = 30)
    {
        ValidateClient(client);
        using var command = connectionFactory.CreateCommand(
            "SELECT `client`, `name`, `value` FROM `settings` WHERE `client` = 'shared' OR `client` = @client");
        command.AddParameter("@client", client);
        using var reader = command.ExecuteReader();
        var values = new Dictionary<(string Client, string Name), string>();
        while (reader.Read())
        {
            values.Add((reader.GetString(0), reader.GetString(1)), reader.GetString(2));
        }

        return FromValues(client, values, pollIntervalSeconds);
    }

    internal static IntegrationSettings FromValues(
        string client, IReadOnlyDictionary<(string Client, string Name), string> values,
        int pollIntervalSeconds = 30)
    {
        ValidateClient(client);
        string Read(string scope, string name) =>
            values.TryGetValue((scope, name), out var value)
                ? value
                : throw new InvalidOperationException($"Required database setting settings[{scope}, {name}] is missing.");

        var defaults = new JiraSettings();
        string ReadOptional(string name, string defaultValue) =>
            values.TryGetValue(("shared", name), out var value) ? value : defaultValue;

        string ReadClientOptional(string name) =>
            values.TryGetValue((client, name), out var value) ? value : string.Empty;

        return new IntegrationSettings
        {
            PollIntervalSeconds = pollIntervalSeconds,
            Jira = new JiraSettings
            {
                JiraBaseUrl = Read("shared", "Jira.JiraBaseUrl"),
                JiraEmail = Read("shared", "Jira.JiraEmail"),
                JiraApiToken = Read("shared", "Jira.JiraApiToken"),
                JiraProjectKey = Read("shared", "Jira.JiraProjectKey"),
                JiraStatusName = Read("shared", "Jira.JiraStatusName"),
                JiraJql = Read("shared", "Jira.JiraJql"),
                JiraInProgressStatus = Read("shared", "Jira.JiraInProgressStatus"),
                JiraInReviewStatus = Read("shared", "Jira.JiraInReviewStatus"),
                JiraAssigneeAccountId = Read("shared", "Jira.JiraAssigneeAccountId"),
                JiraAnalysisOnlyStatus = ReadOptional("Jira.JiraAnalysisOnlyStatus", defaults.JiraAnalysisOnlyStatus),
                JiraDoneStatus = ReadOptional("Jira.JiraDoneStatus", defaults.JiraDoneStatus),
                JiraSubtaskIssueType = ReadOptional("Jira.JiraSubtaskIssueType", defaults.JiraSubtaskIssueType),
                JiraAutoApproveSmallChanges = ReadOptional(
                    "Jira.JiraAutoApproveSmallChanges", defaults.JiraAutoApproveSmallChanges)
            },
            GitHub = new GitHubSettings
            {
                AgentGitHubToken = Read("shared", "GitHub.AgentGitHubToken"),
                TargetRepositoryUrl = Read("shared", "GitHub.TargetRepositoryUrl"),
                GitHubRepositoryToken = Read(client, "GitHub.GitHubRepositoryToken"),
                AgentModel = values.TryGetValue((client, "GitHub.AgentModel"), out var agentModel)
                    ? agentModel
                    : string.Empty
            },
            Claude = new ClaudeSettings
            {
                AnthropicApiKey = ReadClientOptional("Claude.AnthropicApiKey"),
                AgentModel = ReadClientOptional("Claude.AgentModel")
            }
        };
    }

    private static void ValidateClient(string client)
    {
        if (client is not ("aimanager" or "aiworker" or "aiclaudeworker" or "aidashboard" or "aiagent"))
        {
            throw new ArgumentException("Unknown settings client.", nameof(client));
        }
    }
}
