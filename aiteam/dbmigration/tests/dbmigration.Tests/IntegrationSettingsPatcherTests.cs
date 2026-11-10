using dbmigration.Repository;
using DFCommonLib.DataAccess;
using System.Text.RegularExpressions;

namespace dbmigration.Tests;

public sealed class IntegrationSettingsPatcherTests
{
    private static readonly string[] Clients = ["aimanager", "aiworker", "aibuildpage"];

    [Fact]
    public void Patcher_SeedsExactlyTheRowsRequiredByEveryClient()
    {
        var patcher = new SettingsPatcherStub(0);
        Assert.True(IntegrationSettingsPatcher.Run(patcher));
        var insertSql = patcher.Sql.Where(sql => sql.StartsWith("INSERT INTO", StringComparison.Ordinal));
        var values = Regex.Matches(string.Join(Environment.NewLine, insertSql),
            @"\('([^']+)', '([^']+)', '([^']*)'\)")
            .ToDictionary(match => (Client: match.Groups[1].Value, Name: match.Groups[2].Value),
                match => match.Groups[3].Value);

        Assert.Equal(18, values.Count);
        Assert.Contains("ON DUPLICATE KEY UPDATE `value` = `settings`.`value`", patcher.Sql[4]);
        Assert.Contains("DROP CONSTRAINT `ck_settings_scope`", patcher.Sql[3]);
        Assert.Contains("'GitHub.AgentModel'", patcher.Sql[3]);
        Assert.Contains("('aiworker', 'GitHub.AgentModel', 'gpt-5-mini')", patcher.Sql[4]);
        Assert.Equal(11, values.Keys.Count(key => key.Client == "shared"));
        Assert.DoesNotContain("Label", string.Join(",", values.Keys.Select(key => key.Name)));
        Assert.Empty(values[("shared", "Jira.JiraAssigneeAccountId")]);
        Assert.Empty(values[("shared", "Jira.JiraApiToken")]);
        Assert.Empty(values[("shared", "GitHub.AgentGitHubToken")]);
        Assert.Equal("gpt-5-mini", values[("aiworker", "GitHub.AgentModel")]);
        Assert.Equal(3, values.Keys.Count(key => key.Client == "aiclaudeworker"));
        Assert.Empty(values[("aiclaudeworker", "GitHub.GitHubRepositoryToken")]);
        Assert.Empty(values[("aiclaudeworker", "Claude.AnthropicApiKey")]);
        Assert.Equal("claude-sonnet-5-5", values[("aiclaudeworker", "Claude.AgentModel")]);
        Assert.Contains("`client` = 'aiclaudeworker' AND `name` IN", patcher.Sql[9]);
        Assert.Contains("'Claude.AnthropicApiKey'", patcher.Sql[9]);
        foreach (var client in Clients)
        {
            Assert.Equal(client == "aiworker" ? 2 : 1, values.Keys.Count(key => key.Client == client));
            Assert.Empty(values[(client, "GitHub.GitHubRepositoryToken")]);
        }
    }

    [Theory]
    [InlineData(0, true, 11)]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    [InlineData(4, false, 4)]
    [InlineData(5, false, 5)]
    [InlineData(6, false, 6)]
    [InlineData(7, false, 7)]
    [InlineData(8, false, 8)]
    [InlineData(9, false, 9)]
    [InlineData(10, false, 10)]
    [InlineData(11, false, 11)]
    public void Patcher_CreatesAndSeedsSettingsAndPropagatesFailures(
        int failingPatch, bool expected, int patchCount)
    {
        var patcher = new SettingsPatcherStub(failingPatch);

        Assert.Equal(expected, IntegrationSettingsPatcher.Run(patcher));
        Assert.Equal("IntegrationSettings", patcher.System);
        Assert.Equal(patchCount, patcher.Sql.Count);
        Assert.Contains("CHARACTER SET ascii COLLATE ascii_bin", patcher.Sql[0]);
        Assert.Contains("CONSTRAINT `ck_settings_scope` CHECK", patcher.Sql[0]);
        Assert.Contains("'GitHub.AgentModel'", patcher.Sql[0]);
        Assert.Contains("`client` = 'shared' AND `name` NOT IN", patcher.Sql[0]);
        Assert.Contains("`client` IN ('aimanager', 'aiworker', 'aibuildpage') AND `name` IN", patcher.Sql[0]);
    }

