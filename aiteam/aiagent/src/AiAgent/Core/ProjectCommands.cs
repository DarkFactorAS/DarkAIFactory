using DFCommonLib.Logger;

namespace AiAgent.Core;

/// <summary>
/// Chooses the build/test command run in aiagent-runner for a checkout. An explicitly configured command
/// (AIAGENT_BUILD_COMMAND / AIAGENT_TEST_COMMAND) is always used. Otherwise the repository root is inspected:
/// one solution (.slnx preferred over .sln) or one project gives <c>dotnet build|test &lt;file&gt;</c>; anything
/// else (no .NET entry point at the root, or several) skips validation, because a bare <c>dotnet build</c>
/// fails with MSB1003 when the root holds no project or solution file.
/// </summary>
public static class ProjectCommands
{
    private static readonly string[] SolutionPatterns = ["*.slnx", "*.sln"];
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    public static string? Resolve(string? configured, string verb, string workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string? Single(string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                var files = Directory.Exists(workspacePath)
                    ? Directory.GetFiles(workspacePath, pattern, SearchOption.TopDirectoryOnly)
                    : [];
                if (files.Length > 0)
                {
                    return files.Length == 1 ? Path.GetFileName(files[0]) : string.Empty;
                }
            }

            return null;
        }

        var entry = Single(SolutionPatterns) ?? Single(ProjectPatterns);
        if (string.IsNullOrEmpty(entry) || !IsSafeFileName(entry))
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, "ProjectCommands",
                (entry is null
                    ? "The repository root has no solution or project file"
                    : "The repository root has several solution or project files, or an unsafe file name") +
                $"; skipping 'dotnet {verb}'. Set AIAGENT_{verb.ToUpperInvariant()}_COMMAND to validate this repository.");
            return null;
        }

        return $"dotnet {verb} \"{entry}\"";
    }

    // The file name comes from the (untrusted) repository and is placed in a shell command.
    private static bool IsSafeFileName(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
