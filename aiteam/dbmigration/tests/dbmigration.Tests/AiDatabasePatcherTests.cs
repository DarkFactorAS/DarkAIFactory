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
                "init:IntegrationSettings",
                "patch:IntegrationSettings:1", "successful",
                "patch:IntegrationSettings:2", "successful"]);
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
            Assert.Contains("CREATE TABLE IF NOT EXISTS `settings`", dbPatcher.Sql[4]);
            Assert.Contains("CONSTRAINT `ck_settings_scope` CHECK", dbPatcher.Sql[4]);
            Assert.Contains("PRIMARY KEY (`client`, `name`)", dbPatcher.Sql[4]);
            Assert.Contains("('shared', 'Jira.JiraFailureLabel', 'ai-agent-failed')", dbPatcher.Sql[5]);
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
    [InlineData(1, 5)]
    [InlineData(2, 6)]
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

    private sealed class StubDbPatcher(
        bool successful, bool failProjectPatch = false, bool failPlanPatch = false,
        int failingSettingsPatch = 0, bool failFreshnessPatch = false) : IDBPatcher
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
                 ((patchId == 2 && failProjectPatch) || (patchId == 3 && failPlanPatch) || (patchId == 4 && failFreshnessPatch))) ||
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
