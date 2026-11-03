using AiTeam.Configuration;
using AiTeam.Tests;
using ManagerAgent.Repository;

namespace ManagerAgent.Tests;

public sealed class ManagerPlanRepositoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("requirements-hash")]
    public void Find_ReadsFreshnessMetadataIncludingLegacyRows(string? hash)
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("manager_project_id", typeof(long));
        database.Rows.Columns.Add("plan", typeof(string));
        database.Rows.Columns.Add("posted_to_jira", typeof(bool));
        database.Rows.Columns.Add("requirements_hash", typeof(string));
        database.Rows.Columns.Add("source_updated", typeof(string));
        database.Rows.Rows.Add(3L, "Plan", false, (object?)hash ?? DBNull.Value, "2026-11-02T12:00:00Z");

        Assert.Equal(new ManagerPlan("Plan", false, hash, "2026-11-02T12:00:00Z", 3),
            new ManagerPlanRepository(database).Find("TASK-1"));
    }

    [Fact]
    public void RebindProject_UpdatesPlanAssociation()
    {
        var database = new DatabaseStub { AffectedRows = 1 };

        new ManagerPlanRepository(database).RebindProject(4, "TASK-1");

        var command = Assert.Single(database.Commands);
        Assert.Contains("SET manager_project_id = @project_id", command.CommandText);
        Assert.Equal(4L, command.Values["@project_id"]);
        Assert.Equal("TASK-1", command.Values["@jira_key"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Save_InsertsOrReplacesPlanAndResetsPostingState(int affectedRows)
    {
        var database = new DatabaseStub { AffectedRows = affectedRows };

        new ManagerPlanRepository(database).Save(3, "TASK-1", "New plan", "hash", "updated");

        var command = Assert.Single(database.Commands);
        Assert.Contains("ON DUPLICATE KEY UPDATE", command.CommandText);
        Assert.Contains("posted_to_jira = 0", command.CommandText);
        Assert.Equal(3L, command.Values["@project_id"]);
        Assert.Equal("hash", command.Values["@requirements_hash"]);
        Assert.Equal("updated", command.Values["@source_updated"]);
    }

    [Fact]
    public void Save_ReportsFailedPersistence()
    {
        var database = new DatabaseStub { AffectedRows = 0 };

        Assert.Throws<InvalidOperationException>(() =>
            new ManagerPlanRepository(database).Save(1, "TASK-1", "Plan", "hash", "updated"));
    }

    [Fact]
    public void MarkPostedToJira_OnlyUpdatesMatchingPlanVersion()
    {
        var database = new DatabaseStub();
        var repository = new ManagerPlanRepository(database);

        repository.MarkPostedToJira("TASK-1", "hash");

        var command = Assert.Single(database.Commands);
        Assert.Contains("AND requirements_hash = @requirements_hash", command.CommandText);
        Assert.Equal("hash", command.Values["@requirements_hash"]);
        database.AffectedRows = 0;
        Assert.Throws<InvalidOperationException>(() => repository.MarkPostedToJira("TASK-1", "other-hash"));
    }
}
