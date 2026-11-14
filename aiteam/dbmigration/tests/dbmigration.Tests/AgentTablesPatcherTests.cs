using dbmigration.Repository;
using DFCommonLib.DataAccess;

namespace dbmigration.Tests;

public sealed class AgentTablesPatcherTests
{
    [Fact]
    public void Patcher_CreatesAllAgentTablesIdempotentlyWithAgentIdColumns()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(AgentTablesPatcher.Run(patcher));

        Assert.Equal("AgentTables", patcher.System);
        Assert.Equal(13, patcher.Sql.Count);
        string[] tables =
        [
            "agent_definitions", "agent_capabilities", "agent_projects",
            "agent_plans", "agent_pull_requests", "agent_review_attempts"
        ];
        for (var i = 0; i < tables.Length; i++)
        {
            Assert.Contains($"CREATE TABLE IF NOT EXISTS `{tables[i]}`", patcher.Sql[i]);
        }

        Assert.Contains("ENUM('Claude', 'Copilot')", patcher.Sql[0]);
        Assert.Contains("`deleted_at` DATETIME NULL", patcher.Sql[0]);
        Assert.Contains("ENUM('Jira', 'Planning', 'Builder', 'Review')", patcher.Sql[1]);
        Assert.Contains("PRIMARY KEY (`agent_id`, `capability`)", patcher.Sql[1]);
        Assert.Contains("PRIMARY KEY (`agent_id`, `project_key`)", patcher.Sql[2]);
        Assert.Contains("REFERENCES `manager_projects` (`jira_project_key`)", patcher.Sql[2]);
        foreach (var column in new[] { "`agent_id` BIGINT NOT NULL", "`plan` LONGTEXT NOT NULL",
            "`requirements_hash` CHAR(64) NULL", "`source_updated` VARCHAR(64) NULL" })
        {
            Assert.Contains(column, patcher.Sql[3]);
        }
        Assert.Contains("`agent_id` BIGINT NOT NULL", patcher.Sql[4]);
        Assert.Contains("UNIQUE KEY `uq_agent_pull_requests_pr_url`", patcher.Sql[4]);
        Assert.Contains("`agent_id` BIGINT NOT NULL", patcher.Sql[5]);
        Assert.Contains("REFERENCES `agent_pull_requests` (`id`) ON DELETE CASCADE", patcher.Sql[5]);
        Assert.DoesNotContain(patcher.Sql, sql => sql.Contains("DROP TABLE") || sql.Contains("`manager_plans`"));
    }

    [Fact]
    public void Patcher_SeedsDefaultAgentCapabilitiesSettingsAndIsInsertSafe()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(AgentTablesPatcher.Run(patcher));

        Assert.Contains("SELECT 'aiAgent'", patcher.Sql[6]);
        Assert.Contains("'Claude', FALSE", patcher.Sql[6]);
        Assert.Contains("WHERE NOT EXISTS", patcher.Sql[6]);
        Assert.Contains("INSERT IGNORE INTO `agent_capabilities`", patcher.Sql[7]);
        foreach (var capability in new[] { "'Jira'", "'Planning'", "'Builder'", "'Review'" })
        {
            Assert.Contains(capability, patcher.Sql[7]);
        }
        Assert.DoesNotContain("TRUE", patcher.Sql[7]);
        Assert.Contains("`client` = 'aiagent' AND `name` IN", patcher.Sql[8]);
        Assert.Contains("`client` = 'aiclaudeworker' AND `name` IN", patcher.Sql[8]);
        Assert.Contains("('aiagent', 'GitHub.GitHubRepositoryToken', '')", patcher.Sql[9]);
        Assert.Contains("('aiagent', 'Claude.AnthropicApiKey', '')", patcher.Sql[9]);
        Assert.Contains("ON DUPLICATE KEY UPDATE `value` = `settings`.`value`", patcher.Sql[9]);
    }

    [Fact]
    public void Patcher_SeedsDashboardAiLabels()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(AgentTablesPatcher.Run(patcher));

        Assert.Contains("'Dashboard.AiLabels'", patcher.Sql[11]);
        Assert.Contains("`client` = 'aidashboard' AND `name` = 'Dashboard.AiLabels'", patcher.Sql[11]);
        Assert.Contains("('aidashboard', 'Dashboard.AiLabels',", patcher.Sql[12]);
        Assert.Contains("aiagent-picked", patcher.Sql[12]);
    }

    [Fact]
    public void Patcher_AddsAgentStatusWithoutCascade()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(AgentTablesPatcher.Run(patcher));

        Assert.Equal(11, patcher.Numbers[10]);
        Assert.Equal(13, patcher.Numbers[^1]);
        var sql = patcher.Sql[10];
        Assert.Contains("CREATE TABLE IF NOT EXISTS `agent_status`", sql);
        Assert.Contains("ENUM('Idle', 'Running', 'Error', 'Disabled')", sql);
        Assert.Contains("`current_issue_key` VARCHAR(255) NULL", sql);
        Assert.Contains("`current_capability` ENUM('Jira', 'Planning', 'Builder', 'Review') NULL", sql);
        Assert.Contains("`heartbeat_at` DATETIME NULL", sql);
        Assert.Contains("PRIMARY KEY (`agent_id`)", sql);
        Assert.Contains("REFERENCES `agent_definitions` (`id`)", sql);
        Assert.DoesNotContain("ON DELETE CASCADE", sql);
    }

    [Fact]
    public void WorkflowLabelPatcher_SeedsAiAgentLabels()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(WorkflowLabelPatcher.Run(patcher));
        var sql = patcher.Sql[4];
        foreach (var label in new[] { "aiagent-picked", "aiagent-failed", "aiagent-analyzed",
            "aiagent-planned", "aiagent-ignored" })
        {
            Assert.Contains($"'{label}'", sql);
        }
        Assert.Contains("ON DUPLICATE KEY UPDATE", sql);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(11)]
    public void Patcher_StopsAtFailingPatch(int failingPatch)
    {
        var patcher = new RecordingPatcher(failingPatch);
        Assert.False(AgentTablesPatcher.Run(patcher));
        Assert.Equal(failingPatch, patcher.Sql.Count);
    }

    private sealed class RecordingPatcher(int failingPatch) : IDBPatcher
    {
        private bool _successful = true;
        public string System { get; private set; } = "";
        public List<string> Sql { get; } = [];
        public List<int> Numbers { get; } = [];
        public bool IsConnected() => true;
        public void Init(string system) => System = system;
        public bool Patch(string system, int patchId, string sql)
        {
            Sql.Add(sql);
            Numbers.Add(patchId);
            _successful = patchId != failingPatch;
            return _successful;
        }
        public bool Successful() => _successful;
    }
}
