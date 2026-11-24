using AiAgent.Core;
using AiAgent.Metrics;
using AiAgent.Capabilities.Jira;
using AiAgent.LanguageModel;
using AiAgent.GitHub;
using AiAgent.Repositories;
using AiAgent.Runner;
using DFCommonLib.Logger;

namespace AiAgent.Capabilities.Review;

/// <summary>
/// Handles unresolved review comments on the tracked pull requests (agent_pull_requests) in the agent's projects,
/// whichever agent opened them: runs the runner in
/// 'review' mode, accepts only changes to cited files, builds and tests, and pushes without force.
/// The number of attempts per comment is limited and recorded in agent_review_attempts.
/// </summary>
public sealed class ReviewCapability(
    ReviewOptions options,
    IAgentPullRequestRepository pullRequests,
    IReviewAttemptStore attempts,
    IGitRunner gitRunner,
    RunnerClient runnerClient,
    HttpMessageHandler? gitHubHandler = null,
    Uri? gitHubApiUrl = null,
    Func<IJiraClient>? createJiraClient = null,
    AiAgent.Metrics.IAgentRunRecorder? recorder = null) : IAgentCapability
{
    private const string Category = "Review";
    internal const string ReadyToMergeLabel = "aiagent-ready-to-merge";

    public AgentCapabilityKind Kind => AgentCapabilityKind.Review;

    private static string ProjectKeyOf(string issueKey)
    {
        var dash = issueKey.LastIndexOf('-');
        return dash > 0 ? issueKey[..dash] : issueKey;
    }

    public async Task RunCycleAsync(AgentContext context, CancellationToken cancellationToken)
    {
        // Every tracked pull request in the agent's projects, not only the ones this agent opened: with separate
        // agents the builder opens the pull request and the reviewer fixes its comments. The per-issue claim keeps
        // two reviewers off the same pull request.
        var tracked = pullRequests.GetAllStored()
            .Where(stored => context.ProjectKeys.Contains(
                ProjectKeyOf(stored.PullRequest.JiraKey), StringComparer.OrdinalIgnoreCase))
            .ToList();

        using var client = new ReviewGitHubClient(
            options.RepositoryToken, options.ReviewerLogin, options.MaxAttempts, attempts, gitHubHandler, gitHubApiUrl);
        if (!await HandleMergeRequestsAsync(context, client, tracked, cancellationToken) || tracked.Count == 0)
        {
            return;
        }

        var open = await client.ListOpenPullRequestsAsync(tracked, cancellationToken);
        foreach (var pullRequest in open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var claim = context.ClaimLock.TryAcquire(context.AgentId, pullRequest.PullRequest.JiraKey);
            if (claim is null)
            {
                Log($"PR #{pullRequest.PullRequest.PullRequestNumber}: {pullRequest.PullRequest.JiraKey} is claimed elsewhere; skipping.");
                continue;
            }

            var scan = await client.ScanCommentsAsync(pullRequest, cancellationToken);
            LogScan(pullRequest, scan);
            if (scan.Actionable.Count == 0)
            {
                await LogIfReadyToMergeAsync(client, pullRequest, 0, cancellationToken);
                continue;
            }

            await TryUpdateReadinessLabelAsync(pullRequest.PullRequest.JiraKey, false, cancellationToken);
            try
            {
                int? failedCount = null;
                await (recorder ?? AiAgent.Metrics.NullAgentRunRecorder.Instance)
                    .Bind(context, Kind)(pullRequest.PullRequest.JiraKey, async run =>
                    {
                        failedCount = await FixPullRequestAsync(
                            context, client, pullRequest, scan.Actionable, scan.ThreadMembers, run, cancellationToken);
                    });
                if (failedCount is { } completedFailedCount)
                {
                    await LogIfReadyToMergeAsync(client, pullRequest, completedFailedCount, cancellationToken);
                }
            }
            catch (RunnerBusyException)
            {
                Log("The runner is busy; retrying in the next cycle.");
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AgentLog.LogOutput(DFLogLevel.ERROR, Category,
                    $"PR #{pullRequest.PullRequest.PullRequestNumber}: review cycle failed: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// Handles issues carrying the merge-requested label (set by the dashboard). Returns false when the runner is
    /// busy, so the rest of the cycle is skipped like for review fixes.
    /// </summary>
    private async Task<bool> HandleMergeRequestsAsync(
        AgentContext context, ReviewGitHubClient client, IReadOnlyList<StoredAgentPullRequest> tracked,
        CancellationToken cancellationToken)
    {
        if (createJiraClient is null || string.IsNullOrWhiteSpace(options.MergeRequestedLabel))
        {
            return true;
        }

        IJiraClient? jira = null;
        try
        {
            jira = createJiraClient();
            var keys = string.Join(", ", context.ProjectKeys.Select(key => $"\"{JiraQuery.EscapeJql(key)}\""));
            if (keys.Length == 0)
            {
                return true;
            }

            var issues = await jira.SearchAsync(
                $"project in ({keys}) AND labels = \"{JiraQuery.EscapeJql(options.MergeRequestedLabel)}\" ORDER BY updated ASC",
                cancellationToken);
            foreach (var issue in issues)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var claim = context.ClaimLock.TryAcquire(context.AgentId, issue.Key);
                if (claim is null)
                {
                    Log($"{issue.Key}: merge request skipped; the issue is claimed elsewhere.");
                    continue;
                }

                try
                {
                    var rows = tracked.Where(stored =>
                        string.Equals(stored.PullRequest.JiraKey, issue.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                    var handler = new MergeRequestHandler(
                        options, client, gitRunner, new RunnerFixRunner(runnerClient, context.Agent, options, RunnerModes.Merge));
                    MergeRequestResult? result = null;
                    await (recorder ?? AiAgent.Metrics.NullAgentRunRecorder.Instance)
                        .Bind(context, Kind)(issue.Key, async run =>
                        {
                            result = await handler.HandleAsync(issue.Key, rows, cancellationToken);
                            switch (result.Outcome)
                            {
                                case MergeRequestOutcome.Merged:
                                    run.Succeed();
                                    break;
                                case MergeRequestOutcome.Failed:
                                    run.Fail(result.Reason ?? "The merge request failed.");
                                    break;
                                default:
                                    // Waiting for checks: a push is work, a plain wait is not.
                                    if (result.Pushed) run.Succeed(); else run.Discard();
                                    break;
                            }
                        });
                    if (result is { Outcome: MergeRequestOutcome.Merged })
                    {
                        await jira.RemoveLabelAsync(issue.Key, options.MergeRequestedLabel, cancellationToken);
                    }
                    else if (result is { Outcome: MergeRequestOutcome.Failed } failed)
                    {
                        await FailMergeRequestAsync(jira, issue.Key, failed.Reason, cancellationToken);
                    }
                }
                catch (RunnerBusyException)
                {
                    Log("The runner is busy; retrying the merge request in the next cycle.");
                    return false;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AgentLog.LogOutput(DFLogLevel.ERROR, Category, $"{issue.Key}: merge request failed: {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AgentLog.LogOutput(DFLogLevel.WARNING, Category, $"Could not process merge requests: {exception.Message}");
        }
        finally
        {
            (jira as IDisposable)?.Dispose();
        }

        return true;
    }

    private async Task FailMergeRequestAsync(
        IJiraClient jira, string issueKey, string? reason, CancellationToken cancellationToken)
    {
        await jira.AddLabelAsync(issueKey, options.FailureLabel, cancellationToken);
        await jira.RemoveLabelAsync(issueKey, options.MergeRequestedLabel, cancellationToken);
        await jira.AddCommentAsync(
            issueKey,
            $"The requested merge was not completed: {reason ?? "unknown reason"}\n" +
            $"The `{options.FailureLabel}` label was added. Remove it and request the merge again after resolving the problem.",
            cancellationToken);
    }

    /// <summary>
    /// A pull request is ready to merge when no review thread is unresolved, no fix failed, and it either
    /// received at least one review comment thread or the reviewer left the approval phrase.
    /// </summary>
    internal static bool IsReadyToMerge(int totalThreads, int unresolvedThreads, int failedCount, bool reviewerApproved = false) =>
        unresolvedThreads == 0 && failedCount == 0 && (totalThreads > 0 || reviewerApproved);

    /// <summary>
    /// A pull request may be considered for automatic merge when no review thread is unresolved and no fix
    /// failed. Unlike <see cref="IsReadyToMerge"/>, it does not require a review thread: an approved pull
    /// request without threads is still eligible, and <see cref="AutoMergePolicy.BlockReason"/> decides the rest.
    /// </summary>
    internal static bool IsMergeCandidate(int unresolvedThreads, int failedCount) =>
        unresolvedThreads == 0 && failedCount == 0;

    private async Task TryUpdateReadinessLabelAsync(string issueKey, bool ready, CancellationToken cancellationToken)
    {
        try
        {
            if (createJiraClient is null)
            {
                return;
            }

            var jira = createJiraClient();
            try
            {
                await SetReadinessLabelAsync(jira, issueKey, ready, cancellationToken);
            }
            finally
            {
                (jira as IDisposable)?.Dispose();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AgentLog.LogOutput(DFLogLevel.WARNING, Category,
                $"{issueKey}: could not update the merge-readiness Jira label: {exception.Message}");
        }
    }

    private async Task LogIfReadyToMergeAsync(
        ReviewGitHubClient client, StoredAgentPullRequest pullRequest, int failedCount,
        CancellationToken cancellationToken)
    {
        // Read the head before counting so the counts can be tied to the commit they were checked against.
        var checkedHead = options.AutoMergeEnabled
            ? (await client.GetHeadAsync(pullRequest, cancellationToken)).Sha
            : null;
        var counts = await client.CountThreadsAsync(pullRequest, cancellationToken);
        if (counts is not { } threads)
        {
            return;
        }

        var prefix = $"PR #{pullRequest.PullRequest.PullRequestNumber} ({pullRequest.PullRequest.JiraKey})";
        var candidate = IsMergeCandidate(threads.Unresolved, failedCount);
        var readyToMerge = IsReadyToMerge(threads.Total, threads.Unresolved, failedCount);
        PullRequestMergeInfo? info = null;
        if (candidate && options.AutoMergeEnabled)
        {
            try
            {
                info = await client.GetMergeInfoAsync(pullRequest, options.ApprovalPhrase, cancellationToken);
                if (info.ReviewerApproved
                    && !await IsLatestReviewerReviewApprovalAsync(pullRequest, cancellationToken))
                {
                    info = info with { ReviewerApproved = false };
                }

                readyToMerge = IsReadyToMerge(threads.Total, threads.Unresolved, failedCount, info.ReviewerApproved);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AgentLog.LogOutput(DFLogLevel.WARNING, Category, $"{prefix}: automatic merge failed: {exception.Message}");
            }
        }
        else if (candidate && !readyToMerge)
        {
            try
            {
                var approved = await client.HasReviewerApprovalAsync(pullRequest, options.ApprovalPhrase, cancellationToken)
                    && await IsLatestReviewerReviewApprovalAsync(pullRequest, cancellationToken);
                readyToMerge = IsReadyToMerge(threads.Total, threads.Unresolved, failedCount, approved);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AgentLog.LogOutput(DFLogLevel.WARNING, Category, $"{prefix}: could not check the reviewer approval: {exception.Message}");
            }
        }

        await TryUpdateReadinessLabelAsync(pullRequest.PullRequest.JiraKey, readyToMerge, cancellationToken);

        if (!candidate)
        {
            return;
        }

        if (readyToMerge)
        {
            Log($"{prefix} is ready to be merged: all review comments are resolved.");
        }

        if (!options.AutoMergeEnabled || info is null)
        {
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(checkedHead)
                || !string.Equals(checkedHead, info.HeadSha, StringComparison.OrdinalIgnoreCase))
            {
                Log($"{prefix}: not merged automatically: the head changed while review threads were checked; retrying in the next cycle.");
                return;
            }

            if (info.BehindBy > 0 && !info.IsDraft && (info.ReviewerApproved || threads.Total > 0))
            {
                await client.UpdateBranchAsync(pullRequest, info.HeadSha, cancellationToken);
                Log($"{prefix}: branch was {info.BehindBy} commit(s) behind '{info.BaseBranch}'; updated it. Merging after the builds are green.");
                return;
            }

            var reason = AutoMergePolicy.BlockReason(info, threads.Total, threads.Unresolved, failedCount, options.AgentCodePaths);
            if (reason is not null)
            {
                Log($"{prefix}: not merged automatically: {reason}.");
                return;
            }

            await client.MergeAsync(pullRequest, info.HeadSha, options.MergeMethod, cancellationToken);
            Log($"{prefix} was merged automatically ({options.MergeMethod}).");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AgentLog.LogOutput(DFLogLevel.WARNING, Category, $"{prefix}: automatic merge failed: {exception.Message}");
        }
    }

    /// <summary>
    /// True when the reviewer's latest submitted review (pending reviews are ignored) is an APPROVED or COMMENTED
    /// review whose body contains the approval phrase. An earlier approval followed by a later non-approval review
    /// (changes requested, a comment without the phrase or a dismissed review) therefore no longer counts.
    /// </summary>
    private async Task<bool> IsLatestReviewerReviewApprovalAsync(
        StoredAgentPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var phrase = options.ApprovalPhrase;
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return false;
        }

        var segments = new Uri(pullRequest.PullRequest.RepositoryUrl).AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        var path = $"repos/{segments[0]}/{name}/pulls/{pullRequest.PullRequest.PullRequestNumber}/reviews";

        using var http = gitHubHandler is null ? new HttpClient() : new HttpClient(gitHubHandler, disposeHandler: false);
        http.BaseAddress = gitHubApiUrl ?? new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.RepositoryToken);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-AiAgent/1.0");

        static string? Normalize(string? login) =>
            login is not null && login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase) ? login[..^5] : login;

        var reviewer = Normalize(options.ReviewerLogin);
        var reviews = new List<System.Text.Json.Nodes.JsonNode>();
        for (var page = 1; ; page++)
        {
            using var response = await http.GetAsync(
                $"{path}?per_page={ReviewGitHubClient.PageSize}&page={page}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub request GET {path} failed with {(int)response.StatusCode}.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var list = string.IsNullOrWhiteSpace(text)
                ? []
                : System.Text.Json.Nodes.JsonNode.Parse(text)?.AsArray() ?? [];
            reviews.AddRange(list.Where(item => item is not null)!);
            if (list.Count < ReviewGitHubClient.PageSize)
            {
                break;
            }
        }

        var latest = reviews
            .Where(review => string.Equals(
                Normalize(review["user"]?["login"]?.GetValue<string>()), reviewer, StringComparison.OrdinalIgnoreCase))
            .Where(review => !string.Equals(
                review["state"]?.GetValue<string>(), "PENDING", StringComparison.OrdinalIgnoreCase))
            .Select((review, order) => (review, order))
            .OrderBy(item => DateTimeOffset.TryParse(
                item.review["submitted_at"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var submitted)
                ? submitted
                : DateTimeOffset.MinValue)
            .ThenBy(item => item.order)
            .Select(item => item.review)
            .LastOrDefault();
        if (latest is null)
        {
            return false;
        }

        var state = latest["state"]?.GetValue<string>();
        return (string.Equals(state, "APPROVED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "COMMENTED", StringComparison.OrdinalIgnoreCase))
            && (latest["body"]?.GetValue<string>() ?? string.Empty).Contains(phrase, StringComparison.Ordinal);
    }

    internal static async Task SetReadinessLabelAsync(
        IJiraClient jira, string issueKey, bool readyToMerge, CancellationToken cancellationToken)
    {
        var issue = await jira.GetIssueAsync(issueKey, cancellationToken);
        var hasLabel = issue.Labels.Contains(ReadyToMergeLabel, StringComparer.Ordinal);
        if (readyToMerge && !hasLabel)
        {
            await jira.AddLabelAsync(issueKey, ReadyToMergeLabel, cancellationToken);
        }
        else if (!readyToMerge && hasLabel)
        {
            await jira.RemoveLabelAsync(issueKey, ReadyToMergeLabel, cancellationToken);
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
        AgentLog.LogOutput(scan.FailedComments > 0 ? DFLogLevel.WARNING : DFLogLevel.INFO, Category, message);
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
        AiAgent.Metrics.IAgentRun run,
        CancellationToken cancellationToken)
    {
        var number = pullRequest.PullRequest.PullRequestNumber;
        var (branch, headSha) = await client.GetHeadAsync(pullRequest, cancellationToken);
        if (comments.Any(comment => !string.Equals(comment.HeadSha, headSha, StringComparison.OrdinalIgnoreCase)))
        {
            Log($"PR #{number}: head moved while reading comments; retrying next cycle.");
            run.Discard();
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
                run.Discard();
                return null;
            }

            var runner = new RunnerFixRunner(runnerClient, context.Agent, options);
            // Project code (build/test) must never execute in the orchestrator container, which holds the mounted
            // configuration and private Git metadata. Validation runs inside aiagent-runner, which builds the projects
            // containing the changed files and runs the test projects that reference them; a non-zero runner exit
            // status fails the attempt.
            var flow = new ReviewFixFlow(
                context.AgentId, runner, git, NoLocalCommands.Instance, attempts, client,
                options.MaxAttempts, buildCommand: null, testCommand: null);
            Log($"PR #{number}: fixing {comments.Count} comment(s) one at a time.");
            var result = await flow.RunAsync(pullRequest, branch, comments, workspace, cancellationToken, threadMembers);
            quarantineWorkspace = result.QuarantineWorkspace;
            Log($"PR #{number}: fix run finished with {result.Outcome}; " +
                $"{result.PushedCount} pushed, {result.FailedCount} failed" +
                (result.Error is null ? "." : $". Last error: {result.Error}"));
            MapOutcome(run, result);
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

    /// <summary>Succeeded only when a fix was pushed; a discarded fix or reached attempt limit is Failed.</summary>
    internal static void MapOutcome(AiAgent.Metrics.IAgentRun run, FixResult result)
    {
        switch (result.Outcome)
        {
            case FixOutcome.Pushed:
                run.Succeed();
                break;
            case FixOutcome.Skipped:
                // Every comment had used its attempts: nothing was attempted, so no run is recorded.
                run.Discard();
                break;
            case FixOutcome.NoChanges:
                run.Fail("The fix was discarded: the agent made no change.");
                break;
            default:
                run.Fail(result.Error ?? "The fix failed or the attempt limit was reached.");
                break;
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
            AgentLog.LogOutput(DFLogLevel.WARNING, Category, $"Could not delete directory '{path}': {exception.Message}");
        }
    }

    private static void Log(string message) => AgentLog.LogOutput(DFLogLevel.INFO, Category, message);

    /// <summary>Refuses local execution; validation belongs to aiagent-runner.</summary>
    private sealed class NoLocalCommands : IProjectCommandRunner
    {
        public static readonly NoLocalCommands Instance = new();

        public Task<string?> RunAsync(string command, string workspacePath, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("project commands are not executed in the orchestrator; validation runs in aiagent-runner.");
    }

    private sealed class RunnerFixRunner(
        RunnerClient client, AgentDefinition agent, ReviewOptions options, string mode = RunnerModes.Review) : IFixRunner
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
                        provider, agent.LlmModel ?? string.Empty, mode, workspacePath,
                        PromptComposer.Compose(agent, prompt),
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
