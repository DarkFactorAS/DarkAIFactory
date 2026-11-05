using AiTeam.Configuration;

namespace CodingAgent.Tests;

public sealed class IgnoredTaskNotifierTests
{
    private sealed class FakeJira
    {
        public List<string> Comments { get; } = [];
        public bool FailAdd { get; set; }
        public List<string> Labels { get; } = [];
        public bool FailLabel { get; set; }

        public Task AddLabelAsync(string key, string label, CancellationToken token)
        {
            if (FailLabel)
            {
                throw new HttpRequestException("label boom");
            }
            Labels.Add(label);
            return Task.CompletedTask;
        }

        public Task RemoveLabelAsync(string key, string label, CancellationToken token)
        {
            Labels.Remove(label);
            return Task.CompletedTask;
        }

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
            "AIWorker", "DFAI-1", reason, jira.GetAsync, jira.AddAsync, jira.AddLabelAsync,
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
    public async Task AddsIgnoredLabelAndKeepsCommentUnchanged()
    {
        var jira = new FakeJira();
        await Notify(jira, "reason one");
        Assert.Equal(["ai-ignored"], jira.Labels);
        Assert.Equal(["[AIWorker] Not picked up: reason one"], jira.Comments);
    }

    [Fact]
    public async Task AddsLabelEvenWhenCommentIsDuplicate()
    {
        var jira = new FakeJira();
        await Notify(jira, "reason one");
        jira.Labels.Clear();
        Assert.False(await Notify(jira, "reason one"));
        Assert.Equal(["ai-ignored"], jira.Labels);
        Assert.Single(jira.Comments);
    }

    [Fact]
    public async Task LabelFailureIsNonFatalAndCommentStillPosted()
    {
        var jira = new FakeJira { FailLabel = true };
        var warnings = new List<string>();
        Assert.True(await Notify(jira, "reason one", warnings));
        Assert.Single(warnings);
        Assert.Single(jira.Comments);
    }

    [Fact]
    public async Task ClearRemovesLabelWhenPresent()
    {
        var jira = new FakeJira();
        jira.Labels.Add("ai-ignored");
        await IgnoredTaskNotifier.ClearAsync(
            "DFAI-1", ["ai-ignored", "other"], jira.RemoveLabelAsync, _ => { }, CancellationToken.None);
        Assert.Empty(jira.Labels);
    }

    [Fact]
    public async Task ClearDoesNothingWhenLabelAbsent()
    {
        var calls = 0;
        await IgnoredTaskNotifier.ClearAsync(
            "DFAI-1", ["other"], (_, _, _) => { calls++; return Task.CompletedTask; }, _ => { }, CancellationToken.None);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ClearFailureIsNonFatal()
    {
        var warnings = new List<string>();
        await IgnoredTaskNotifier.ClearAsync(
            "DFAI-1", ["ai-ignored"], (_, _, _) => throw new HttpRequestException("boom"), warnings.Add, CancellationToken.None);
        Assert.Single(warnings);
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
