namespace AiAgent.GitHub;

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

public interface IGitRunner
{
    /// <param name="workingDirectory">Directory to run in (the checkout, or its parent while cloning).</param>
    /// <param name="withCredentials">Adds the GitHub token as an HTTP header for this command only.</param>
    Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool withCredentials,
        CancellationToken cancellationToken,
        bool allowExitCodeOne = false);
}
