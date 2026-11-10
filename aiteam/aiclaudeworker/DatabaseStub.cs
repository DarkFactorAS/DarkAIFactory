using System.Data;
using DFCommonLib.DataAccess;

namespace AiTeam.Tests;

internal sealed class DatabaseStub : IDbConnectionFactory
{
    public DataTable Rows { get; } = new();
    public List<Command> Commands { get; } = [];
    public int AffectedRows { get; set; } = 1;
    public object? ScalarResult { get; set; }
    public Func<Command, int>? OnNonQuery { get; set; }
    public Func<Command, IDataReader>? OnReader { get; set; }

    public IDbConnection CreateConnection() => throw new NotSupportedException();
    public IBluDbCommand CreateCommand(string commandText)
    {
        var command = new Command(this, commandText);
        Commands.Add(command);
        return command;
    }
    public IBluDbCommand CreateCommand(string commandText, IDbConnection connection) => CreateCommand(commandText);

    internal sealed class Command(DatabaseStub database, string commandText) : IBluDbCommand
    {
        public Dictionary<string, object> Values { get; } = [];
        public bool BindByName { get; set; }
        public int ArrayBindCount { get; set; }
        public string CommandText { get; set; } = commandText;
        public CommandType CommandType { get; set; }
        public IDataParameterCollection Parameters => throw new NotSupportedException();
        public int ExecuteNonQuery() => database.OnNonQuery?.Invoke(this) ?? database.AffectedRows;
        public IDataReader ExecuteReader() => database.OnReader?.Invoke(this) ?? database.Rows.CreateDataReader();
        public object ExecuteScalar() => database.ScalarResult!;
        public TReturnType ExecuteScalar<TReturnType>() => throw new NotSupportedException();
        public void Prepare() => throw new NotSupportedException();
        public IDbDataParameter AddParameter(string name, DbType dbType) => throw new NotSupportedException();
        public IDbDataParameter AddParameter(IDbDataParameter parameter) => throw new NotSupportedException();
        public IDbDataParameter AddParameter(string name, object value)
        {
            Values.Add(name, value);
            return null!;
        }
        public IDbDataParameter AddParameter(string name, object value, DbType dbType, ParameterDirection direction) =>
            throw new NotSupportedException();
        public void AddClobParameter(string name, object value) => throw new NotSupportedException();
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
