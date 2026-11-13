namespace ReviewWorker.Tests;

public sealed class ReviewCommitMessageTests
{
    private static ReviewComment Comment(string body, string path = "src/Worker/Client.cs", int? line = 42) =>
        new(123, 1, 7, path, line, body, "copilot-pull-request-reviewer", "sha");

    [Fact]
    public void SubjectNamesTheFixAndTheFile()
    {
        var message = ReviewCommitMessage.Build("DFAI-166",
            Comment("The HttpClient is never disposed. Wrap it in a using statement."));
        var lines = message.Split('\n');

        Assert.Equal("DFAI-166: The HttpClient is never disposed (src/Worker/Client.cs:42)", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal("Review comment 123 by copilot-pull-request-reviewer on src/Worker/Client.cs:42:", lines[2]);
        Assert.Contains("> The HttpClient is never disposed. Wrap it in a using statement.", message);
    }

    [Fact]
    public void SkipsMarkdownAndSuggestionBlocks()
    {
        var body = "```suggestion\nvar x = 1;\n```\n\n**Possible null reference:** `user` can be null here.";

        Assert.Equal("Possible null reference: user can be null here", ReviewCommitMessage.Summary(body));
    }

    [Theory]
    [InlineData("- use `ConfigureAwait(false)`", "Use ConfigureAwait(false)")]
    [InlineData("## Typo in log message", "Typo in log message")]
    [InlineData("Version 1.2 is outdated", "Version 1.2 is outdated")]
    public void SummaryUsesTheFirstProseLine(string body, string expected) =>
        Assert.Equal(expected, ReviewCommitMessage.Summary(body));

    [Fact]
    public void LongSummaryIsTruncatedButKeepsTheFile()
    {
        var subject = ReviewCommitMessage.Build("DFAI-1", Comment(new string('a', 300))).Split('\n')[0];

        Assert.True(subject.Length <= ReviewCommitMessage.MaxSubjectLength, subject);
        Assert.EndsWith("… (src/Worker/Client.cs:42)", subject);
    }

    [Fact]
    public void EmptyCommentFallsBackToFileOnly()
    {
        var subject = ReviewCommitMessage.Build("DFAI-1", Comment("```\ncode\n```", line: null)).Split('\n')[0];

        Assert.Equal("DFAI-1: Fix review comment in src/Worker/Client.cs", subject);
    }

    [Fact]
    public void ControlCharactersAreRemoved()
    {
        var message = ReviewCommitMessage.Build("DFAI-1", Comment("Bad\u001b[31m color\r\nsecond line"));

        Assert.DoesNotContain('\u001b', message);
        Assert.DoesNotContain('\r', message);
        Assert.Contains("> second line", message);
    }
}
