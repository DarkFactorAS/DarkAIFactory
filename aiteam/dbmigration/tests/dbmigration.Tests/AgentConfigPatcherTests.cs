using dbmigration.Repository;
using DFCommonLib.DataAccess;
using System.Text.RegularExpressions;

namespace dbmigration.Tests;

public sealed class AgentConfigPatcherTests
{
    [Fact]
    public void Patcher_CreatesPrefixedIdempotentTablesAndSeedsDefaultAgent()
    {
        var patcher = new StubPatcher(0);
        Assert.True(AgentConfigPatcher.Run(patcher));
        Assert.Equal("AgentConfig", patcher.System);
        Assert.Equal(6, patcher.Sql.Count);

        var creates = patcher.Sql.Where(s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, creates.Count);
        foreach (var sql in creates)
        {
            Assert.Matches(@"^CREATE TABLE IF NOT EXISTS `new_\w+`", sql);
        }
        foreach (var sql in patcher.Sql)
        {
            foreach (Match m in Regex.Matches(sql, @"(?:TABLE IF NOT EXISTS|INSERT IGNORE INTO|REFERENCES) `(\w+)`"))
            {
                Assert.StartsWith("new_", m.Groups[1].Value);
            }
        }

        AssertColumns(patcher.Sql[0], "`name`", "`role`", "`instructions`", "`language_model_key`");
        AssertColumns(patcher.Sql[1], "`model_key`", "`enabled`");
        AssertColumns(patcher.Sql[2], "`agent_id`", "`capability`", "`enabled`");
        AssertColumns(patcher.Sql[3], "`agent_id`", "`tokens`", "`tool_calls`", "`duration_ms`",
            "`success_count`", "`failure_count`");
        Assert.Contains("REFERENCES `new_agents` (`id`)", patcher.Sql[2]);
        Assert.Contains("REFERENCES `new_agents` (`id`)", patcher.Sql[3]);

        Assert.StartsWith("INSERT IGNORE INTO `new_language_models`", patcher.Sql[4]);
        Assert.StartsWith("INSERT IGNORE INTO `new_agents`", patcher.Sql[5]);
        Assert.Contains("'default'", patcher.Sql[5]);
        Assert.Contains("'gpt-5-mini'", patcher.Sql[5]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    public void Patcher_PropagatesFailureAndStops(int failingPatch, int expectedCount)
    {
        var patcher = new StubPatcher(failingPatch);
        Assert.False(AgentConfigPatcher.Run(patcher));
        Assert.Equal(expectedCount, patcher.Sql.Count);
    }

    [Fact]
    public void Patcher_IsIdempotentWithMigrationHistory()
    {
        var applied = new HashSet<(string, int)>();
        var executed = 0;
        var patcher = new HistoryStub(applied, () => executed++);
        Assert.True(AgentConfigPatcher.Run(patcher));
        Assert.Equal(6, executed);
        Assert.True(AgentConfigPatcher.Run(patcher));
        Assert.Equal(6, executed);
    }

    private static void AssertColumns(string sql, params string[] columns)
    {
        foreach (var column in columns)
        {
            Assert.Contains(column, sql);
        }
    }

    private sealed class HistoryStub(HashSet<(string, int)> applied, Action onExecute) : IDBPatcher
    {
        public bool IsConnected() => true;
        public void Init(string system) { }
        public bool Patch(string system, int patchId, string sql)
        {
            if (applied.Add((system, patchId))) onExecute();
            return true;
        }
        public bool Successful() => true;
    }

    private sealed class StubPatcher(int failingPatch) : IDBPatcher
    {
        private bool _successful = true;
        public string? System { get; private set; }
        public List<string> Sql { get; } = [];
        public bool IsConnected() => true;
        public void Init(string system) => System = system;
        public bool Patch(string system, int patchId, string sql)
        {
            Sql.Add(sql);
            _successful = patchId != failingPatch;
            return _successful;
        }
        public bool Successful() => _successful;
    }
}
