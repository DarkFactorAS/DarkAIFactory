using AiAgent.Core;

namespace AiAgent.Tests;

public sealed class ProjectCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("aiagent-project-commands-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Touch(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_root, name), string.Empty);
        }
    }

    [Fact]
    public void ConfiguredCommandIsAlwaysUsed()
    {
        Assert.Equal("make check", ProjectCommands.Resolve(" make check ", "build", _root));
    }

    [Fact]
    public void RootWithoutSolutionOrProjectSkipsValidation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "App.csproj"), string.Empty);

        Assert.Null(ProjectCommands.Resolve(null, "build", _root));
        Assert.Null(ProjectCommands.Resolve(null, "test", _root));
    }

    [Fact]
    public void SingleSolutionIsBuiltAndPreferredOverProjects()
    {
        Touch("App.sln", "Tool.csproj");

        Assert.Equal("dotnet build \"App.sln\"", ProjectCommands.Resolve(null, "build", _root));
        Assert.Equal("dotnet test \"App.sln\"", ProjectCommands.Resolve(null, "test", _root));
    }

    [Fact]
    public void SlnxIsPreferredOverSln()
    {
        Touch("App.sln", "App.slnx");

        Assert.Equal("dotnet build \"App.slnx\"", ProjectCommands.Resolve(null, "build", _root));
    }

    [Fact]
    public void SingleProjectIsBuilt()
    {
        Touch("App.csproj");

        Assert.Equal("dotnet build \"App.csproj\"", ProjectCommands.Resolve(null, "build", _root));
    }

    [Fact]
    public void SeveralSolutionsOrUnsafeNamesSkipValidation()
    {
        Touch("A.sln", "B.sln");
        Assert.Null(ProjectCommands.Resolve(null, "build", _root));

        File.Delete(Path.Combine(_root, "A.sln"));
        File.Delete(Path.Combine(_root, "B.sln"));
        Touch("a$(id).csproj");
        Assert.Null(ProjectCommands.Resolve(null, "build", _root));
    }
}
