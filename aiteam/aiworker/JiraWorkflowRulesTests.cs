using AiTeam.Configuration;

namespace CodingAgent.Tests;

public sealed class JiraWorkflowRulesTests
{
    [Theory]
    [InlineData("DFAI-136", "ai-agent/DFAI-136")]
    [InlineData(" DFAI 1/2 ", "ai-agent/DFAI-1-2")]
    public void ParentBranchName_IsDeterministicAndBranchSafe(string key, string expected) =>
        Assert.Equal(expected, JiraWorkflowRules.ParentBranchName(key));

    [Fact]
    public void OpenBlockers_TreatsOnlyDoneAsFinishedAndUnknownStatusAsOpen()
    {
        var blockers = new[]
        {
            new JiraBlocker("A-1", "Done"),
            new JiraBlocker("A-2", "done"),
            new JiraBlocker("A-3", "In Review"),
            new JiraBlocker("A-4", string.Empty)
        };

        var open = JiraWorkflowRules.OpenBlockers(blockers, "Done");

        Assert.Equal(["A-3", "A-4"], open.Select(blocker => blocker.Key));
        Assert.Equal("A-3 (In Review), A-4", JiraWorkflowRules.DescribeBlockers(open));
        Assert.Empty(JiraWorkflowRules.OpenBlockers([], "Done"));
    }
}
