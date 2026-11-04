using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

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
                JiraFailureLabel = Read("shared", "Jira.JiraFailureLabel"),
                JiraInProgressStatus = Read("shared", "Jira.JiraInProgressStatus"),
                JiraInReviewStatus = Read("shared", "Jira.JiraInReviewStatus"),
                JiraAssigneeAccountId = Read("shared", "Jira.JiraAssigneeAccountId"),
                JiraClarificationLabel = ReadOptional("Jira.JiraClarificationLabel", defaults.JiraClarificationLabel),
                JiraApprovalLabel = ReadOptional("Jira.JiraApprovalLabel", defaults.JiraApprovalLabel),
                JiraApprovedLabel = ReadOptional("Jira.JiraApprovedLabel", defaults.JiraApprovedLabel),
                JiraAnalysisOnlyStatus = ReadOptional("Jira.JiraAnalysisOnlyStatus", defaults.JiraAnalysisOnlyStatus),
                JiraDoneStatus = ReadOptional("Jira.JiraDoneStatus", defaults.JiraDoneStatus),
                JiraSubtaskIssueType = ReadOptional("Jira.JiraSubtaskIssueType", defaults.JiraSubtaskIssueType),
                JiraAutoApproveSmallChanges = ReadOptional(
                    "Jira.JiraAutoApproveSmallChanges", defaults.JiraAutoApproveSmallChanges),
                JiraPickupLabel = Read(client, "Jira.JiraPickupLabel"),
                JiraAnalyzedLabel = Read(client, "Jira.JiraAnalyzedLabel")
            },
            GitHub = new GitHubSettings
            {
                AgentGitHubToken = Read("shared", "GitHub.AgentGitHubToken"),
                TargetRepositoryUrl = Read("shared", "GitHub.TargetRepositoryUrl"),
                GitHubRepositoryToken = Read(client, "GitHub.GitHubRepositoryToken")
            }
        };
    }

    private static void ValidateClient(string client)
    {
        if (client is not ("aimanager" or "aiworker" or "aibuildpage"))
        {
            throw new ArgumentException("Unknown settings client.", nameof(client));
        }
    }
}
