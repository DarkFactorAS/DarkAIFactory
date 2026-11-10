using AiTeam.Configuration;
using AiTeam.Tests;

namespace CodingAgent.Tests;

public sealed class AgentRepositoryTests
{
    [Fact]
    public void GetAgents_AttachesCapabilitiesToEachAgent()
    {
        var database = new DatabaseStub();
        var agents = new System.Data.DataTable();
        agents.Columns.Add("id", typeof(long));
        agents.Columns.Add("name", typeof(string));
        agents.Columns.Add("role", typeof(string));
        agents.Columns.Add("behavior_prompt", typeof(string));
        agents.Columns.Add("language_model", typeof(string));
        agents.Columns.Add("enabled", typeof(bool));
        agents.Rows.Add(1L, "dev", "Developer", "Be careful", "model-a", true);
        agents.Rows.Add(2L, "empty", "Reviewer", "Review", "model-a", false);
        var capabilities = new System.Data.DataTable();
        capabilities.Columns.Add("agent_id", typeof(long));
        capabilities.Columns.Add("capability", typeof(string));
        capabilities.Columns.Add("language_model", typeof(string));
        capabilities.Columns.Add("enabled", typeof(bool));
        capabilities.Rows.Add(1L, "read_files", "model-a", true);
        database.OnReader = command => command.CommandText.Contains("agent_capabilities")
            ? capabilities.CreateDataReader()
            : agents.CreateDataReader();

        var result = new AgentDefinitionRepository(database).GetAgents();

        Assert.Equal(2, result.Count);
        Assert.Equal(new AgentCapability("read_files", "model-a", true), Assert.Single(result[0].Capabilities));
        Assert.Empty(result[1].Capabilities);
    }

    [Theory]
    [InlineData(null, AgentRunStatus.Failed)]
    [InlineData(40L, AgentRunStatus.Success)]
    public void Record_BindsNullableTokensOutAndStatus(long? tokensOut, AgentRunStatus status)
    {
        var database = new DatabaseStub();

        new AgentMetricsRepository(database).Record(
            new AgentTaskMetric(1, "model-a", 10, tokensOut, 3, 1500, status));

        var values = Assert.Single(database.Commands).Values;
        Assert.Equal(tokensOut.HasValue ? tokensOut.Value : DBNull.Value, values["@output_tokens"]);
        Assert.Equal(status == AgentRunStatus.Success, values["@succeeded"]);
        Assert.Equal(DBNull.Value, values["@jira_key"]);
    }

    [Fact]
    public void GetForAgent_ReadsNullOutputTokensAndStatus()
    {
        var database = new DatabaseStub();
        foreach (var (name, type) in new (string, Type)[]
        {
            ("agent_id", typeof(long)), ("agent_task_id", typeof(long)), ("jira_key", typeof(string)),
            ("language_model", typeof(string)), ("input_tokens", typeof(long)), ("output_tokens", typeof(long)),
            ("tool_calls", typeof(int)), ("duration_ms", typeof(long)), ("succeeded", typeof(bool))
        })
        {
            database.Rows.Columns.Add(name, type);
        }

        database.Rows.Rows.Add(1L, DBNull.Value, DBNull.Value, "model-a", 10L, DBNull.Value, 2, 500L, false);

        var metric = Assert.Single(new AgentMetricsRepository(database).GetForAgent(1));

        Assert.Null(metric.TokensOut);
        Assert.Null(metric.JiraKey);
        Assert.Equal(AgentRunStatus.Failed, metric.Status);
    }
}
