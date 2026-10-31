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
        Assert.Equal(
            new[] { "connected", "init:StartupDatabasePatcher", "patch:StartupDatabasePatcher:1", "successful" },
            dbPatcher.Calls);
        Assert.Contains("CREATE TABLE `logtable`", dbPatcher.Sql);
    }

    private sealed class StubDbPatcher(bool successful) : IDBPatcher
    {
        public List<string> Calls { get; } = [];
        public string Sql { get; private set; } = string.Empty;

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
            Sql = sql;
            return successful;
        }

        public bool Successful()
        {
            Calls.Add("successful");
            return successful;
        }
    }
}
