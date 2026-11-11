using AiTeam.Tests;
using ManagerAgent.Repository;

namespace ManagerAgent.Tests;

public sealed class PullRequestRepositoryTests
{
    [Fact]
    public void Replace_RemovesAlreadyTrackedReplacementBeforeUpdatingOriginal()
    {
        var database = new DatabaseStub();
        database.OnNonQuery = command =>
            command.CommandText.StartsWith("DELETE old", StringComparison.Ordinal) ? 1 : 0;
        var repositoryUrl = new Uri("https://github.com/owner/repo");
        var repository = new PullRequestRepository(database);

        Assert.True(repository.Replace(repositoryUrl, 5, "https://github.com/owner/repo/pull/7"));

        var delete = Assert.IsType<DatabaseStub.Command>(database.Commands[0]);
        Assert.StartsWith("DELETE old", delete.CommandText);
        Assert.Contains("replacement.jira_key = old.jira_key", delete.CommandText);
        Assert.Equal("https://github.com/owner/repo/pull/7", delete.Values["@new_pr_url"]);

        var update = Assert.IsType<DatabaseStub.Command>(database.Commands[1]);
        Assert.StartsWith("UPDATE pull_requests", update.CommandText);
        Assert.Equal(7L, update.Values["@new_pr_number"]);
    }
}
