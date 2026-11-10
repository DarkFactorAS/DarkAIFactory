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
}
