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
                JiraStatusName = Read("shared", "Jira.JiraStatusName"),
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
                // The dashboard performs no GitHub operations, so these per-client rows are optional.
                GitHubRepositoryToken = ReadClientOptional("GitHub.GitHubRepositoryToken"),
                AgentModel = ReadClientOptional("GitHub.AgentModel")
            },
            Dashboard = new DashboardSettings
            {
                AiLabels = ParseAiLabels(ReadClientOptional("Dashboard.AiLabels")),
                ActiveStatuses = ParseStatuses(ReadClientOptional("Dashboard.ActiveStatuses")),
                CardLabels = ParseAiLabels(ReadClientOptional("Dashboard.CardLabels"))
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

    // Status names may contain spaces ("In Progress"): split on commas only, compare case-insensitively.
    public static IReadOnlyList<string> ParseStatuses(string value) =>
        NormalizeStatuses(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static IReadOnlyList<string> NormalizeStatuses(IEnumerable<string> statuses) =>
        statuses.Select(status => status.Trim())
            .Where(status => status.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // Writes only settings[aidashboard, Dashboard.AiLabels]; no generic settings write is exposed.
    public void SaveDashboardAiLabels(IReadOnlyList<string> labels) =>
        SaveDashboardSetting("Dashboard.AiLabels", string.Join(",", NormalizeAiLabels(labels)));

    // Writes only settings[aidashboard, Dashboard.ActiveStatuses].
    public void SaveDashboardActiveStatuses(IReadOnlyList<string> statuses) =>
        SaveDashboardSetting("Dashboard.ActiveStatuses", string.Join(",", NormalizeStatuses(statuses)));

    // Writes only settings[aidashboard, Dashboard.CardLabels].
    public void SaveDashboardCardLabels(IReadOnlyList<string> labels) =>
        SaveDashboardSetting("Dashboard.CardLabels", string.Join(",", NormalizeAiLabels(labels)));

    private void SaveDashboardSetting(string name, string value)
    {
        using (var update = connectionFactory.CreateCommand(
            "UPDATE `settings` SET `value` = @value WHERE `client` = 'aidashboard' AND `name` = @name"))
        {
            update.AddParameter("@value", value);
            update.AddParameter("@name", name);
            if (update.ExecuteNonQuery() > 0)
            {
                return;
            }
        }

        using var insert = connectionFactory.CreateCommand(
            "INSERT INTO `settings` (`client`, `name`, `value`) VALUES ('aidashboard', @name, @value)");
        insert.AddParameter("@name", name);
        insert.AddParameter("@value", value);
        insert.ExecuteNonQuery();
    }

    private static void ValidateClient(string client)
    {
        if (client is not ("aiagent" or "aidashboard"))
        {
            throw new ArgumentException("Unknown settings client.", nameof(client));
        }
    }
}
