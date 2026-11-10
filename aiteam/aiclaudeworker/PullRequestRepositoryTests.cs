using AiTeam.Tests;
using ClaudeCodingAgent.Repository;

namespace ClaudeCodingAgent.Tests;

public sealed class PullRequestRepositoryTests
{
    [Fact]
    public void Record_InsertsPullRequestIdempotently()
    {
        var database = new DatabaseStub { AffectedRows = 0 };

        new PullRequestRepository(database).Record(
            "TASK-1", new Uri("https://github.com/owner/repo"), "https://github.com/owner/repo/pull/42");

        var command = Assert.Single(database.Commands);
        Assert.Contains("INSERT INTO pull_requests", command.CommandText);
        Assert.Contains("ON DUPLICATE KEY UPDATE", command.CommandText);
        Assert.Equal("TASK-1", command.Values["@jira_key"]);
        Assert.Equal("https://github.com/owner/repo", command.Values["@repository_url"]);
        Assert.Equal(42L, command.Values["@pr_number"]);
        Assert.Equal("https://github.com/owner/repo/pull/42", command.Values["@pr_url"]);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/pull/7/", 7)]
    [InlineData("https://github.com/owner/repo/pull/123", 123)]
    public void ParsePullRequestNumber_ReadsLastSegment(string url, long expected) =>
        Assert.Equal(expected, PullRequestRepository.ParsePullRequestNumber(url));

    [Fact]
    public void ParsePullRequestNumber_RejectsUrlWithoutNumber() =>
        Assert.Throws<InvalidDataException>(() =>
            PullRequestRepository.ParsePullRequestNumber("https://github.com/owner/repo/pulls"));

    [Fact]
    public void ListOpen_ReturnsTrackedPullRequestsForRepository()
    {
        var database = new DatabaseStub();
        database.Rows.Columns.Add("jira_key", typeof(string));
        database.Rows.Columns.Add("repository_url", typeof(string));
        database.Rows.Columns.Add("pr_number", typeof(long));
        database.Rows.Columns.Add("pr_url", typeof(string));
        database.Rows.Rows.Add("TASK-1", "https://github.com/owner/repo", 42L, "https://github.com/owner/repo/pull/42");

        var result = new PullRequestRepository(database).ListOpen(new Uri("https://github.com/owner/repo"));

        var pullRequest = Assert.Single(result);
        Assert.Equal("TASK-1", pullRequest.JiraKey);
        Assert.Equal(42L, pullRequest.Number);
        Assert.Equal("https://github.com/owner/repo/pull/42", pullRequest.Url);
        var command = Assert.Single(database.Commands);
        Assert.Contains("FROM pull_requests", command.CommandText);
        Assert.Equal("https://github.com/owner/repo", command.Values["@repository_url"]);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void DeleteByNumber_ReportsWhetherRowWasDeleted(int affectedRows, bool expected)
    {
        var database = new DatabaseStub { AffectedRows = affectedRows };

        var deleted = new PullRequestRepository(database).DeleteByNumber(new Uri("https://github.com/owner/repo"), 42);

        Assert.Equal(expected, deleted);
        var command = Assert.Single(database.Commands);
        Assert.Contains("DELETE FROM pull_requests", command.CommandText);
        Assert.Equal(42L, command.Values["@pr_number"]);
        Assert.Equal("https://github.com/owner/repo", command.Values["@repository_url"]);
    }

    [Fact]
    public void Replace_UpdatesRowWithNewPullRequestNumberAndUrl()
    {
        var database = new DatabaseStub();

        var replaced = new PullRequestRepository(database).Replace(
            new Uri("https://github.com/owner/repo"), 42, "https://github.com/owner/repo/pull/43");

        Assert.True(replaced);
        var command = Assert.Single(database.Commands);
        Assert.Contains("UPDATE pull_requests", command.CommandText);
        Assert.Equal(43L, command.Values["@new_pr_number"]);
        Assert.Equal("https://github.com/owner/repo/pull/43", command.Values["@new_pr_url"]);
        Assert.Equal(42L, command.Values["@old_pr_number"]);
    }

    [Fact]
    public void Replace_ReturnsFalseWhenNoRowMatches() =>
        Assert.False(new PullRequestRepository(new DatabaseStub { AffectedRows = 0 }).Replace(
            new Uri("https://github.com/owner/repo"), 42, "https://github.com/owner/repo/pull/43"));
}
