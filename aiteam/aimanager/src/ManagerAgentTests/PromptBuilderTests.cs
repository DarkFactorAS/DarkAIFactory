namespace ManagerAgent.Tests;

public sealed class PromptBuilderTests
{
    private static readonly JiraIssue Issue = new("DFAI-1", "My title", "My description");

    [Fact]
    public void PlanPrompt_ContainsIssueAndUntrustedWarning()
    {
        var prompt = PromptBuilder.BuildPlanPrompt(Issue);

        Assert.Contains("Jira key: DFAI-1", prompt);
        Assert.Contains("Title: My title", prompt);
        Assert.Contains("<description>", prompt);
        Assert.Contains("My description", prompt);
        Assert.Contains("untrusted", prompt);
    }

    [Fact]
    public void PlanPrompt_ForbidsImplementation()
    {
        var prompt = PromptBuilder.BuildPlanPrompt(Issue);

        Assert.Contains("do not implement code", prompt);
        Assert.Contains("edit files", prompt);
        Assert.Contains("run commands or builds", prompt);
        Assert.Contains("commit, push, or create pull requests", prompt);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(24_000)]
    [InlineData(24_001)]
    [InlineData(50_000)]
    public void BuildPlanComments_PreservesFullPlan(int length)
    {
        var plan = new string('a', length);
        var comments = PromptBuilder.BuildPlanComments(plan);
        var parts = comments.Select(comment => comment[(comment.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]);

        Assert.Equal(plan, string.Concat(parts));
        Assert.All(comments, comment => Assert.True(comment.Length < 25_000));
    }

    [Fact]
    public void BuildPlanComments_DoesNotSplitSurrogatePairs()
    {
        var plan = new string('a', 23_999) + "\U0001F600" + "end";
        var comments = PromptBuilder.BuildPlanComments(plan);

        Assert.EndsWith(new string('a', 23_999), comments[0]);
        Assert.EndsWith("\U0001F600end", comments[1]);
    }

    [Fact]
    public void LimitForComment_KeepsShortText()
    {
        Assert.Equal("short", PromptBuilder.LimitForComment("short"));
    }

    [Fact]
    public void LimitForComment_KeepsTextAtLimit()
    {
        var text = new string('a', 24_000);

        Assert.Equal(text, PromptBuilder.LimitForComment(text));
    }

    [Fact]
    public void LimitForComment_TruncatesLongText()
    {
        var result = PromptBuilder.LimitForComment(new string('a', 24_001));

        Assert.StartsWith(new string('a', 24_000), result);
        Assert.EndsWith("[Output truncated to fit in a Jira comment.]", result);
        Assert.DoesNotContain(new string('a', 24_001), result);
    }
}
