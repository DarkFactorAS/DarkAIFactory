using System.Net;
using System.Net.Http.Json;
using AiAgent.Runner;

namespace AiAgent.Tests;

public sealed class RunnerPolicyTests
{
    [Theory]
    [InlineData("/workspaces/a/checkout")]
    [InlineData("/workspaces/a/../b")]
    public void WorkspaceUnderRootIsAccepted(string path) =>
        Assert.StartsWith("/workspaces/", RunnerPolicy.ResolveWorkspace(path, "/workspaces"));

    [Theory]
    [InlineData("")]
    [InlineData("/workspaces")]
    [InlineData("/workspaces-evil/a")]
    [InlineData("/workspaces/../etc")]
    [InlineData("/etc/passwd")]
    public void WorkspaceOutsideRootIsRejected(string path) =>
        Assert.Throws<ArgumentException>(() => RunnerPolicy.ResolveWorkspace(path, "/workspaces"));

    [Fact]
    public void PlanModeIsReadOnly()
    {
        var args = RunnerPolicy.BuildClaudeArguments("m", RunnerModes.Plan);
        Assert.DoesNotContain("bypassPermissions", args);
        foreach (var tool in new[] { "Bash", "Edit", "Write", "Bash(git push:*)", "Bash(gh:*)" })
        {
            Assert.Contains(tool, args);
        }

        Assert.Contains("--deny-tool=write", RunnerPolicy.BuildCopilotArguments("", RunnerModes.Plan));
    }

    [Theory]
    [InlineData(RunnerModes.Implement)]
    [InlineData(RunnerModes.Review)]
    public void EditModesAllowEditsButDenyPushAndGh(string mode)
    {
        var args = RunnerPolicy.BuildClaudeArguments("", mode);
        Assert.Contains("bypassPermissions", args);
        Assert.DoesNotContain("Edit", args);
        Assert.Contains("Bash(git push:*)", args);
        Assert.Contains("Bash(gh:*)", args);
        var copilot = RunnerPolicy.BuildCopilotArguments("", mode);
        Assert.DoesNotContain("--deny-tool=write", copilot);
        Assert.Contains("--deny-tool=shell(git push)", copilot);
        Assert.Contains("--deny-tool=shell(gh)", copilot);
    }

    [Fact]
    public void EnvironmentStripsCredentialsButKeepsNuGet()
    {
        var env = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "x", ["GITHUB_TOKEN"] = "x", ["COPILOT_GITHUB_TOKEN"] = "x",
            ["JIRA_API_TOKEN"] = "x", ["ANTHROPIC_API_KEY"] = "x",
            ["NUGET_USERNAME"] = "u", ["NUGET_TOKEN"] = "t", ["PATH"] = "/bin"
        };
        RunnerPolicy.ApplyEnvironment(env, "claude", "copilot-secret");
        Assert.False(env.ContainsKey("GH_TOKEN"));
        Assert.False(env.ContainsKey("GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("COPILOT_GITHUB_TOKEN"));
        Assert.False(env.ContainsKey("JIRA_API_TOKEN"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("u", env["NUGET_USERNAME"]);
        Assert.Equal("t", env["NUGET_TOKEN"]);
    }

    [Fact]
    public void CopilotTokenIsOnlyPassedToCopilot()
    {
        var env = new Dictionary<string, string?>();
        RunnerPolicy.ApplyEnvironment(env, "copilot", "tok");
        Assert.Equal("tok", env["GH_TOKEN"]);
    }

    [Fact]
    public async Task RunPassesPromptViaStdinAndRejectsBadWorkspace()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "w")).FullName;
        string? stdin = null;
        IDictionary<string, string?>? seenEnv = null;
        IReadOnlyList<string>? seenArgs = null;
        await using var app = RunnerServer.Build("http://127.0.0.1:0", root, 2, (_, args, _, env, input, _) =>
        {
            stdin = input; seenEnv = env; seenArgs = args;
            return Task.FromResult(new ProcessResult(0, "{\"result\":\"ok\",\"usage\":{\"input_tokens\":3,\"output_tokens\":4},\"duration_api_ms\":7}", ""));
        });
        await app.StartAsync();
        using var client = new RunnerClient(new Uri(app.Urls.First()));
        var result = await client.RunAsync(new RunnerRequest("claude", "m", "plan", workspace, "secret prompt"), default);
        Assert.Equal("ok", result.Output);
        Assert.Equal(7, result.LlmDurationMs);
        Assert.Equal(4, result.Usage!.OutputTokens);
        Assert.Equal("secret prompt", stdin);
        Assert.DoesNotContain("secret prompt", seenArgs!);
        Assert.False(seenEnv!.ContainsKey("GITHUB_TOKEN"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RunAsync(new RunnerRequest("claude", "", "plan", "/etc", "p"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RunAsync(new RunnerRequest("claude", "", "bogus", workspace, "p"), default));
    }

    [Fact]
    public async Task ExtraJobsAreRejectedAsBusy()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "w")).FullName;
        var gate = new TaskCompletionSource();
        await using var app = RunnerServer.Build("http://127.0.0.1:0", root, 1, async (_, _, _, _, _, _) =>
        {
            await gate.Task;
            return new ProcessResult(0, "x", "");
        });
        await app.StartAsync();
        using var client = new RunnerClient(new Uri(app.Urls.First()));
        var request = new RunnerRequest("claude", "", "implement", workspace, "p");
        var first = client.RunAsync(request, default);
        await Task.Delay(300);
        await Assert.ThrowsAsync<RunnerBusyException>(() => client.RunAsync(request, default));
        gate.SetResult();
        Assert.Equal(0, (await first).ExitCode);
    }
}
