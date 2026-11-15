using dbmigration.Repository;
using DFCommonLib.DataAccess;

namespace dbmigration.Tests;

public sealed class LegacyAgentCleanupPatcherTests
{
    [Fact]
    public void Patcher_DropsLegacyTablesIdempotentlyInDependencyOrder()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(LegacyAgentCleanupPatcher.Run(patcher));

        Assert.Equal("LegacyAgentCleanup", patcher.System);
        Assert.Equal(Enumerable.Range(1, 8), patcher.Numbers);
        Assert.Equal("DROP TABLE IF EXISTS `review_comment_attempts`", patcher.Sql[0]);
        Assert.Equal("DROP TABLE IF EXISTS `pull_requests`", patcher.Sql[1]);
        Assert.Equal("DROP TABLE IF EXISTS `manager_plans`", patcher.Sql[2]);
    }

    [Fact]
    public void Patcher_KeepsTablesStillInUse()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(LegacyAgentCleanupPatcher.Run(patcher));

        Assert.DoesNotContain(patcher.Sql, sql => sql.Contains("DROP TABLE") && (
            sql.Contains("manager_projects") || sql.Contains("agent_") || sql.Contains("logtable")
            || sql.Contains("`settings`") || sql.Contains("workflow_labels")));
    }

    [Fact]
    public void Patcher_DeletesLegacyLabelsAndSettings()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(LegacyAgentCleanupPatcher.Run(patcher));

        Assert.Contains("DELETE FROM `workflow_labels`", patcher.Sql[3]);
        Assert.Contains("'aimanager', 'aiworker', 'aiclaudeworker'", patcher.Sql[3]);
        Assert.DoesNotContain("'aiagent'", patcher.Sql[3]);
        Assert.Contains("DELETE FROM `settings`", patcher.Sql[4]);
        Assert.Contains("'aimanager', 'aiworker', 'aiclaudeworker', 'aibuildpage'", patcher.Sql[4]);
        Assert.Contains("`client` = 'aidashboard'", patcher.Sql[5]);
        foreach (var name in new[] { "Jira.JiraPickupLabel", "Jira.JiraAnalyzedLabel", "GitHub.GitHubRepositoryToken", "GitHub.AgentModel" })
        {
            Assert.Contains($"'{name}'", patcher.Sql[5]);
        }
    }

    [Fact]
    public void Patcher_ReplacesScopeConstraintWithoutLegacyClients()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(LegacyAgentCleanupPatcher.Run(patcher));

        var sql = patcher.Sql[6];
        Assert.Contains("DROP CONSTRAINT `ck_settings_scope`", sql);
        Assert.Contains("ADD CONSTRAINT `ck_settings_scope` CHECK", sql);
        Assert.Contains("`client` = 'aidashboard' AND `name` IN ('Dashboard.AiLabels', 'Dashboard.ActiveStatuses')", sql);
        Assert.Contains("`client` = 'aiagent' AND `name` IN", sql);
        Assert.DoesNotContain("aimanager", sql);
        Assert.DoesNotContain("aiworker", sql);
        Assert.DoesNotContain("aiclaudeworker", sql);
    }

    [Fact]
    public void Patcher_RewritesDashboardAiLabelsKeepingOtherLabels()
    {
        var patcher = new RecordingPatcher(0);
        Assert.True(LegacyAgentCleanupPatcher.Run(patcher));

        var sql = patcher.Sql[7];
        Assert.StartsWith("UPDATE `settings`", sql);
        Assert.Contains("ai-manager-planned|ai-analyzed|ai-agent-picked", sql);
        Assert.Contains("`client` = 'aidashboard' AND `name` = 'Dashboard.AiLabels'", sql);
        Assert.Contains("`agent_name` = 'aiagent'", sql);
        Assert.Contains("'aiagent-picked,aiagent-analyzed,aiagent-planned'", sql);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Patcher_StopsAtFailingPatch(int failingPatch)
    {
        var patcher = new RecordingPatcher(failingPatch);
        Assert.False(LegacyAgentCleanupPatcher.Run(patcher));
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
