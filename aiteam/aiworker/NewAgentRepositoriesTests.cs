using AiTeam.Configuration;
using AiTeam.Tests;

namespace CodingAgent.Tests;

public sealed class NewAgentRepositoriesTests
{
    [Fact]
    public void AgentConfig_UpsertWritesNullModelAsDbNull()
    {
        var database = new DatabaseStub();

        new NewAgentConfigRepository(database).Upsert("worker", "coder", "task", "behave", null);

        var command = Assert.Single(database.Commands);
        Assert.Contains("INSERT INTO new_agent_config", command.CommandText);
        Assert.Equal(DBNull.Value, command.Values["@language_model_id"]);
    }

    [Fact]
    public void Capabilities_SetEnabledUpsertsFlag()
    {
        var database = new DatabaseStub();

        new NewAgentCapabilityRepository(database).SetEnabled(2, "git", true);

        var command = Assert.Single(database.Commands);
        Assert.Contains("INSERT INTO new_agent_capabilities", command.CommandText);
        Assert.Equal(1, command.Values["@enabled"]);
    }

    [Theory]
    [InlineData(true, 1L, 0L)]
    [InlineData(false, 0L, 1L)]
    public void Metrics_RecordSplitsSuccessAndFailure(bool succeeded, long success, long failure)
    {
        var database = new DatabaseStub();

        new NewAgentMetricsRepository(database).Record(1, "K-1", 10, 20, 3, 400, succeeded);

        var command = Assert.Single(database.Commands);
        Assert.Contains("INSERT INTO new_agent_metrics", command.CommandText);
        Assert.Equal(success, command.Values["@success_count"]);
        Assert.Equal(failure, command.Values["@failure_count"]);
    }

    [Fact]
    public void LanguageModels_ListReadsRows()
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("id", typeof(long));
        database.Rows.Columns.Add("name", typeof(string));
        database.Rows.Columns.Add("enabled", typeof(bool));
        database.Rows.Rows.Add(1L, "gpt", true);

        var model = Assert.Single(new NewLanguageModelRepository(database).List(true));

        Assert.Equal(new NewLanguageModel(1, "gpt", true), model);
    }
}
