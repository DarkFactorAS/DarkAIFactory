using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiDashboard.Tasks;
using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

public interface IJiraLabelClient
{
    Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken);
}

// Adds one label to a Jira issue (PUT update.labels.add); no other field is changed.
public sealed class JiraLabelClient : IJiraLabelClient
{
    private readonly HttpClient _httpClient;

    public JiraLabelClient(IntegrationSettings settings, HttpClient httpClient)
    {
        _httpClient = httpClient;
        if (Uri.TryCreate(settings.Jira.JiraBaseUrl, UriKind.Absolute, out var baseUrl))
        {
            _httpClient.BaseAddress = baseUrl;
        }

        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{settings.Jira.JiraEmail}:{settings.Jira.JiraApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-AiDashboard/1.0");
    }

    public static string BuildBody(string label) =>
        JsonSerializer.Serialize(new { update = new { labels = new[] { new { add = label } } } });

    public async Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken)
    {
        using var content = new StringContent(BuildBody(label), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync(
            $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed record ApprovalLabels(
    string ApprovalLabel,
    string ApprovedLabel,
    string MergeRequestedLabel = WorkflowApprovalLabelSource.DefaultMergeRequestedLabel,
    string ReadyToMergeLabel = WorkflowApprovalLabelSource.DefaultReadyToMergeLabel,
    string FailureLabel = WorkflowApprovalLabelSource.DefaultFailureLabel);

public interface IApprovalLabelSource
{
    ApprovalLabels Get();
}

// Reads workflow_labels for the aiagent; missing keys fall back to the agent's defaults.
public sealed class WorkflowApprovalLabelSource(WorkflowLabelRepository repository) : IApprovalLabelSource
{
    public const string DefaultApprovalLabel = "aiagent-awaiting-approval";
    public const string DefaultApprovedLabel = "aiagent-approved";
    public const string DefaultMergeRequestedLabel = "aiagent-merge-requested";
    public const string DefaultReadyToMergeLabel = "aiagent-ready-to-merge";
    public const string DefaultFailureLabel = "aiagent-failed";

    public ApprovalLabels Get()
    {
        var labels = repository.Load("aiagent");
        return new ApprovalLabels(
            Pick(labels, "ApprovalLabel", DefaultApprovalLabel),
            Pick(labels, "ApprovedLabel", DefaultApprovedLabel),
            Pick(labels, "MergeRequestedLabel", DefaultMergeRequestedLabel),
            DefaultReadyToMergeLabel,
            Pick(labels, "FailureLabel", DefaultFailureLabel));
    }

    private static string Pick(WorkflowLabels labels, string key, string fallback)
    {
        try
        {
            return labels.Get(key);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}

public static partial class JiraApprovalEndpoint
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$")]
    private static partial Regex KeyPattern();

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/tasks/{key}/approve", HandleAsync).RequireAuthorization();

    public static IResult ConfigAsync(
        AiDashboard.Agents.IAgentDefinitionRepository repository, IApprovalLabelSource labelSource)
    {
        var keys = repository.ListProjectKeys();
        var labels = labelSource.Get();
        return Results.Ok(new
        {
            projectKey = keys.FirstOrDefault(),
            projectKeys = keys,
            approvalLabel = labels.ApprovalLabel,
            approvedLabel = labels.ApprovedLabel,
            mergeRequestedLabel = labels.MergeRequestedLabel,
            readyToMergeLabel = labels.ReadyToMergeLabel
        });
    }

    public static async Task<IResult> HandleAsync(
        IJiraLabelClient client, IActiveAiTaskService activeTasks, IApprovalLabelSource labelSource,
        string key, CancellationToken cancellationToken)
    {
        if (!KeyPattern().IsMatch(key))
        {
            return Results.BadRequest(new { error = "key must be a valid Jira issue key." });
        }

        try
        {
            var task = (await activeTasks.GetActiveAsync(cancellationToken))
                .FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var labels = labelSource.Get();
            var taskLabels = task.Labels ?? [];
            if (!taskLabels.Contains(labels.ApprovalLabel, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Conflict(new { error = "The task is not awaiting approval." });
            }

            if (!taskLabels.Contains(labels.ApprovedLabel, StringComparer.OrdinalIgnoreCase))
            {
                await client.AddLabelAsync(task.Key, labels.ApprovedLabel, cancellationToken);
            }

            return Results.Ok(new { key = task.Key, label = labels.ApprovedLabel });
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or InvalidDataException or
                JsonException or KeyNotFoundException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraApproval", ex.Message);
            return Results.Problem("Unable to approve the task in Jira.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
