using System.Diagnostics;
using AiAgent.Core;
using AiAgent.LanguageModel;

namespace AiAgent.Tests;

public sealed class LanguageModelTests
{
    private static AgentDefinition Agent(string provider, string? model) =>
        new(1, "a", "r", "b", provider, model, true, false);

    [Fact]
    public void ClaudeArguments_ReadOnly_DenyMutatingTools()
    {
        var args = ClaudeLanguageModel.BuildArguments("m", allowChanges: false);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--model", "m"], args.Take(6));
        Assert.Contains("default", args);
        Assert.DoesNotContain("bypassPermissions", args);
        foreach (var tool in new[] { "Bash", "Edit", "Write", "Bash(git push:*)", "Bash(gh:*)" })
        {
            Assert.Contains(tool, args);
        }
    }

    [Fact]
    public void ClaudeArguments_AllowChanges_BypassButStillDenyPush()
    {
        var args = ClaudeLanguageModel.BuildArguments("m", allowChanges: true);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("Bash(git push:*)", args);
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public void ParseStreamJson_PrefersResultEvent()
    {
        var output = "noise\n{\"type\":\"system\"}\n" +
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"partial\"}]}}\n" +
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"final answer\"}\n";
        Assert.Equal("final answer", ClaudeLanguageModel.ParseStreamJson(output));
    }

    [Fact]
    public void ParseStreamJson_FallsBackToAssistantText()
    {
        var output = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"tool_use\"}]}}";
        Assert.Equal("hi", ClaudeLanguageModel.ParseStreamJson(output));
    }

    [Fact]
    public void ParseStreamJson_ErrorResultThrows()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ClaudeLanguageModel.ParseStreamJson("{\"type\":\"result\",\"is_error\":true,\"result\":\"boom\"}"));
    }

    [Fact]
    public void CopilotArguments_ReadOnly_DenyShellAndWrite()
    {
        var args = CopilotLanguageModel.BuildArguments("p", "gpt-5-mini", allowChanges: false);
        Assert.Equal(["--prompt", "p", "--model", "gpt-5-mini"], args.Take(4));
        Assert.Contains("--deny-tool=shell", args);
        Assert.Contains("--deny-tool=write", args);
        Assert.Contains("--deny-tool=shell(git push)", args);
    }

    [Fact]
    public void CopilotArguments_AllowChanges_OmitsBlanketDenies()
    {
        var args = CopilotLanguageModel.BuildArguments("p", "x", allowChanges: true);
        Assert.DoesNotContain("--deny-tool=shell", args);
        Assert.DoesNotContain("--deny-tool=write", args);
        Assert.Contains("--deny-tool=shell(gh)", args);
    }

    [Fact]
    public void Factory_UsesDefaultsAndSwitchesProviderPerCall()
    {
        var factory = new LanguageModelFactory();
        var claude = factory.Create(Agent("claude", null));
        Assert.IsType<ClaudeLanguageModel>(claude);
        Assert.Equal("claude-sonnet-5-5", claude.Model);

        var copilot = factory.Create(Agent("Copilot", " "));
        Assert.IsType<CopilotLanguageModel>(copilot);
        Assert.Equal("gpt-5-mini", copilot.Model);

        Assert.Equal("custom", factory.Create(Agent("claude", "custom")).Model);
        Assert.Throws<InvalidOperationException>(() => factory.Create(Agent("other", null)));
    }

    [Fact]
    public async Task RunAsync_InvokesProviderCliWithModel()
    {
        var files = new List<(string File, List<string> Args)>();
        Task<ProcessResult> Runner(ProcessStartInfo info, CancellationToken _)
        {
            files.Add((info.FileName, info.ArgumentList.ToList()));
            return Task.FromResult(new ProcessResult(0,
                info.FileName == "claude" ? "{\"type\":\"result\",\"result\":\"ok-c\"}" : "ok-p\n", ""));
        }

        var factory = new LanguageModelFactory(Runner);
        var request = new LanguageModelRequest("/tmp", "do it", false);
        Assert.Equal("ok-c", await factory.Create(Agent("claude", null)).RunAsync(request, default));
        Assert.Equal("ok-p", await factory.Create(Agent("copilot", "m2")).RunAsync(request, default));

        Assert.Equal("claude", files[0].File);
        Assert.Equal("do it", files[0].Args[^1]);
        Assert.Contains("claude-sonnet-5-5", files[0].Args);
        Assert.Equal("copilot", files[1].File);
        Assert.Contains("m2", files[1].Args);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_Throws()
    {
        var model = new CopilotLanguageModel(null, (_, _) => Task.FromResult(new ProcessResult(2, "", "bad")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            model.RunAsync(new LanguageModelRequest("/tmp", "p", true), default));
        Assert.Contains("bad", ex.Message);
    }
}
