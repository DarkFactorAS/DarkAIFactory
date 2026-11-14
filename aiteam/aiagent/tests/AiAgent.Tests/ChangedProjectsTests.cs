using AiAgent.Runner;

namespace AiAgent.Tests;

public sealed class ChangedProjectsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aiagent-changed-" + Guid.NewGuid().ToString("N"));

    public ChangedProjectsTests()
    {
        Write("src/App/App.csproj", Project());
        Write("src/App/Services/Worker.cs", "class Worker {}");
        Write("src/Lib/Lib.csproj", Project());
        Write("src/Lib/Thing.cs", "class Thing {}");
        Write("tests/App.Tests/App.Tests.csproj",
            Project("<PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" />" +
                    "<ProjectReference Include=\"..\\..\\src\\App\\App.csproj\" />"));
        Write("tests/Lib.Tests/Lib.Tests.csproj",
            Project("<ProjectReference Include=\"../../src/Lib/Lib.csproj\" />", isTest: true));
        Write("README.md", "# repo");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ChangedFileBuildsItsNearestProjectAndTestsTheProjectsReferencingIt()
    {
        var plan = ChangedProjects.Resolve(_root, ["src/App/Services/Worker.cs"]);

        Assert.Equal(["src/App/App.csproj"], plan.BuildProjects);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj"], plan.TestProjects);
    }

    [Fact]
    public void TestProjectsAreFoundThroughTransitiveReferences()
    {
        // App references Lib, so a change in Lib must also run App.Tests.
        Write("src/App/App.csproj", Project("<ProjectReference Include=\"..\\Lib\\Lib.csproj\" />"));

        var plan = ChangedProjects.Resolve(_root, ["src/Lib/Thing.cs"]);

        Assert.Equal(["src/Lib/Lib.csproj"], plan.BuildProjects);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj", "tests/Lib.Tests/Lib.Tests.csproj"], plan.TestProjects);
    }

    [Fact]
    public void ChangedTestProjectIsBuiltAndTested()
    {
        var plan = ChangedProjects.Resolve(_root, ["tests/Lib.Tests/LibTests.cs"]);

        Assert.Equal(["tests/Lib.Tests/Lib.Tests.csproj"], plan.BuildProjects);
        Assert.Equal(["tests/Lib.Tests/Lib.Tests.csproj"], plan.TestProjects);
    }

    [Fact]
    public void FilesOutsideAnyProjectGiveAnEmptyPlan()
    {
        var plan = ChangedProjects.Resolve(_root, ["README.md", "docs/guide.md"]);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void ChangesInSeveralProjectsBuildEachOnce()
    {
        var plan = ChangedProjects.Resolve(_root, ["src/App/Program.cs", "src/App/Services/Worker.cs", "src/Lib/Thing.cs"]);

        Assert.Equal(["src/App/App.csproj", "src/Lib/Lib.csproj"], plan.BuildProjects);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj", "tests/Lib.Tests/Lib.Tests.csproj"], plan.TestProjects);
    }

    [Fact]
    public void SnapshotReportsAddedModifiedAndDeletedFilesButIgnoresBuildOutput()
    {
        var before = WorkspaceSnapshot.Capture(_root);
        Write("src/App/New.cs", "class New {}");
        Write("src/Lib/Thing.cs", "class Thing { int changed; }");
        File.Delete(Path.Combine(_root, "README.md"));
        Write("src/App/bin/Debug/App.dll", "binary");
        Write("src/App/obj/project.assets.json", "{}");

        var changed = WorkspaceSnapshot.Capture(_root).ChangedSince(before);

        Assert.Equal(["README.md", "src/App/New.cs", "src/Lib/Thing.cs"], changed);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Project(string items = "", bool isTest = false) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">" +
        (isTest ? "<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>" : string.Empty) +
        $"<ItemGroup>{items}</ItemGroup></Project>";
}
