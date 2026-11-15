using System.Data;
using AiAgent.Repositories;
using DFCommonLib.DataAccess;

namespace AiAgent.Tests;

/// <summary>
/// The claim and ownership locks must run RELEASE_LOCK on the connection that took the lock and then close it.
/// Only disposing the command left the lock held by a pooled session, so other agent processes could never claim it.
/// </summary>
public sealed class NamedLockTests
{
    [Fact]
    public void ClaimIsReleasedOnTheSameConnectionAndTheConnectionIsClosed()
    {
        var database = new FakeDatabase();
        var claim = new AgentClaimLock(database).TryAcquire(1, "DFAI-216");

        Assert.NotNull(claim);
        var connection = Assert.Single(database.Connections);
        Assert.True(connection.Opened);
        Assert.False(connection.Disposed);
        Assert.Equal(1, database.HeldBy(AgentClaimLock.LockName("DFAI-216")));

        claim.Dispose();

        Assert.Equal(
            [
                ("SELECT GET_LOCK(@lock_name, 0)", AgentClaimLock.LockName("DFAI-216")),
                ("SELECT RELEASE_LOCK(@lock_name)", AgentClaimLock.LockName("DFAI-216"))
            ],
            connection.Executed);
        Assert.True(connection.Disposed);
        Assert.Null(database.HeldBy(AgentClaimLock.LockName("DFAI-216")));
    }

    [Fact]
    public void ReleasedClaimCanBeTakenByAnotherProcess()
    {
        var database = new FakeDatabase();
        var first = new AgentClaimLock(database);
        var second = new AgentClaimLock(database);

        var held = first.TryAcquire(1, "DFAI-216");
        Assert.NotNull(held);
        Assert.Null(second.TryAcquire(2, "dfai-216"));

        held.Dispose();

        using var taken = second.TryAcquire(2, "DFAI-216");
        Assert.NotNull(taken);
    }

    [Fact]
    public void DisposingTwiceReleasesOnce()
    {
        var database = new FakeDatabase();
        var claim = new AgentClaimLock(database).TryAcquire(1, "DFAI-1")!;

        claim.Dispose();
        claim.Dispose();

        Assert.Equal(1, database.Connections[0].Executed.Count(sql => sql.Sql.Contains("RELEASE_LOCK")));
    }

    [Fact]
    public void BusyLockReturnsNullAndClosesItsConnection()
    {
        var database = new FakeDatabase();
        using var held = new AgentClaimLock(database).TryAcquire(1, "DFAI-1");

        Assert.Null(new AgentClaimLock(database).TryAcquire(2, "DFAI-1"));

        var refused = database.Connections[1];
        Assert.True(refused.Disposed);
        Assert.DoesNotContain(refused.Executed, sql => sql.Sql.Contains("RELEASE_LOCK"));
    }

    [Fact]
    public void FailedReleaseStillClosesTheConnection()
    {
        var database = new FakeDatabase { FailRelease = true };
        var claim = new AgentClaimLock(database).TryAcquire(1, "DFAI-1")!;

        claim.Dispose();

        Assert.True(database.Connections[0].Disposed);
    }

    [Fact]
    public void OwnershipLockIsReleasedOnDispose()
    {
        var database = new FakeDatabase();
        var ownership = new AgentOwnershipLock(database).TryAcquire(42);

        Assert.NotNull(ownership);
        Assert.Null(new AgentOwnershipLock(database).TryAcquire(42));

        ownership.Dispose();

        Assert.Null(database.HeldBy("aiagent-agent-42"));
        using var again = new AgentOwnershipLock(database).TryAcquire(42);
        Assert.NotNull(again);
    }

    [Fact]
    public void JiraIssueClaimIsReleasedOnDispose()
    {
        var database = new FakeDatabase();
        var repository = new JiraIssueClaimRepository(database);
        var claim = repository.TryAcquire("DFAI-5");

        Assert.NotNull(claim);
        Assert.Null(repository.TryAcquire("DFAI-5"));

        claim.Dispose();

        using var again = repository.TryAcquire("DFAI-5");
        Assert.NotNull(again);
    }

