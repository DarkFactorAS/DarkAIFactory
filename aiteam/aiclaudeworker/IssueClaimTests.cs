using AiTeam.Configuration;
using AiTeam.Tests;

namespace ClaudeCodingAgent.Tests;

public sealed class IssueClaimTests
{
    [Fact]
    public void TryAcquire_HoldsDatabaseLockUntilClaimIsDisposed()
    {
        var database = new DatabaseStub { ScalarResult = 1 };
        var repository = new JiraIssueClaimRepository(database);

        var claim = repository.TryAcquire("task-1");

        Assert.NotNull(claim);
        var command = Assert.Single(database.Commands);
        Assert.Contains("GET_LOCK", command.CommandText);
        Assert.Equal("jira-issue-", command.Values["@lock_name"].ToString()![..11]);
        Assert.False(command.IsDisposed);
        claim!.Dispose();
        Assert.True(command.IsDisposed);
    }

    [Fact]
    public void TryAcquire_DisposesDatabaseCommandWhenIssueIsAlreadyClaimed()
    {
        var database = new DatabaseStub { ScalarResult = 0 };
        var repository = new JiraIssueClaimRepository(database);

        var claim = repository.TryAcquire("TASK-1");

        Assert.Null(claim);
        Assert.True(Assert.Single(database.Commands).IsDisposed);
    }
}