    [Fact]
    public void Patcher_ReusesExistingMigrationHistoryWithoutRecreatingTablesOrReseeding()
    {
        var patcher = new HistoryPatcher();
        patcher.Applied.UnionWith([
            ("StartupDatabasePatcher", 1),
            ("StartupDatabasePatcher", 2),
            ("StartupDatabasePatcher", 3),
            ("StartupDatabasePatcher", 4),
            ("StartupDatabasePatcher", 5),
            ("IntegrationSettings", 1),
            ("IntegrationSettings", 2),
            ("IntegrationSettings", 3),
            ("IntegrationSettings", 4),
            ("IntegrationSettings", 5),
            ("IntegrationSettings", 6),
            ("IntegrationSettings", 7),
            ("IntegrationSettings", 8),
            ("IntegrationSettings", 9),
            ("IntegrationSettings", 10),
            ("IntegrationSettings", 11),
            ("WorkflowLabels", 1),
            ("WorkflowLabels", 2),
            ("WorkflowLabels", 3),
            ("WorkflowLabels", 4),
            ("AgentTasks", 1),
            ("AgentConfig", 1), ("AgentConfig", 2), ("AgentConfig", 3), ("AgentConfig", 4),
            ("AgentConfig", 5), ("AgentConfig", 6), ("AgentConfig", 7)]);

        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Empty(patcher.Executed);
    }

    [Fact]
    public void AgentTaskPatcher_CreatesTableIdempotentlyAndPropagatesFailure()
    {
        var patcher = new SettingsPatcherStub(0);
        Assert.True(AgentTaskPatcher.Run(patcher));
        Assert.Equal("AgentTasks", patcher.System);
        var sql = Assert.Single(patcher.Sql);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `agent_tasks`", sql);
        foreach (var column in new[]
            { "`id`", "`project_key`", "`title`", "`description`", "`status`", "`jira_key`", "`created`", "`updated`" })
        {
            Assert.Contains(column, sql);
        }
        Assert.Contains("IN ('New', 'Created', 'Failed')", sql);

        Assert.False(AgentTaskPatcher.Run(new SettingsPatcherStub(1)));
    }

    [Theory]
    [InlineData(0, true, 7)]
    [InlineData(1, false, 1)]
    [InlineData(4, false, 4)]
    [InlineData(7, false, 7)]
    public void AgentConfigPatcher_CreatesTablesAndSeedsIdempotently(int failingPatch, bool expected, int count)
    {
        var patcher = new SettingsPatcherStub(failingPatch);
        Assert.Equal(expected, AgentConfigPatcher.Run(patcher));
        Assert.Equal("AgentConfig", patcher.System);
        Assert.Equal(count, patcher.Sql.Count);
        if (!expected)
        {
            return;
        }

        Assert.All(patcher.Sql.Take(4), sql => Assert.Contains("CREATE TABLE IF NOT EXISTS", sql));
        foreach (var column in new[] { "`name`", "`role`", "`instructions`", "`language_model_key`" })
        {
            Assert.Contains(column, patcher.Sql[0]);
        }

        Assert.Contains("`agent_capabilities`", patcher.Sql[1]);
        Assert.Contains("`enabled`", patcher.Sql[1]);
        Assert.Contains("`language_models`", patcher.Sql[2]);
        Assert.Contains("`enabled`", patcher.Sql[2]);
        foreach (var column in new[]
            { "`input_tokens`", "`output_tokens`", "`tool_calls`", "`duration_ms`", "`success_count`", "`failure_count`" })
        {
            Assert.Contains(column, patcher.Sql[3]);
        }

        Assert.All(patcher.Sql.Skip(4), sql => Assert.Contains("ON DUPLICATE KEY UPDATE", sql));
        Assert.Contains("VALUES ('default'", patcher.Sql[5]);
    }

    [Fact]
    public void WorkflowLabelPatcher_CreatesTableInsertsRowsAndDeletesLabelSettings()
    {
        var patcher = new SettingsPatcherStub(0);
        Assert.True(WorkflowLabelPatcher.Run(patcher));

        Assert.Equal("WorkflowLabels", patcher.System);
        Assert.Equal(4, patcher.Sql.Count);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `workflow_labels`", patcher.Sql[0]);
        var rows = Regex.Matches(patcher.Sql[1], @"\('(aimanager|aiworker|aidashboard)', '([^']+)', '([^']+)', '")
            .ToDictionary(match => (match.Groups[1].Value, match.Groups[2].Value), match => match.Groups[3].Value);
        Assert.Equal(9, rows.Count);
        Assert.DoesNotContain(rows.Keys, key => key.Item1 == "aidashboard");
        Assert.Equal("ai-manager-planned", rows[("aimanager", "PickupLabel")]);
        Assert.Equal("ai-agent-picked", rows[("aiworker", "PickupLabel")]);
        Assert.Equal("ai-agent-failed", rows[("aimanager", "FailureLabel")]);
        Assert.Equal("ai-agent-failed", rows[("aiworker", "FailureLabel")]);
        Assert.Equal("ai-analyzed", rows[("aimanager", "AnalyzedLabel")]);
        Assert.Equal("ai-analyzed", rows[("aiworker", "AnalyzedLabel")]);
        Assert.Equal("ai-needs-clarification", rows[("aimanager", "ClarificationLabel")]);
        Assert.Equal("ai-awaiting-approval", rows[("aimanager", "ApprovalLabel")]);
        Assert.Equal("ai-approved", rows[("aimanager", "ApprovedLabel")]);
        Assert.StartsWith("DELETE FROM `settings`", patcher.Sql[2]);
        foreach (var name in new[]
            {
                "JiraPickupLabel", "JiraAnalyzedLabel", "JiraFailureLabel",
                "JiraClarificationLabel", "JiraApprovalLabel", "JiraApprovedLabel"
            })
        {
            Assert.Contains($"'Jira.{name}'", patcher.Sql[2]);
        }

        Assert.Contains("SELECT 'aiclaudeworker', `code_key`, `label_name`, `description`", patcher.Sql[3]);
        Assert.Contains("FROM `workflow_labels`", patcher.Sql[3]);
        Assert.Contains("WHERE `agent_name` = 'aiworker'", patcher.Sql[3]);
        Assert.Contains("('PickupLabel', 'FailureLabel', 'AnalyzedLabel')", patcher.Sql[3]);
        Assert.Contains("ON DUPLICATE KEY UPDATE `label_name` = `workflow_labels`.`label_name`", patcher.Sql[3]);
    }

