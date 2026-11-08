namespace ClaudeCodingAgent.Tests;

public sealed class ClaudeRunnerTests
{
    [Fact]
    public void BuildArguments_PlanningIsReadOnlyAndHeadless()
    {
        var arguments = ClaudeRunner.BuildArguments("claude-sonnet-5-5", allowChanges: false);

        Assert.Equal(["-p", "--output-format", "text", "--model", "claude-sonnet-5-5"], arguments.Take(5));
        Assert.Equal("default", ValueAfter(arguments, "--permission-mode"));
        Assert.DoesNotContain("bypassPermissions", arguments);
        var allowed = ValuesOf(arguments, "--allowedTools");
        Assert.Equal(["Read", "Glob", "Grep"], allowed);
        var denied = ValuesOf(arguments, "--disallowedTools");
        foreach (var tool in new[] { "Bash", "Edit", "MultiEdit", "Write", "NotebookEdit", "Bash(git push:*)", "Bash(gh:*)" })
        {
            Assert.Contains(tool, denied);
        }
    }

    [Fact]
    public void BuildArguments_ImplementationAllowsChangesButNeverPushOrGh()
    {
        var arguments = ClaudeRunner.BuildArguments(string.Empty, allowChanges: true);

        Assert.DoesNotContain("--model", arguments);
        Assert.Equal("bypassPermissions", ValueAfter(arguments, "--permission-mode"));
        Assert.Empty(ValuesOf(arguments, "--allowedTools"));
        Assert.Equal(["Bash(git push:*)", "Bash(gh:*)"], ValuesOf(arguments, "--disallowedTools"));
    }

    [Fact]
    public void ApplyEnvironment_UsesApiKeyAndStripsGitHubAndJiraSecrets()
    {
        var environment = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh",
            ["GITHUB_TOKEN"] = "gh",
            ["COPILOT_GITHUB_TOKEN"] = "gh",
            ["GITHUB_REPOSITORY_TOKEN"] = "gh",
            ["JIRA_API_TOKEN"] = "jira",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "stale",
            ["NUGET_TOKEN"] = "nuget",
            ["PATH"] = "/usr/bin"
        };

        ClaudeRunner.ApplyEnvironment(environment, "sk-ant-api03-key");

        Assert.Equal("sk-ant-api03-key", environment["ANTHROPIC_API_KEY"]);
        Assert.False(environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN", "GITHUB_REPOSITORY_TOKEN", "JIRA_API_TOKEN" })
        {
            Assert.False(environment.ContainsKey(name), name);
        }

        Assert.Equal("nuget", environment["NUGET_TOKEN"]);
        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("1", environment["DISABLE_AUTOUPDATER"]);
    }

    [Fact]
    public void ApplyEnvironment_UsesOAuthTokenVariableForSubscriptionTokens()
    {
        var environment = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "stale" };

        ClaudeRunner.ApplyEnvironment(environment, "sk-ant-oat01-token");

        Assert.Equal("sk-ant-oat01-token", environment["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
    }

    [Fact]
    public void ApplyEnvironment_WithoutCredentialLeavesCliLoginAndStillStripsSecrets()
    {
        var environment = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh",
            ["JIRA_API_TOKEN"] = "jira",
            ["CLAUDE_CONFIG_DIR"] = "/claude-config"
        };

        ClaudeRunner.ApplyEnvironment(environment, string.Empty);

        Assert.False(environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.False(environment.ContainsKey("GH_TOKEN"));
        Assert.False(environment.ContainsKey("JIRA_API_TOKEN"));
        Assert.Equal("/claude-config", environment["CLAUDE_CONFIG_DIR"]);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string flag) =>
        arguments[arguments.ToList().IndexOf(flag) + 1];

    private static List<string> ValuesOf(IReadOnlyList<string> arguments, string flag) =>
        arguments.Select((value, index) => (value, index))
            .Where(pair => pair.index > 0 && arguments[pair.index - 1] == flag)
            .Select(pair => pair.value)
            .ToList();
}
