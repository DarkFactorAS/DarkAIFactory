using AiAgent.Core;
using AiAgent.LanguageModel;
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

    private static string ProjectKeyOf(string issueKey)
    {
        var dash = issueKey.LastIndexOf('-');
        return dash > 0 ? issueKey[..dash] : issueKey;
    }

    public async Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        var tracked = pullRequests.GetStoredForAgent(context.AgentId)
            .Where(stored => context.ProjectKeys.Contains(
                ProjectKeyOf(stored.PullRequest.JiraKey), StringComparer.OrdinalIgnoreCase))
            .ToList();
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
            LogScan(pullRequest, scan);
            if (scan.Actionable.Count == 0)
            {
                await LogIfReadyToMergeAsync(client, pullRequest, cancellationToken);
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
                var failedCount = await FixPullRequestAsync(
                    context, client, pullRequest, scan.Actionable, scan.ThreadMembers, cancellationToken);
                if (failedCount == 0)
                {
                    await LogIfReadyToMergeAsync(client, pullRequest, cancellationToken);
                }
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

    /// <summary>A pull request is ready to merge when no review thread is unresolved and no fix failed.</summary>
    internal static bool IsReadyToMerge(int unresolvedThreads, int failedCount) =>
        unresolvedThreads == 0 && failedCount == 0;

    private async Task LogIfReadyToMergeAsync(
        ReviewGitHubClient client, StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var unresolved = await client.CountUnresolvedThreadsAsync(pullRequest, cancellationToken);
        if (unresolved is { } count && IsReadyToMerge(count, 0))
        {
            Log($"PR #{pullRequest.PullRequest.PullRequestNumber} ({pullRequest.PullRequest.JiraKey}) is ready to be merged: " +
                "all review comments are resolved.");
        }
    }

    private void LogScan(StoredAgentPullRequest pullRequest, ReviewCommentScan scan)
    {
        var prefix = $"PR #{pullRequest.PullRequest.PullRequestNumber} ({pullRequest.PullRequest.JiraKey})";
        if (scan.OpenComments == 0)
        {
            Log($"{prefix} has no unresolved review comments.");
            return;
        }

        var reviewer = options.ReviewerLogin;
        var message = $"{prefix} has {scan.OpenComments} unresolved review comment(s); {scan.Actionable.Count} actionable " +
            $"from '{reviewer}'" +
            (scan.HandledComments > 0 ? $", {scan.HandledComments} from '{reviewer}' already handled" : string.Empty) +
            (scan.FailedComments > 0
                ? $", {scan.FailedComments} from '{reviewer}' marked failed (terminal failure or attempts exhausted; see agent_review_attempts)"
                : string.Empty) + ".";
        DFLogger.LogOutput(scan.FailedComments > 0 ? DFLogLevel.WARNING : DFLogLevel.INFO, Category, message);
        if (scan.OtherAuthors.Count > 0)
        {
            var authors = string.Join(", ", scan.OtherAuthors.Select(author => $"{author.Key} ({author.Value})"));
            Log($"{prefix}: ignoring comment(s) not from '{reviewer}'. Reviewers found: {authors}.");
        }
    }

    /// <summary>Returns the failed count, or null when the run did not complete (head moved or branch mismatch).</summary>
    private async Task<int?> FixPullRequestAsync(
        AgentContext context,
        ReviewGitHubClient client,
        StoredAgentPullRequest pullRequest,
        IReadOnlyList<ReviewComment> comments,
        IReadOnlyList<ReviewThreadMember> threadMembers,
        CancellationToken cancellationToken)
    {
        var number = pullRequest.PullRequest.PullRequestNumber;
        var (branch, headSha) = await client.GetHeadAsync(pullRequest, cancellationToken);
        if (comments.Any(comment => !string.Equals(comment.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"PR #{number}: head moved while reading comments; retrying next cycle.");
            return null;
        }

        var git = new ReviewGit(gitRunner);
        var workspaceRoot = options.WorkspaceRoot ?? RunnerPolicy.WorkspaceRoot;
        var workspace = Path.Combine(workspaceRoot, Guid.NewGuid().ToString("N"), "checkout");
        var quarantineWorkspace = false;
        try
        {
            await git.CloneAsync(new Uri(pullRequest.PullRequest.RepositoryUrl), branch, workspace, cancellationToken);
            var revision = await git.GetRevisionAsync(workspace, cancellationToken);
            if (!string.Equals(revision, headSha, StringComparison.OrdinalIgnoreCase))
            {
                Log($"PR #{number}: branch is at {revision}, expected {headSha}; retrying next cycle.");
                return null;
            }

            var runner = new RunnerFixRunner(runnerClient, context.Agent, options);
            // Project code (build/test) must never execute in the orchestrator container, which holds the mounted
            // configuration and private Git metadata. Validation runs inside aiagent-runner: the build/test commands
            // are sent with the runner request and a non-zero runner exit status fails the attempt. Local command
            // execution is only used when a command runner is explicitly injected (e.g. by tests).
            var flow = new ReviewFixFlow(
                context.AgentId, runner, git, commands ?? NoLocalCommands.Instance, attempts, client,
                options.MaxAttempts,
                commands is null ? null : options.BuildCommand,
                commands is null ? null : options.TestCommand);
            Log($"PR #{number}: fixing {comments.Count} comment(s) one at a time.");
            var result = await flow.RunAsync(pullRequest, branch, comments, workspace, cancellationToken, threadMembers);
            quarantineWorkspace = result.QuarantineWorkspace;
            Log($"PR #{number}: fix run finished with {result.Outcome}; " +
                $"{result.PushedCount} pushed, {result.FailedCount} failed" +
                (result.Error is null ? "." : $". Last error: {result.Error}"));
            return result.FailedCount;
        }
        finally
        {
            if (quarantineWorkspace || cancellationToken.IsCancellationRequested)
            {
                Log($"PR #{number}: quarantining checkout '{workspace}' after runner timeout or cancellation.");
            }
            else
            {
                TryDelete(Path.GetDirectoryName(workspace)!);
                // GitRunner clones with --separate-git-dir into its private metadata root, outside the workspace;
                // remove that object database too so per-attempt clones do not accumulate on disk.
                TryDelete(Builder.BuilderCapability.GitMetadataDirectoryFor(workspace));
            }
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
            DFLogger.LogOutput(DFLogLevel.WARNING, Category, $"Could not delete directory '{path}': {exception.Message}");
        }
    }

    private static void Log(string message) => DFLogger.LogOutput(DFLogLevel.INFO, Category, message);

    /// <summary>Refuses local execution; validation belongs to aiagent-runner.</summary>
    private sealed class NoLocalCommands : IProjectCommandRunner
    {
        public static readonly NoLocalCommands Instance = new();

        public Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("project commands are not executed in the orchestrator; validation runs in aiagent-runner.");
    }

    private sealed class RunnerFixRunner(RunnerClient client, AgentDefinition agent, ReviewOptions options) : IFixRunner
    {
        public async Task RunAsync(string workspacePath, string prompt, CancellationToken cancellationToken)
        {
            var provider = new LanguageModelFactory().Create(agent).Provider;
            // Closing the request on timeout makes the runner cancel the job (it observes request abortion).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RunnerTimeout);
            RunnerResponse response;
            try
            {
                response = await client.RunAsync(
                    new RunnerRequest(
                        provider, agent.LlmModel ?? string.Empty, RunnerModes.Review, workspacePath,
                        PromptComposer.Compose(agent, prompt), options.BuildCommand, options.TestCommand,
                        provider == "copilot" ? options.CopilotToken : null),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The runner did not finish within {options.RunnerTimeout.TotalMinutes:0} minute(s).");
            }

            if (response.ExitCode != 0)
            {
                throw new InvalidOperationException($"The runner exited with code {response.ExitCode}: {response.Output}");
            }
        }
    }
}
