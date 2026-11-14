using AiAgent.Core;

namespace AiAgent.Tests;

public sealed class AgentLogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Format_WithoutName_LeavesMessageUnchanged(string? name) =>
        Assert.Equal("hello", AgentLog.Format(name, "hello"));

    [Fact]
    public void Format_WithName_AddsPrefix() =>
        Assert.Equal("[Thor Techlead] hello", AgentLog.Format("Thor Techlead", "hello"));

    [Fact]
    public void NestedScopes_RestorePreviousName()
    {
        Assert.Null(AgentLog.Current);
        using (AgentLog.BeginScope("outer"))
        {
            using (AgentLog.BeginScope("inner"))
            {
                Assert.Equal("inner", AgentLog.Current);
            }

            Assert.Equal("outer", AgentLog.Current);
        }

        Assert.Null(AgentLog.Current);
    }

    [Fact]
    public async Task ConcurrentScopes_SeeOnlyOwnName()
    {
        async Task<string?> Run(string name)
        {
            using var scope = AgentLog.BeginScope(name);
            await Task.Delay(30);
            return AgentLog.Current;
        }

        var results = await Task.WhenAll(Task.Run(() => Run("A")), Task.Run(() => Run("B")));
        Assert.Equal("A", results[0]);
        Assert.Equal("B", results[1]);
    }
}
