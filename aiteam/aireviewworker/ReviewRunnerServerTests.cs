namespace ReviewWorker.Tests;

public sealed class ReviewRunnerServerTests
{
    [Fact]
    public void BuildClaudeArguments_RunsHeadlessAndDeniesGitWrites()
    {
        var arguments = ReviewRunnerServer.BuildClaudeArguments(string.Empty);

        Assert.Equal(["-p", "--output-format", "text"], arguments.Take(3));
        Assert.DoesNotContain("--model", arguments);
        Assert.Contains("bypassPermissions", arguments);
        foreach (var tool in new[] { "Bash(git push:*)", "Bash(git commit:*)", "Bash(gh:*)" })
        {
            var index = arguments.ToList().IndexOf(tool);
            Assert.True(index > 0, $"{tool} is not denied.");
            Assert.Equal("--disallowedTools", arguments[index - 1]);
        }
    }

    [Fact]
    public void BuildClaudeArguments_PassesModel()
    {
        var arguments = ReviewRunnerServer.BuildClaudeArguments(" sonnet ").ToList();

        Assert.Equal("sonnet", arguments[arguments.IndexOf("--model") + 1]);
    }

    [Fact]
    public void ApplyClaudeEnvironment_SetsConfigDirectory()
    {
        var environment = new Dictionary<string, string?>();

        ReviewRunnerServer.ApplyClaudeEnvironment(environment);

        Assert.False(string.IsNullOrEmpty(environment["CLAUDE_CONFIG_DIR"]));
        Assert.Equal("1", environment["DISABLE_AUTOUPDATER"]);
    }
}
