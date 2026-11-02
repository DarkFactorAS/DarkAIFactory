using AiTeam.Configuration;
using AiTeam.Tests;
using CodingAgent.Repository;

namespace CodingAgent.Tests;

public sealed class ProjectRepositoryTests
{
    [Fact]
    public void GetProjects_ReadsAllMappingsInIdOrder()
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("id", typeof(long));
        database.Rows.Columns.Add("jira_project_key", typeof(string));
        database.Rows.Columns.Add("target_repository_url", typeof(string));
        database.Rows.Rows.Add(1L, "FIRST", "https://github.com/owner/first");
        database.Rows.Rows.Add(2L, "SECOND", "https://github.com/owner/second");

        var projects = new ManagerProjectRepository(database).GetProjects();

        Assert.Equal(2, projects.Count);
        Assert.Equal(new ManagerProject(2, "SECOND", "https://github.com/owner/second"), projects[1]);
        Assert.EndsWith("ORDER BY id", Assert.Single(database.Commands).CommandText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(12L)]
    public void Find_BindsIssueAndOptionalProject(long? projectId)
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("plan", typeof(string));
        database.Rows.Columns.Add("posted_to_jira", typeof(bool));
        database.Rows.Columns.Add("requirements_hash", typeof(string));
        database.Rows.Columns.Add("source_updated", typeof(string));
        database.Rows.Rows.Add("Manager plan", true, "hash", "2026-11-02T12:00:00Z");

        var plan = new ManagerPlanReader(database).Find("TASK-1", projectId);
        Assert.Equal(new ManagerPlan("Manager plan", true, "hash", "2026-11-02T12:00:00Z"), plan);

        var command = Assert.Single(database.Commands);
        Assert.Equal("TASK-1", command.Values["@jira_key"]);
        if (projectId.HasValue)
        {
            Assert.Contains("AND manager_project_id = @project_id", command.CommandText);
            Assert.Equal(projectId.Value, command.Values["@project_id"]);
        }
        else
        {
            Assert.DoesNotContain("@project_id", command.CommandText);
        }
    }
}
