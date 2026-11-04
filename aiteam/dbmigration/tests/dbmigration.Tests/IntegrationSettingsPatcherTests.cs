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

        Assert.Equal(22, values.Count);
        Assert.Contains("ON DUPLICATE KEY UPDATE `value` = `settings`.`value`", patcher.Sql[4]);
        Assert.Contains("DROP CONSTRAINT `ck_settings_scope`", patcher.Sql[3]);
        Assert.Contains("'GitHub.AgentModel'", patcher.Sql[3]);
        Assert.Contains("('aiworker', 'GitHub.AgentModel', 'gpt-5-mini')", patcher.Sql[4]);
        Assert.Equal(12, values.Keys.Count(key => key.Client == "shared"));
        Assert.Equal("ai-agent-failed", values[("shared", "Jira.JiraFailureLabel")]);
        Assert.Empty(values[("shared", "Jira.JiraAssigneeAccountId")]);
        Assert.Empty(values[("shared", "Jira.JiraApiToken")]);
        Assert.Empty(values[("shared", "GitHub.AgentGitHubToken")]);
        Assert.Equal("gpt-5-mini", values[("aiworker", "GitHub.AgentModel")]);
        foreach (var client in Clients)
        {
            Assert.Equal(client == "aiworker" ? 4 : 3, values.Keys.Count(key => key.Client == client));
            Assert.Equal("ai-analyzed", values[(client, "Jira.JiraAnalyzedLabel")]);
            Assert.Empty(values[(client, "GitHub.GitHubRepositoryToken")]);
            Assert.NotEmpty(values[(client, "Jira.JiraPickupLabel")]);
        }
    }

    [Theory]
    [InlineData(0, true, 5)]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    [InlineData(4, false, 4)]
    [InlineData(5, false, 5)]
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
            ("IntegrationSettings", 1),
            ("IntegrationSettings", 2),
            ("IntegrationSettings", 3),
            ("IntegrationSettings", 4),
            ("IntegrationSettings", 5)]);

        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Empty(patcher.Executed);
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
            ("IntegrationSettings", 2),
            ("IntegrationSettings", 3),
            ("IntegrationSettings", 4),
            ("IntegrationSettings", 5)], patcher.Executed);

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
            ("StartupDatabasePatcher", 4),
            ("IntegrationSettings", 1), ("IntegrationSettings", 2)]);

        Assert.True(new AiDatabasePatcher(patcher).RunPatcher());
        Assert.Equal([("IntegrationSettings", 3), ("IntegrationSettings", 4), ("IntegrationSettings", 5)], patcher.Executed);
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
