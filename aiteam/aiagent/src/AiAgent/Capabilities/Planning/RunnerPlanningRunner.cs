using AiAgent.Core;
using AiAgent.GitHub;
using AiAgent.Providers;
using AiAgent.Repositories;
using AiAgent.Runner;

namespace AiAgent.Capabilities.Planning;

/// <summary>Clones the repository into a runner workspace and runs the language model in 'plan' mode.</summary>
internal sealed class RunnerPlanningRunner(
    RunnerClient runnerClient,
    IAgentPullRequestRepository pullRequests,
    string? workspaceRoot = null) : IPlanningRunner
{
    private readonly string _workspaceRoot = workspaceRoot ?? RunnerPolicy.WorkspaceRoot;

    public async Task<string> GeneratePlanAsync(
        AgentContext context, IntegrationSettings settings, PlanningConfiguration configuration,
        JiraIssue issue, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var workspace = Path.Combine(_workspaceRoot, $"plan-{id}");
        var metadata = Path.Combine(Path.GetTempPath(), $"aiagent-plan-{id}");
        try
        {
            var core = CreateCore(settings, configuration, metadata);
            await core.CloneAndBranchAsync(workspace, issue.Key, issue.Summary, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(issue.ParentKey))
            {
                await TryCheckOutParentBranchAsync(settings, configuration, metadata, workspace, issue.ParentKey, cancellationToken);
            }

            var provider = NormalizeProvider(context.Agent.LlmProvider);
            var response = await runnerClient.RunAsync(
                new RunnerRequest(
                    provider,
                    context.Agent.LlmModel?.Trim() ?? string.Empty,
                    RunnerModes.Plan,
                    workspace,
                    PromptComposer.Compose(context.Agent, PlanningPrompts.BuildTriagePrompt(issue)),
                    CopilotToken: provider == "copilot" ? settings.GitHub.AgentGitHubToken : null),
                cancellationToken);
            if (response.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The runner failed planning {issue.Key} with exit code {response.ExitCode}: " +
                    PlanningPrompts.LimitForComment(response.Output));
            }

            return response.Output;
        }
        finally
        {
            DeleteQuietly(workspace);
            DeleteQuietly(metadata);
        }
    }

    public async Task<string> CreateParentPullRequestAsync(
        AgentContext context, IntegrationSettings settings, PlanningConfiguration configuration,
        string headBranch, string issueKey, string summary, CancellationToken cancellationToken)
    {
        var core = CreateCore(settings, configuration, Path.Combine(Path.GetTempPath(), "aiagent-plan-pr"));
        var record = await core.OpenPullRequestAsync(
            context.AgentId, issueKey, summary, headBranch, null,
            $"All sub-tasks for Jira task {issueKey} are complete.\n\nCreated automatically by aiAgent.",
            cancellationToken);
        return record.PullRequestUrl;
    }

    internal static string NormalizeProvider(string provider) =>
        provider.Trim().ToLowerInvariant() switch
        {
            "claude" or "anthropic" => "claude",
            "copilot" or "github" => "copilot",
            _ => throw new InvalidOperationException($"Unsupported LLM provider '{provider}'.")
        };

    private GitHubCore CreateCore(IntegrationSettings settings, PlanningConfiguration configuration, string metadata)
    {
        var token = string.IsNullOrWhiteSpace(settings.GitHub.GitHubRepositoryToken)
            ? settings.GitHub.AgentGitHubToken
            : settings.GitHub.GitHubRepositoryToken;
        return new GitHubCore(new GitRunner(token, metadata), configuration.GitHubRepositoryUrl, token, pullRequests);
    }

    // Sub-tasks plan against the parent's integration branch when it already exists on origin.
    private static async Task TryCheckOutParentBranchAsync(
        IntegrationSettings settings, PlanningConfiguration configuration, string metadata, string workspace,
        string parentKey, CancellationToken cancellationToken)
    {
        var token = string.IsNullOrWhiteSpace(settings.GitHub.GitHubRepositoryToken)
            ? settings.GitHub.AgentGitHubToken
            : settings.GitHub.GitHubRepositoryToken;
        var git = new GitRunner(token, metadata);
        var branch = JiraWorkflowRules.ParentBranchName(parentKey);
        try
        {
            await git.RunAsync(workspace, ["fetch", "--quiet", "origin", branch], true, cancellationToken);
            await git.RunAsync(workspace, ["switch", "--detach", $"origin/{branch}"], false, cancellationToken);
            await git.RunAsync(workspace, ["submodule", "sync", "--quiet", "--recursive"], false, cancellationToken);
            await git.RunAsync(workspace, ["submodule", "update", "--init", "--recursive"], true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The integration branch does not exist yet: plan against the default branch.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
