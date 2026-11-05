using AiTeam.Configuration;

namespace CodingAgent.Tests;

public sealed class IgnoredTaskNotifierTests
{
    private sealed class FakeJira
    {
        public List<string> Comments { get; } = [];
        public bool FailAdd { get; set; }

        public Task<IReadOnlyList<string>> GetAsync(string key, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<string>>(Comments.ToArray());

        public Task AddAsync(string key, string text, CancellationToken token)
        {
            if (FailAdd)
            {
                throw new HttpRequestException("boom");
            }
            Comments.Add(text);
            return Task.CompletedTask;
        }
    }

    private static Task<bool> Notify(FakeJira jira, string reason, List<string>? warnings = null) =>
        IgnoredTaskNotifier.NotifyAsync(
            "AIWorker", "DFAI-1", reason, jira.GetAsync, jira.AddAsync,
            message => warnings?.Add(message), CancellationToken.None);

    [Fact]
    public async Task PostsCommentOnFirstSkip()
    {
        var jira = new FakeJira();
        Assert.True(await Notify(jira, "reason one"));
        Assert.Equal(["[AIWorker] Not picked up: reason one"], jira.Comments);
    }

    [Fact]
    public async Task DoesNotRepeatSameReason()
    {
        var jira = new FakeJira();
        await Notify(jira, "reason one");
        Assert.False(await Notify(jira, "reason one"));
        Assert.Single(jira.Comments);
    }

    [Fact]
    public async Task PostsNewCommentWhenReasonChanges()
    {
        var jira = new FakeJira();
        await Notify(jira, "reason one");
        Assert.True(await Notify(jira, "reason two"));
        Assert.Equal(2, jira.Comments.Count);
    }

    [Fact]
    public async Task FailedPostIsNonFatal()
    {
        var jira = new FakeJira { FailAdd = true };
        var warnings = new List<string>();
        Assert.False(await Notify(jira, "reason one", warnings));
        Assert.Single(warnings);
    }
}
