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
                JiraTaskIssueType = ReadOptional("Jira.JiraTaskIssueType", defaults.JiraTaskIssueType),
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
            Dashboard = new DashboardSettings
            {
                AiLabels = ParseAiLabels(ReadClientOptional("Dashboard.AiLabels"))
            },
            Claude = new ClaudeSettings
            {
                AnthropicApiKey = ReadClientOptional("Claude.AnthropicApiKey"),
                AgentModel = ReadClientOptional("Claude.AgentModel")
            }
        };
    }

    // Shared by the startup loader and the dashboard editor: split, trim, drop empties and duplicates.
    public static IReadOnlyList<string> ParseAiLabels(string value) =>
        NormalizeAiLabels(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static IReadOnlyList<string> NormalizeAiLabels(IEnumerable<string> labels) =>
        labels.Select(label => label.Trim())
            .Where(label => label.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    // Writes only settings[aidashboard, Dashboard.AiLabels]; no generic settings write is exposed.
    public void SaveDashboardAiLabels(IReadOnlyList<string> labels)
    {
        var value = string.Join(",", NormalizeAiLabels(labels));
        using (var update = connectionFactory.CreateCommand(
            "UPDATE `settings` SET `value` = @value WHERE `client` = 'aidashboard' AND `name` = 'Dashboard.AiLabels'"))
        {
            update.AddParameter("@value", value);
            if (update.ExecuteNonQuery() > 0)
            {
                return;
            }
        }

        using var insert = connectionFactory.CreateCommand(
            "INSERT INTO `settings` (`client`, `name`, `value`) VALUES ('aidashboard', 'Dashboard.AiLabels', @value)");
        insert.AddParameter("@value", value);
        insert.ExecuteNonQuery();
    }

    private static void ValidateClient(string client)
    {
        if (client is not ("aimanager" or "aiworker" or "aiclaudeworker" or "aidashboard"))
        {
            throw new ArgumentException("Unknown settings client.", nameof(client));
        }
    }
}
