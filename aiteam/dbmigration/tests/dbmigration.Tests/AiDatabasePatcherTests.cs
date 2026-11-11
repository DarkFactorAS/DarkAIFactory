using DFCommonLib.DataAccess;
using dbmigration.Repository;

namespace dbmigration.Tests;

public sealed class AiDatabasePatcherTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunPatcher_AppliesBasePatchesAndReportsResult(bool successful)
    {
        var dbPatcher = new StubDbPatcher(successful);
        var patcher = new AiDatabasePatcher(dbPatcher);

        patcher.WaitForConnection();
        var result = patcher.RunPatcher();

        Assert.Equal(successful, result);
        var expectedCalls = new List<string>
        {
            "connected", "init:StartupDatabasePatcher", "patch:StartupDatabasePatcher:1", "successful"
        };
        if (successful)
        {
            expectedCalls.AddRange([
                "patch:StartupDatabasePatcher:2", "successful",
                "patch:StartupDatabasePatcher:3", "successful",
                "patch:StartupDatabasePatcher:4", "successful",
                "patch:StartupDatabasePatcher:5", "successful",
                "init:IntegrationSettings",
                "patch:IntegrationSettings:1", "successful",
                "patch:IntegrationSettings:2", "successful",
                "patch:IntegrationSettings:3", "successful",
                "patch:IntegrationSettings:4", "successful",
                "patch:IntegrationSettings:5", "successful",
                "patch:IntegrationSettings:6", "successful",
                "patch:IntegrationSettings:7", "successful",
                "patch:IntegrationSettings:8", "successful",
                "patch:IntegrationSettings:9", "successful",
                "patch:IntegrationSettings:10", "successful",
                "patch:IntegrationSettings:11", "successful",
                "init:WorkflowLabels",
                "patch:WorkflowLabels:1", "successful",
                "patch:WorkflowLabels:2", "successful",
                "patch:WorkflowLabels:3", "successful",
                "patch:WorkflowLabels:4", "successful",
                "init:AgentTasks",
                "patch:AgentTasks:1", "successful",
                "init:AgentConfig",
                "patch:AgentConfig:1", "successful",
                "patch:AgentConfig:2", "successful",
                "patch:AgentConfig:3", "successful",
                "patch:AgentConfig:4", "successful",
                "patch:AgentConfig:5", "successful",
                "patch:AgentConfig:6", "successful",
                "patch:AgentConfig:7", "successful",
                "patch:AgentConfig:8", "successful",
                "patch:AgentConfig:9", "successful",
                "patch:AgentConfig:10", "successful",
                "patch:AgentConfig:11", "successful",
                "init:ReviewCommentAttempts",
                "patch:ReviewCommentAttempts:1", "successful"]);
        }

        Assert.Equal(expectedCalls, dbPatcher.Calls);
        Assert.Contains("CREATE TABLE `logtable`", dbPatcher.Sql[0]);
        if (successful)
        {
            Assert.Contains("CREATE TABLE `manager_projects`", dbPatcher.Sql[1]);
            Assert.Contains("`id` BIGINT NOT NULL AUTO_INCREMENT", dbPatcher.Sql[1]);
            Assert.Contains("PRIMARY KEY (`id`)", dbPatcher.Sql[1]);
            Assert.Contains("`jira_project_key`", dbPatcher.Sql[1]);
            Assert.Contains("`target_repository_url`", dbPatcher.Sql[1]);
            Assert.Contains("CREATE TABLE `manager_plans`", dbPatcher.Sql[2]);
            Assert.Contains("`jira_key` VARCHAR(255) NOT NULL", dbPatcher.Sql[2]);
            Assert.Contains("`plan` LONGTEXT NOT NULL", dbPatcher.Sql[2]);
            Assert.Contains("UNIQUE KEY `uq_manager_plans_jira_key`", dbPatcher.Sql[2]);
            Assert.Contains("ALTER TABLE `manager_plans`", dbPatcher.Sql[3]);
            Assert.Contains("ADD COLUMN IF NOT EXISTS `requirements_hash` CHAR(64) NULL", dbPatcher.Sql[3]);
            Assert.Contains("ADD COLUMN IF NOT EXISTS `source_updated` VARCHAR(64) NULL", dbPatcher.Sql[3]);
            Assert.DoesNotContain("DELETE", dbPatcher.Sql[3]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `pull_requests`", dbPatcher.Sql[4]);
            Assert.Contains("`jira_key` VARCHAR(255) NOT NULL", dbPatcher.Sql[4]);
            Assert.Contains("`pr_number` BIGINT NOT NULL", dbPatcher.Sql[4]);
            Assert.Contains("UNIQUE KEY `uq_pull_requests_pr_url`", dbPatcher.Sql[4]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `settings`", dbPatcher.Sql[5]);
            Assert.Contains("CONSTRAINT `ck_settings_scope` CHECK", dbPatcher.Sql[5]);
            Assert.Contains("PRIMARY KEY (`client`, `name`)", dbPatcher.Sql[5]);
            Assert.DoesNotContain("Label", dbPatcher.Sql[6]);
            Assert.Contains("('shared', 'Jira.JiraAssigneeAccountId', '')", dbPatcher.Sql[7]);
            Assert.Contains("DROP CONSTRAINT `ck_settings_scope`", dbPatcher.Sql[8]);
            Assert.Contains("('aiworker', 'GitHub.AgentModel', 'gpt-5-mini')", dbPatcher.Sql[9]);
            Assert.Contains("'aidashboard'", dbPatcher.Sql[10]);
            Assert.Contains("UPDATE IGNORE `settings` SET `client` = 'aidashboard'", dbPatcher.Sql[11]);
            Assert.Contains("DELETE FROM `settings` WHERE `client` = 'aibuildpage'", dbPatcher.Sql[12]);
            Assert.Contains("'aidashboard'", dbPatcher.Sql[13]);
            Assert.Contains("`client` = 'aiclaudeworker' AND `name` IN", dbPatcher.Sql[14]);
            Assert.Contains("('aiclaudeworker', 'Claude.AgentModel', 'claude-sonnet-5-5')", dbPatcher.Sql[15]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `workflow_labels`", dbPatcher.Sql[16]);
            Assert.Contains("INSERT INTO `workflow_labels`", dbPatcher.Sql[17]);
            Assert.Contains("DELETE FROM `settings`", dbPatcher.Sql[18]);
            Assert.Contains("SELECT 'aiclaudeworker', `code_key`, `label_name`, `description`", dbPatcher.Sql[19]);
            Assert.Contains("WHERE `agent_name` = 'aiworker'", dbPatcher.Sql[19]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `agent_tasks`", dbPatcher.Sql[20]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `agents`", dbPatcher.Sql[21]);
            Assert.Contains("`behavior_prompt` LONGTEXT NOT NULL", dbPatcher.Sql[21]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `agent_capabilities`", dbPatcher.Sql[22]);
            Assert.Contains("PRIMARY KEY (`agent_id`, `capability`, `language_model`)", dbPatcher.Sql[22]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `agent_task_metrics`", dbPatcher.Sql[23]);
            Assert.Contains("`input_tokens`", dbPatcher.Sql[23]);
            Assert.Contains("`tool_calls`", dbPatcher.Sql[23]);
            Assert.Contains("`duration_ms`", dbPatcher.Sql[23]);
            Assert.Contains("CREATE TABLE IF NOT EXISTS `agent_metrics_totals`", dbPatcher.Sql[24]);
            Assert.Contains("`tasks_succeeded`", dbPatcher.Sql[24]);
            Assert.Contains("`tasks_failed`", dbPatcher.Sql[24]);
            Assert.Contains("WHERE NOT EXISTS", dbPatcher.Sql[25]);
            Assert.Contains("'sample-agent'", dbPatcher.Sql[25]);
            Assert.Contains("INSERT IGNORE INTO `agent_capabilities`", dbPatcher.Sql[26]);
            Assert.Contains("INSERT IGNORE INTO `agent_metrics_totals`", dbPatcher.Sql[27]);
            Assert.Contains("ALTER TABLE `agent_task_metrics`", dbPatcher.Sql[28]);
            Assert.Contains("MODIFY COLUMN `input_tokens` BIGINT NULL DEFAULT NULL", dbPatcher.Sql[28]);
            Assert.Contains("MODIFY COLUMN `output_tokens` BIGINT NULL DEFAULT NULL", dbPatcher.Sql[28]);
            Assert.DoesNotContain("NOT NULL", dbPatcher.Sql[28]);
            Assert.Contains("ALTER TABLE `agent_task_metrics`", dbPatcher.Sql[29]);
            Assert.Contains("ADD COLUMN IF NOT EXISTS `status` ENUM('success', 'failed') NULL", dbPatcher.Sql[29]);
            Assert.DoesNotContain("NOT NULL", dbPatcher.Sql[29]);
            Assert.Contains("UPDATE `agent_task_metrics`", dbPatcher.Sql[30]);
            Assert.Contains("SET `status` = IF(`succeeded`, 'success', 'failed')", dbPatcher.Sql[30]);
            Assert.Contains("WHERE `status` IS NULL", dbPatcher.Sql[30]);
            Assert.Contains("ALTER TABLE `agent_task_metrics`", dbPatcher.Sql[31]);
            Assert.Contains("MODIFY COLUMN `status` ENUM('success', 'failed') NOT NULL", dbPatcher.Sql[31]);
        }
    }

    [Fact]
    public void RunPatcher_ReportsProjectTablePatchFailure()
    {
        var dbPatcher = new StubDbPatcher(true, failProjectPatch: true);

        Assert.False(new AiDatabasePatcher(dbPatcher).RunPatcher());
        Assert.Equal(2, dbPatcher.Sql.Count);
    }

    [Fact]
    public void RunPatcher_ReportsPlanTablePatchFailure()
    {
        var dbPatcher = new StubDbPatcher(true, failPlanPatch: true);

        Assert.False(new AiDatabasePatcher(dbPatcher).RunPatcher());
        Assert.Equal(3, dbPatcher.Sql.Count);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 7)]
    [InlineData(3, 8)]
    [InlineData(4, 9)]
    [InlineData(5, 10)]
    public void RunPatcher_ReportsSettingsPatchFailure(int patchId, int expectedPatchCount)
    {
        var dbPatcher = new StubDbPatcher(true, failingSettingsPatch: patchId);

        Assert.False(new AiDatabasePatcher(dbPatcher).RunPatcher());
        Assert.Equal(expectedPatchCount, dbPatcher.Sql.Count);
    }

    [Fact]
    public void RunPatcher_StopsWhenFreshnessMigrationFails()
    {
        var dbPatcher = new StubDbPatcher(true, failFreshnessPatch: true);

        Assert.False(new AiDatabasePatcher(dbPatcher).RunPatcher());
        Assert.Equal(4, dbPatcher.Sql.Count);
        Assert.DoesNotContain(dbPatcher.Calls, call => call.StartsWith("init:IntegrationSettings"));
    }

    [Fact]
    public void RunPatcher_StopsWhenPullRequestMigrationFails()
    {
        var dbPatcher = new StubDbPatcher(true, failPullRequestPatch: true);

        Assert.False(new AiDatabasePatcher(dbPatcher).RunPatcher());
        Assert.Equal(5, dbPatcher.Sql.Count);
        Assert.DoesNotContain(dbPatcher.Calls, call => call.StartsWith("init:IntegrationSettings"));
    }

    private sealed class StubDbPatcher(
        bool successful, bool failProjectPatch = false, bool failPlanPatch = false,
        int failingSettingsPatch = 0, bool failFreshnessPatch = false, bool failPullRequestPatch = false) : IDBPatcher
    {
        private bool _successful = successful;
        public List<string> Calls { get; } = [];
        public List<string> Sql { get; } = [];

        public bool IsConnected()
        {
            Calls.Add("connected");
            return true;
        }

        public void Init(string system)
        {
            Calls.Add($"init:{system}");
        }

        public bool Patch(string system, int patchId, string sql)
        {
            Calls.Add($"patch:{system}:{patchId}");
            Sql.Add(sql);
            if ((system == "StartupDatabasePatcher" &&
                 ((patchId == 2 && failProjectPatch) || (patchId == 3 && failPlanPatch) || (patchId == 4 && failFreshnessPatch) || (patchId == 5 && failPullRequestPatch))) ||
                (system == "IntegrationSettings" && patchId == failingSettingsPatch))
            {
                _successful = false;
            }

            return _successful;
        }

        public bool Successful()
        {
            Calls.Add("successful");
            return _successful;
        }
    }
}