    /// <summary>Simulates MariaDB named locks: each lock is owned by one connection (session) until released.</summary>
    private sealed class FakeDatabase : IDbConnectionFactory
    {
        private readonly Dictionary<string, int> _locks = [];
        public List<FakeConnection> Connections { get; } = [];
        public bool FailRelease { get; init; }

        public int? HeldBy(string lockName) => _locks.TryGetValue(lockName, out var id) ? id : null;

        public IDbConnection CreateConnection()
        {
            var connection = new FakeConnection(Connections.Count + 1);
            Connections.Add(connection);
            return connection;
        }

        public IBluDbCommand CreateCommand(string commandText) =>
            throw new InvalidOperationException("Named locks must run on an explicit connection.");

        public IBluDbCommand CreateCommand(string commandText, IDbConnection connection) =>
            new FakeCommand(this, (FakeConnection)connection, commandText);

        public object Execute(FakeConnection connection, string sql, string lockName)
        {
            Assert.True(connection.Opened && !connection.Disposed, "The command ran on a closed connection.");
            connection.Executed.Add((sql, lockName));
            if (sql.Contains("GET_LOCK"))
            {
                if (_locks.TryGetValue(lockName, out var owner) && owner != connection.Id)
                {
                    return 0L;
                }

                _locks[lockName] = connection.Id;
                return 1L;
            }

            if (FailRelease)
            {
                throw new InvalidOperationException("connection lost");
            }

            if (!_locks.TryGetValue(lockName, out var holder))
            {
                return DBNull.Value;
            }

            if (holder != connection.Id)
            {
                return 0L;
            }

            _locks.Remove(lockName);
            return 1L;
        }
    }

    private sealed class FakeConnection(int id) : IDbConnection
    {
        public int Id { get; } = id;
        public bool Opened { get; private set; }
        public bool Disposed { get; private set; }
        public List<(string Sql, string LockName)> Executed { get; } = [];

        public ConnectionState State => Opened && !Disposed ? ConnectionState.Open : ConnectionState.Closed;
        public void Open() => Opened = true;
        public void Close() => Disposed = true;
        public void Dispose() => Disposed = true;

#pragma warning disable CS8767 // Nullability of the interface's setter.
        public string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8767
        public int ConnectionTimeout => 0;
        public string Database => "aiteam";
        public IDbTransaction BeginTransaction() => throw new NotSupportedException();
        public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
        public void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public IDbCommand CreateCommand() => throw new NotSupportedException();
    }

    private sealed class FakeCommand(FakeDatabase database, FakeConnection connection, string commandText) : IBluDbCommand
    {
        private string _lockName = string.Empty;

        public bool BindByName { get; set; }
        public int ArrayBindCount { get; set; }
        public string CommandText { get; set; } = commandText;
        public CommandType CommandType { get; set; }
        public IDataParameterCollection Parameters => throw new NotSupportedException();
        public object ExecuteScalar() => database.Execute(connection, CommandText, _lockName);
        public int ExecuteNonQuery() => throw new NotSupportedException();
        public IDataReader ExecuteReader() => throw new NotSupportedException();
        public TReturnType ExecuteScalar<TReturnType>() => throw new NotSupportedException();
        public void Prepare() => throw new NotSupportedException();
        public IDbDataParameter AddParameter(string name, DbType dbType) => throw new NotSupportedException();
        public IDbDataParameter AddParameter(IDbDataParameter parameter) => throw new NotSupportedException();

        public IDbDataParameter AddParameter(string name, object value)
        {
            Assert.Equal("@lock_name", name);
            _lockName = (string)value;
            return null!;
        }

        public IDbDataParameter AddParameter(string name, object value, DbType dbType, ParameterDirection direction) =>
            throw new NotSupportedException();

        public void AddClobParameter(string name, object value) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