    [Fact]
    public void Patcher_CompletesPartiallyMigratedDatabaseOnlyOnce()
    {
        var patcher = new HistoryPatcher();
        patcher.Applied.Add(("StartupDatabasePatcher", 1));
        patcher.Applied.Add(("IntegrationSettings", 1));

        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Equal([
            ("StartupDatabasePatcher", 2),
            ("StartupDatabasePatcher", 3),
            ("StartupDatabasePatcher", 4),
            ("StartupDatabasePatcher", 5),
            ("IntegrationSettings", 2),
            ("IntegrationSettings", 3),
            ("IntegrationSettings", 4),
            ("IntegrationSettings", 5),
            ("IntegrationSettings", 6),
            ("IntegrationSettings", 7),
            ("IntegrationSettings", 8),
            ("IntegrationSettings", 9),
            ("IntegrationSettings", 10),
            ("IntegrationSettings", 11),
            ("WorkflowLabels", 1),
            ("WorkflowLabels", 2),
            ("WorkflowLabels", 3),
            ("WorkflowLabels", 4),
            ("AgentTasks", 1),
            ("AgentConfig", 1), ("AgentConfig", 2), ("AgentConfig", 3), ("AgentConfig", 4),
            ("AgentConfig", 5), ("AgentConfig", 6), ("AgentConfig", 7)], patcher.Executed);

        patcher.Executed.Clear();
        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Empty(patcher.Executed);
    }

    [Fact]
    public void Patcher_UpgradesExistingPlansWithoutReapplyingOldPatches()
    {
        var patcher = new HistoryPatcher();
        patcher.Applied.UnionWith([
            ("StartupDatabasePatcher", 1), ("StartupDatabasePatcher", 2), ("StartupDatabasePatcher", 3),
            ("StartupDatabasePatcher", 4), ("StartupDatabasePatcher", 5),
            ("IntegrationSettings", 1), ("IntegrationSettings", 2)]);

        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Equal([
            ("IntegrationSettings", 3), ("IntegrationSettings", 4), ("IntegrationSettings", 5),
            ("IntegrationSettings", 6), ("IntegrationSettings", 7), ("IntegrationSettings", 8),
            ("IntegrationSettings", 9), ("IntegrationSettings", 10), ("IntegrationSettings", 11),
            ("WorkflowLabels", 1), ("WorkflowLabels", 2), ("WorkflowLabels", 3), ("WorkflowLabels", 4),
            ("AgentTasks", 1),
            ("AgentConfig", 1), ("AgentConfig", 2), ("AgentConfig", 3), ("AgentConfig", 4),
            ("AgentConfig", 5), ("AgentConfig", 6), ("AgentConfig", 7)], patcher.Executed);
        patcher.Executed.Clear();
        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Empty(patcher.Executed);
    }

    private sealed class HistoryPatcher : IDBPatcher
    {
        public HashSet<(string, int)> Applied { get; } = [];
        public List<(string, int)> Executed { get; } = [];
        public bool IsConnected() => true;
        public void Init(string system) { }
        public bool Patch(string system, int patchId, string sql)
        {
            if (Applied.Add((system, patchId)))
            {
                Executed.Add((system, patchId));
            }
            return true;
        }
        public bool Successful() => true;
    }

    private sealed class SettingsPatcherStub(int failingPatch) : IDBPatcher
    {
        private bool _successful = true;
        public string? System { get; private set; }
        public List<string> Sql { get; } = [];
        public bool IsConnected() => true;
        public void Init(string system) => System = system;
        public bool Patch(string system, int patchId, string sql)
        {
            Assert.Equal(System, system);
            Sql.Add(sql);
            _successful = patchId != failingPatch;
            return _successful;
        }
        public bool Successful() => _successful;
    }
}
