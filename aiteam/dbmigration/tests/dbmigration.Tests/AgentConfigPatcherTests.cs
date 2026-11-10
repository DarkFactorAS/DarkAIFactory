using dbmigration.Repository;
using DFCommonLib.DataAccess;

namespace dbmigration.Tests;

public sealed class AgentConfigPatcherTests
{
    [Fact]
    public void Run_CreatesNewTablesIdempotentlyWithoutTouchingExistingOnes()
    {
        var patcher = new Stub(0);
        Assert.True(AgentConfigPatcher.Run(patcher));

        Assert.Equal("AgentConfig", patcher.System);
        Assert.Equal(6, patcher.Sql.Count);
        foreach (var table in new[] { "language_models", "agent_definitions", "agent_capabilities", "task_metrics" })
        {
            Assert.Contains(patcher.Sql.Take(4), sql => sql.Contains($"CREATE TABLE IF NOT EXISTS `{table}`"));
        }

        var all = string.Join('\n', patcher.Sql);
        Assert.DoesNotContain("ALTER TABLE", all);
        Assert.DoesNotContain("DROP ", all);
        Assert.DoesNotContain("DELETE FROM", all);
        foreach (var existing in new[] { "manager_projects", "manager_plans", "pull_requests", "settings", "workflow_labels", "agent_tasks", "logtable" })
        {
            Assert.DoesNotContain($"`{existing}`", all);
        }
    }

    [Fact]
    public void Run_DefinesRequiredColumnsAndConstraints()
    {
        var patcher = new Stub(0);
        AgentConfigPatcher.Run(patcher);

        Assert.Contains("`enabled` BOOLEAN", patcher.Sql[0]);
        Assert.Contains("`instructions` TEXT NOT NULL", patcher.Sql[1]);
        Assert.Contains("`role`", patcher.Sql[1]);
        Assert.Contains("FOREIGN KEY (`language_model_id`)", patcher.Sql[1]);
        Assert.Contains("`enabled` BOOLEAN", patcher.Sql[2]);
        Assert.Contains("UNIQUE KEY `uq_agent_capabilities_agent_name`", patcher.Sql[2]);
        Assert.Contains("`tokens_in` BIGINT NULL", patcher.Sql[3]);
        Assert.Contains("`tokens_out` BIGINT NULL", patcher.Sql[3]);
        Assert.Contains("`tool_call_count`", patcher.Sql[3]);
        Assert.Contains("`duration_ms`", patcher.Sql[3]);
        Assert.Contains("IN ('success', 'failed')", patcher.Sql[3]);
        Assert.Contains("`started`", patcher.Sql[3]);
        Assert.Contains("`completed`", patcher.Sql[3]);
    }

    [Fact]
    public void Run_SeedsOneDefaultAgentGuardedAgainstDuplicates()
    {
        var patcher = new Stub(0);
        AgentConfigPatcher.Run(patcher);

        Assert.Contains("WHERE NOT EXISTS", patcher.Sql[4]);
        Assert.Contains("'claude-sonnet-5-5'", patcher.Sql[4]);
        Assert.Contains("INSERT INTO `agent_definitions`", patcher.Sql[5]);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM `agent_definitions` WHERE `name` = 'default')", patcher.Sql[5]);
    }

    [Fact]
    public void Run_RecordsHistoryAndSecondRunChangesNothing()
    {
        var patcher = new Stub(0);
        Assert.True(AgentConfigPatcher.Run(patcher));
        Assert.Equal([1, 2, 3, 4, 5, 6], patcher.Ids);

        patcher.Sql.Clear();
        Assert.True(AgentConfigPatcher.Run(patcher));
        Assert.Empty(patcher.Sql);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Run_StopsOnFailure(int failingPatch)
    {
        var patcher = new Stub(failingPatch);
        Assert.False(AgentConfigPatcher.Run(patcher));
        Assert.Equal(failingPatch, patcher.Sql.Count);
    }

    private sealed class Stub(int failingPatch) : IDBPatcher
    {
        private readonly HashSet<int> _applied = [];
        private bool _successful = true;
        public string? System { get; private set; }
        public List<string> Sql { get; } = [];
        public List<int> Ids { get; } = [];
        public bool IsConnected() => true;
        public void Init(string system) => System = system;
        public bool Patch(string system, int patchId, string sql)
        {
            _successful = patchId != failingPatch;
            if (_successful && _applied.Add(patchId))
            {
                Sql.Add(sql);
                Ids.Add(patchId);
            }
            else if (!_successful)
            {
                Sql.Add(sql);
            }
            return _successful;
        }
        public bool Successful() => _successful;
    }
}
