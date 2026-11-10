using AiTeam.Configuration;
using AiTeam.Tests;
using ClaudeCodingAgent.Repository;

namespace ClaudeCodingAgent.Tests;

public sealed class ManagerPlanTests
{
    private static readonly Uri GitHubRepositoryUrl = new("https://github.com/owner/repo");

    [Theory]
    [InlineData("Changed title", "Description", "https://github.com/owner/repo")]
    [InlineData("Title", "Changed description", "https://github.com/owner/repo")]
    [InlineData("Title", "Description", "https://github.com/owner/other")]
    public void CanImplement_RejectsChangedRequirementsOrRepository(string title, string description, string gitHubRepositoryUrlText)
    {
        var plan = new ManagerPlan("Plan", true, ManagerPlan.Fingerprint("Title", "Description", GitHubRepositoryUrl));

        Assert.False(plan.CanImplement(title, description, new Uri(gitHubRepositoryUrlText)));
    }

    [Fact]
    public void CanImplement_AllowsSameRequirementsAfterAgentCommentsAndLabelsChangeUpdatedTime()
    {
        var issue = new JiraIssue("TASK-1", "Title", "Description")
        {
            UpdatedAt = "2026-11-02T13:00:00Z", Labels = ["ai-analyzed", "ai-manager-planned"]
        };
        var plan = new ManagerPlan("Plan", true, ManagerPlan.Fingerprint(issue.Summary, issue.Description, GitHubRepositoryUrl),
            "2026-11-02T12:00:00Z");

        Assert.True(plan.CanImplement(issue.Summary, issue.Description, GitHubRepositoryUrl));
    }

    [Theory]
    [InlineData(null, true, "Plan")]
    [InlineData("", true, "Plan")]
    [InlineData("current", false, "Plan")]
    [InlineData("current", true, " ")]
    public void CanImplement_RejectsLegacyUnpostedAndEmptyPlans(string? hash, bool posted, string text)
    {
        var plan = new ManagerPlan(text, posted,
            hash == "current" ? ManagerPlan.Fingerprint("Title", "Description", GitHubRepositoryUrl) : hash);

        Assert.False(plan.CanImplement("Title", "Description", GitHubRepositoryUrl));
    }

    [Fact]
    public void Fingerprint_NormalizesEquivalentRepositoryUrlsAndLineEndings()
    {
        Assert.Equal(
            ManagerPlan.Fingerprint("Title", "One\r\nTwo", new Uri("https://github.com/Owner/Repo.git")),
            ManagerPlan.Fingerprint("Title", "One\nTwo", new Uri("https://github.com/owner/repo/")));
    }

    [Fact]
    public void Fingerprint_SeparatesFieldsWithoutDelimiterCollisions()
    {
        Assert.NotEqual(ManagerPlan.Fingerprint("A\nB", "C", GitHubRepositoryUrl),
            ManagerPlan.Fingerprint("A", "B\nC", GitHubRepositoryUrl));
        Assert.Equal(64, ManagerPlan.Fingerprint("A", "B", GitHubRepositoryUrl).Length);
    }

    [Fact]
    public void Find_LegacyPlanRemainsReadableButCannotBeImplemented()
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("plan", typeof(string));
        database.Rows.Columns.Add("posted_to_jira", typeof(bool));
        database.Rows.Columns.Add("requirements_hash", typeof(string));
        database.Rows.Columns.Add("source_updated", typeof(string));
        database.Rows.Rows.Add("Old plan", true, DBNull.Value, DBNull.Value);

        var plan = Assert.IsType<ManagerPlan>(new ManagerPlanReader(database).Find("TASK-1", 1));

        Assert.Null(plan.RequirementsHash);
        Assert.False(plan.CanImplement("Title", "Description", GitHubRepositoryUrl));
    }
}
