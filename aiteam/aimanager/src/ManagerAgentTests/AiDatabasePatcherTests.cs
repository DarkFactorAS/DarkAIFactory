using DFCommonLib.DataAccess;
using ManagerAgent.Repository;

namespace ManagerAgent.Tests;

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
            expectedCalls.AddRange(["patch:StartupDatabasePatcher:2", "successful"]);
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
        }
    }

    [Fact]
    public void RunPatcher_ReportsProjectTablePatchFailure()
    {
        var dbPatcher = new StubDbPatcher(true, failProjectPatch: true);

        Assert.False(new AiDatabasePatcher(dbPatcher).RunPatcher());
        Assert.Equal(2, dbPatcher.Sql.Count);
    }

    private sealed class StubDbPatcher(bool successful, bool failProjectPatch = false) : IDBPatcher
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
            if (patchId == 2 && failProjectPatch)
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
