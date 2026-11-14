using System.Xml.Linq;

namespace AiAgent.Runner;

/// <summary>
/// File state of a workspace (relative path to size and last write time), used to find the files the LLM changed.
/// The runner has no access to the checkout's Git metadata (it lives outside the workspace on purpose), so it
/// compares a snapshot taken before the LLM run with one taken after it.
/// </summary>
internal sealed class WorkspaceSnapshot
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "TestResults"
    };

    private readonly Dictionary<string, (long Length, DateTime LastWriteUtc)> _files;

    private WorkspaceSnapshot(Dictionary<string, (long, DateTime)> files) => _files = files;

    public static WorkspaceSnapshot Capture(string root)
    {
        var files = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        foreach (var path in EnumerateFiles(root))
        {
            var info = new FileInfo(path);
            files[Relative(root, path)] = (info.Length, info.LastWriteTimeUtc);
        }

        return new WorkspaceSnapshot(files);
    }

    /// <summary>Added, modified and deleted files, relative to the workspace with '/' separators.</summary>
    public IReadOnlyList<string> ChangedSince(WorkspaceSnapshot before) =>
        _files.Where(file => !before._files.TryGetValue(file.Key, out var old) || old != file.Value)
            .Select(file => file.Key)
            .Concat(before._files.Keys.Where(path => !_files.ContainsKey(path)))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Files under <paramref name="root"/>, skipping build output, VCS and tool directories.</summary>
    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory);
                directories = Directory.EnumerateDirectories(directory);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (directory != root || !string.Equals(Path.GetFileName(file), ".git", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }

            foreach (var child in directories)
            {
                // Never follow links: the LLM could point one outside the workspace.
                if (!SkippedDirectories.Contains(Path.GetFileName(child)) &&
                    !new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }
    }

    internal static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>The projects to build and the test projects to run for a set of changed files.</summary>
internal sealed record ProjectValidationPlan(IReadOnlyList<string> BuildProjects, IReadOnlyList<string> TestProjects)
{
    public bool IsEmpty => BuildProjects.Count == 0 && TestProjects.Count == 0;
}

/// <summary>
/// Maps changed files to .NET projects: a changed file belongs to the project(s) in the nearest folder at or above
/// it that holds a project file. Those projects are built. Test projects are every test project that is one of
/// them or references one of them, directly or through other projects.
/// </summary>
internal static class ChangedProjects
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    public static ProjectValidationPlan Resolve(string root, IEnumerable<string> changedFiles)
    {
        var projects = WorkspaceSnapshot.EnumerateFiles(root)
            .Where(IsProjectFile)
            .Select(path => WorkspaceSnapshot.Relative(root, path))
            .ToHashSet(StringComparer.Ordinal);
        var projectsByDirectory = projects
            .GroupBy(DirectoryOf, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        var changedProjects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in changedFiles)
        {
            if (projects.Contains(file))
            {
                changedProjects.Add(file);
                continue;
            }

            for (var directory = DirectoryOf(file); directory is not null; directory = Parent(directory))
            {
                if (projectsByDirectory.TryGetValue(directory, out var owners))
                {
                    changedProjects.UnionWith(owners);
                    break;
                }
            }
        }

        if (changedProjects.Count == 0)
        {
            return new ProjectValidationPlan([], []);
        }

        // Reverse project references: project -> projects that reference it.
        var dependents = projects.ToDictionary(project => project, _ => new List<string>(), StringComparer.Ordinal);
        var testProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var (references, isTest) = ReadProject(root, project);
            if (isTest)
            {
                testProjects.Add(project);
            }

            foreach (var reference in references.Where(dependents.ContainsKey))
            {
                dependents[reference].Add(project);
            }
        }

        var affected = new HashSet<string>(changedProjects, StringComparer.Ordinal);
        var pending = new Queue<string>(changedProjects);
        while (pending.Count > 0)
        {
            foreach (var dependent in dependents[pending.Dequeue()].Where(affected.Add))
            {
                pending.Enqueue(dependent);
            }
        }

        return new ProjectValidationPlan(
            changedProjects.ToList(),
            affected.Where(testProjects.Contains).Order(StringComparer.Ordinal).ToList());
    }

    private static bool IsProjectFile(string path) =>
        ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Referenced projects (relative to the workspace) and whether this is a test project.</summary>
    private static (IReadOnlyList<string> References, bool IsTest) ReadProject(string root, string project)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(Path.Combine(root, project));
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException)
        {
            return ([], false);
        }

        var directory = Path.GetDirectoryName(Path.Combine(root, project))!;
        var references = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(include => WorkspaceSnapshot.Relative(
                root, Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)))))
            .ToList();
        var isTest =
            document.Descendants().Any(element =>
                element.Name.LocalName == "IsTestProject" &&
                string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) ||
            document.Descendants().Any(element =>
                element.Name.LocalName == "PackageReference" &&
                string.Equals(element.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        return (references, isTest);
    }

    /// <summary>Folder of a relative path ("" for the workspace root).</summary>
    private static string DirectoryOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    private static string? Parent(string directory) =>
        directory.Length == 0 ? null : DirectoryOf(directory);
}
