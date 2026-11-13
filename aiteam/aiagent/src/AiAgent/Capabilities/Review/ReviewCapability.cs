using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.Repositories;
using AiAgent.Runner;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Review;

/// <summary>
/// Handles unresolved review comments on the agent's own pull requests (agent_pull_requests): runs the runner in
/// 'review' mode, accepts only changes to cited files, builds and tests, and pushes without force.
/// The number of attempts per comment is limited and recorded in agent_review_attempts.
/// </summary>
public sealed class ReviewCapability(
    ReviewOptions options,
    IAgentPullRequestRepository pullRequests,
    IReviewAttemptStore attempts,
    IGitRunner gitRunner,
    RunnerClient runnerClient,
    IProjectCommandRunner? commands = null,
    HttpMessageHandler? gitHubHandler = null,
    Uri? gitHubApiUrl = null) : IAgentCapability
{
    private const string Category = "Review";

    public AgentCapabilityKind Kind => AgentCapabilityKind.Review;

    public async Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var tracked = pullRequests.GetStoredForAgent(context.AgentId);
        if (tracked.Count == 0)
        {
            return;
        }

        using var client = new ReviewGitHubClient(
            options.RepositoryToken, options.ReviewerLogin, options.MaxAttempts, attempts, gitHubHandler, gitHubApiUrl);
        var open = await client.ListOpenPullRequestsAsync(tracked, cancellationToken);
        foreach (var pullRequest in open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = await client.ScanCommentsAsync(pullRequest, cancellationToken);
            if (scan.Actionable.Count == 0)
            {
                continue;
            }

            using var claim = context.ClaimLock.TryAcquire(context.AgentId, pullRequest.PullRequest.JiraKey);
            if (claim is null)
            {
                Log($"PR #{pullRequest.PullRequest.PullRequestNumber}: {pullRequest.PullRequest.JiraKey} is claimed elsewhere; skipping.");
                continue;
            }

            try
            {
                await FixPullRequestAsync(context, client, pullRequest, scan.Actionable, cancellationToken);
            }
            catch (RunnerBusyException)
            {
                Log("The runner is busy; retrying in the next cycle.");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DFLogger.LogOutput(DFLogLevel.ERROR, Category,
                    $"PR #{pullRequest.PullRequest.PullRequestNumber}: review cycle failed: {exception.Message}");
            }
        }
    }

    private async Task FixPullRequestAsync(
        AgentContext context,
        ReviewGitHubClient client,
        StoredAgentPullRequest pullRequest,
        IReadOnlyList<ReviewComment> comments,
        CancellationToken cancellationToken)
    {
        var number = pullRequest.PullRequest.PullRequestNumber;
        var (branch, headSha) = await client.GetHeadAsync(pullRequest, cancellationToken);
        if (comments.Any(comment => !string.Equals(comment.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"PR #{number}: head moved while reading comments; retrying next cycle.");
            return;
        }

        var git = new ReviewGit(gitRunner);
        var workspaceRoot = options.WorkspaceRoot ?? RunnerPolicy.WorkspaceRoot;
        var workspace = Path.Combine(workspaceRoot, Guid.NewGuid().ToString("N"), "checkout");
        try
        {
            await git.CloneAsync(new Uri(pullRequest.PullRequest.RepositoryUrl), branch, workspace, cancellationToken);
            var revision = await git.GetRevisionAsync(workspace, cancellationToken);
            if (!string.Equals(revision, headSha, StringComparison.OrdinalIgnoreCase))
            {
                Log($"PR #{number}: branch is at {revision}, expected {headSha}; retrying next cycle.");
                return;
            }

            var runner = new RunnerFixRunner(runnerClient, context.Agent, options);
            var flow = new ReviewFixFlow(
                context.AgentId, runner, git, commands ?? new ProcessCommandRunner(), attempts,
                options.MaxAttempts, options.BuildCommand, options.TestCommand);
            var result = await flow.RunAsync(pullRequest, branch, comments, workspace, cancellationToken);
            Log($"PR #{number}: fix attempt finished with {result.Outcome}" +
                (result.Error is null ? "." : $": {result.Error}"));
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(workspace)!);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            DFLogger.LogOutput(DFLogLevel.WARNING, Category, $"Could not delete workspace '{path}': {exception.Message}");
        }
    }

    private static void Log(string message) => DFLogger.LogOutput(DFLogLevel.INFO, Category, message);

    private sealed class RunnerFixRunner(RunnerClient client, AgentDefinition agent, ReviewOptions options) : IFixRunner
    {
        public async Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken)
        {
            var provider = agent.LlmProvider.Trim().ToLowerInvariant() is "copilot" or "github" ? "copilot" : "claude";
            var response = await client.RunAsync(
                new RunnerRequest(
                    provider, agent.LlmModel ?? string.Empty, RunnerModes.Review, workspacePath,
                    PromptComposer.Compose(agent, prompt), options.BuildCommand, options.TestCommand,
                    provider == "copilot" ? options.CopilotToken : null),
                cancellationToken);
            if (response.ExitCode != 0)
            {
                throw new InvalidOperationException($"The runner exited with code {response.ExitCode}: {response.Output}");
            }
        }
    }
}
