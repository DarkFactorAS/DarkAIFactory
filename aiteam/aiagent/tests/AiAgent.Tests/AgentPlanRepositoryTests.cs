using System.Data;
using AiAgent.Repositories;
using DFCommonLib.DataAccess;

namespace AiAgent.Tests;

public sealed class AgentPlanRepositoryTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void SaveSendsPostedToJiraAsAnInteger(bool posted, int expected)
    {
        // DFCommonLib sends a bool as 'True'/'False', which MariaDB rejects for the TINYINT posted_to_jira column.
        var database = new CommandCapture();
        new AgentPlanRepository(database).Save(new AgentPlan(7, 5, "DFAI-1", "plan", posted, "hash", "u"));

        var value = Assert.Single(database.Commands).Values["@posted"];
        Assert.IsType<int>(value);
        Assert.Equal(expected, value);
    }

    private sealed class CommandCapture : IDbConnectionFactory
    {
        public List<Command> Commands { get; } = [];

        public IDbConnection CreateConnection() => throw new NotSupportedException();

        public IBluDbCommand CreateCommand(string commandText)
        {
            var command = new Command(commandText);
            Commands.Add(command);
            return command;
        }

        public IBluDbCommand CreateCommand(string commandText, IDbConnection connection) => CreateCommand(commandText);

        internal sealed class Command(string commandText) : IBluDbCommand
        {
            public Dictionary<string, object> Values { get; } = [];
            public bool BindByName { get; set; }
            public int ArrayBindCount { get; set; }
            public string CommandText { get; set; } = commandText;
            public CommandType CommandType { get; set; }
            public IDataParameterCollection Parameters => throw new NotSupportedException();
            public int ExecuteNonQuery() => 1;
            public IDataReader ExecuteReader() => throw new NotSupportedException();
            public object ExecuteScalar() => throw new NotSupportedException();
            public TReturnType ExecuteScalar<TReturnType>() => throw new NotSupportedException();
            public void Prepare() => throw new NotSupportedException();
            public IDbDataParameter AddParameter(string name, DbType dbType) => throw new NotSupportedException();
            public IDbDataParameter AddParameter(IDbDataParameter parameter) => throw new NotSupportedException();

            public IDbDataParameter AddParameter(string name, object value)
            {
                Values[name] = value;
                return null!;
            }

            public IDbDataParameter AddParameter(string name, object value, DbType dbType, ParameterDirection direction) =>
                throw new NotSupportedException();

            public void AddClobParameter(string name, object value) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
