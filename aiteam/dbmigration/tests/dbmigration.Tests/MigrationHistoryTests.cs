using System.Data;
using dbmigration.Repository;
using DFCommonLib.DataAccess;

namespace dbmigration.Tests;

public sealed class MigrationHistoryTests
{
    [Fact]
    public void Initialize_CreatesHistoryTableWithCompositePrimaryKey()
    {
        var database = new MigrationDatabase();

        MigrationHistory.Initialize(database);

        Assert.True(database.TableExists);
        Assert.Equal(["system", "patchId"], database.PrimaryKeyColumns);
        Assert.Contains(database.Commands, command => command.StartsWith("CREATE TABLE IF NOT EXISTS"));
    }

    [Fact]
    public void Initialize_UpgradesLegacyPrimaryKeyAndPreservesRows()
    {
        var database = new MigrationDatabase(["patchId"]);
        database.Rows.Add(("StartupDatabasePatcher", 1));

        MigrationHistory.Initialize(database);

        Assert.Equal(["system", "patchId"], database.PrimaryKeyColumns);
        Assert.Equal([("StartupDatabasePatcher", 1)], database.Rows);
        Assert.Contains(database.Commands, command => command.StartsWith("ALTER TABLE"));
    }

    [Fact]
    public void Initialize_IsSafeToRepeat()
    {
        var database = new MigrationDatabase();

        MigrationHistory.Initialize(database);
        MigrationHistory.Initialize(database);

        Assert.Equal(["system", "patchId"], database.PrimaryKeyColumns);
        Assert.DoesNotContain(database.Commands, command => command.StartsWith("ALTER TABLE"));
    }

    [Fact]
    public void Initialize_RejectsUnexpectedPrimaryKey()
    {
        var database = new MigrationDatabase(["system"]);

        Assert.Throws<InvalidOperationException>(() => MigrationHistory.Initialize(database));
    }

    [Fact]
    public void Initialize_UpgradeKeepsRealPatchBookkeepingFromReapplyingAppliedPatch()
    {
        var database = new MigrationDatabase(["patchId"]);
        database.Rows.Add(("StartupDatabasePatcher", 1));

        MigrationHistory.Initialize(database);
        var patcher = new MySQLDBPatcher(database);
        patcher.Init("StartupDatabasePatcher");

        Assert.False(patcher.Patch("StartupDatabasePatcher", 1, "SHOULD NOT RUN"));
        Assert.Equal([("StartupDatabasePatcher", 1)], database.Rows);
        Assert.DoesNotContain(database.Commands, command => command == "SHOULD NOT RUN");
    }

    private sealed class MigrationDatabase : IDbConnectionFactory
    {
        public MigrationDatabase(string[]? primaryKeyColumns = null)
        {
            TableExists = primaryKeyColumns is not null;
            PrimaryKeyColumns = primaryKeyColumns ?? [];
        }

        public bool TableExists { get; set; }
        public string[] PrimaryKeyColumns { get; set; }
        public List<(string System, int PatchId)> Rows { get; } = [];
        public List<string> Commands { get; } = [];

        public IDbConnection CreateConnection() => throw new NotSupportedException();

        public IBluDbCommand CreateCommand(string commandText) => new MigrationCommand(this, commandText);

        public IBluDbCommand CreateCommand(string commandText, IDbConnection connection) =>
            new MigrationCommand(this, commandText);

        public IDataReader ExecuteReader(MigrationCommand command)
        {
            var result = new DataTable();
            if (command.CommandText.StartsWith("SELECT COLUMN_NAME"))
            {
                result.Columns.Add("COLUMN_NAME", typeof(string));
                foreach (var column in PrimaryKeyColumns)
                {
                    result.Rows.Add(column);
                }
            }
            else if (command.CommandText.StartsWith("SELECT database()"))
            {
                result.Columns.Add("db", typeof(string));
                result.Rows.Add("test");
            }
            else if (command.CommandText.StartsWith("SELECT * FROM information_schema.tables"))
            {
                result.Columns.Add("table_name", typeof(string));
                if (TableExists)
                {
                    result.Rows.Add("version");
                }
            }
            else if (command.CommandText.StartsWith("select patchId from version"))
            {
                result.Columns.Add("patchId", typeof(int));
                var system = (string)command.ParametersByName["@system"];
                var patchId = (int)command.ParametersByName["@patchId"];
                if (Rows.Contains((system, patchId)))
                {
                    result.Rows.Add(patchId);
                }
            }
            else
            {
                throw new NotSupportedException(command.CommandText);
            }

            return result.CreateDataReader();
        }

        public int ExecuteNonQuery(MigrationCommand command)
        {
            if (command.CommandText.StartsWith("CREATE TABLE IF NOT EXISTS"))
            {
                TableExists = true;
                if (PrimaryKeyColumns.Length == 0)
                {
                    PrimaryKeyColumns = ["system", "patchId"];
                }
            }
            else if (command.CommandText.StartsWith("ALTER TABLE"))
            {
                PrimaryKeyColumns = ["system", "patchId"];
            }
            else if (command.CommandText.StartsWith("insert into version"))
            {
                Rows.Add(((string)command.ParametersByName["@system"], (int)command.ParametersByName["@patchId"]));
            }

            return 1;
        }
    }

    private sealed class MigrationCommand(MigrationDatabase database, string commandText) : IBluDbCommand
    {
        public Dictionary<string, object> ParametersByName { get; } = [];
        public bool BindByName { get; set; }
        public int ArrayBindCount { get; set; }
        public string CommandText { get; set; } = commandText;
        public CommandType CommandType { get; set; }
        public IDataParameterCollection Parameters => throw new NotSupportedException();

        public int ExecuteNonQuery()
        {
            database.Commands.Add(CommandText);
            return database.ExecuteNonQuery(this);
        }

        public object ExecuteScalar() => throw new NotSupportedException();
        public void Prepare() => throw new NotSupportedException();
        public IDbDataParameter AddParameter(string name, DbType dbType) => throw new NotSupportedException();
        public IDbDataParameter AddParameter(IDbDataParameter parameter) => throw new NotSupportedException();

        public IDbDataParameter AddParameter(string name, object value)
        {
            ParametersByName[name] = value;
            return null!;
        }

        public IDbDataParameter AddParameter(
            string name, object value, DbType dbType, ParameterDirection direction) =>
            throw new NotSupportedException();

        public void AddClobParameter(string name, object value) => throw new NotSupportedException();
        public TReturnType ExecuteScalar<TReturnType>() => throw new NotSupportedException();

        public IDataReader ExecuteReader()
        {
            database.Commands.Add(CommandText);
            return database.ExecuteReader(this);
        }

        public void Dispose()
        {
        }
    }
}
