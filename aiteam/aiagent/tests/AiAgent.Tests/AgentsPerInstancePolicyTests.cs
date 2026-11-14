using AiAgent.Core;

namespace AiAgent.Tests;

public class AgentsPerInstancePolicyTests
{
    [Fact]
    public void DefaultIsOne()
    {
        Assert.Equal(1, AgentsPerInstancePolicy.Resolve(new AgentSettings().AgentsPerInstance, null, out var w));
        Assert.Null(w);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ValidValuesAreUsed(int value)
    {
        Assert.Equal(value, AgentsPerInstancePolicy.Resolve(value, null, out var w));
        Assert.Null(w);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(5, 3)]
    public void OutOfRangeIsClampedWithWarning(int value, int expected)
    {
        Assert.Equal(expected, AgentsPerInstancePolicy.Resolve(value, null, out var w));
        Assert.NotNull(w);
    }

    [Fact]
    public void EnvOverridesSettings()
    {
        Assert.Equal(3, AgentsPerInstancePolicy.Resolve(1, "3", out var w));
        Assert.Null(w);
    }

    [Fact]
    public void UnparsableEnvFallsBackWithWarning()
    {
        Assert.Equal(2, AgentsPerInstancePolicy.Resolve(2, "many", out var w));
        Assert.NotNull(w);
    }
}
