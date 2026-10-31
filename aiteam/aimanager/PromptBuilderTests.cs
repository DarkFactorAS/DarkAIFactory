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
    public void ImplementationPrompt_ContainsIssueAndPlan()
    {
        var prompt = PromptBuilder.BuildImplementationPrompt(Issue, "step one");

        Assert.Contains("Jira key: DFAI-1", prompt);
        Assert.Contains("My description", prompt);
        Assert.Contains("<plan>", prompt);
        Assert.Contains("step one", prompt);
        Assert.Contains("untrusted", prompt);
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
