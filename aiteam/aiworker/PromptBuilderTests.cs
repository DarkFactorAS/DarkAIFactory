using AiTeam.Configuration;

namespace CodingAgent.Tests;

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
        Assert.Contains("Shell execution is enabled", prompt);
        Assert.Contains("unit tests of ALL test projects", prompt);
        Assert.DoesNotContain("Shell execution is disabled", prompt);
    }

    [Fact]
    public void ResultComment_IdentifiesManagerPlanAndExactRequirementsVersion()
    {
        var plan = new ManagerPlan("Plan", true, new string('A', 64), "2026-11-02T12:00:00Z");

        var comment = PromptBuilder.BuildResultComment("Implemented task", "Pull request: https://github.com/owner/repo/pull/1", plan);

        Assert.Contains("Plan source: manager plan from `manager_plans`.", comment);
        Assert.Contains($"Requirements fingerprint: `{plan.RequirementsHash}`.", comment);
        Assert.Contains($"Source Jira updated: `{plan.SourceUpdated}`.", comment);
        Assert.Contains("Implemented task", comment);
        Assert.Contains("https://github.com/owner/repo/pull/1", comment);
        Assert.DoesNotContain("local worker planning", comment);
    }

    [Fact]
    public void ResultComment_IdentifiesLocalPlanningWithoutClaimingManagerDelegation()
    {
        var comment = PromptBuilder.BuildResultComment("Implemented locally", "No changes to push.", null);

        Assert.Contains("Plan source: local worker planning", comment);
        Assert.Contains("manager-less custom JQL mode", comment);
        Assert.DoesNotContain("Requirements fingerprint", comment);
        Assert.DoesNotContain("Source Jira updated", comment);
    }

    [Fact]
    public void ResultComment_PreservesAuditMetadataAndDeliveryDetailsWhenResultIsTruncated()
    {
        var plan = new ManagerPlan("Plan", true, new string('A', 64), "2026-11-02T12:00:00Z");
        var comment = PromptBuilder.BuildResultComment(new string('x', 40_000), "Pull request: https://github.com/owner/repo/pull/1", plan);

        Assert.Contains(plan.RequirementsHash!, comment);
        Assert.Contains(plan.SourceUpdated!, comment);
        Assert.Contains("[Output truncated", comment);
        Assert.EndsWith("Pull request: https://github.com/owner/repo/pull/1", comment);
        Assert.True(comment.Length < 32_000);
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
