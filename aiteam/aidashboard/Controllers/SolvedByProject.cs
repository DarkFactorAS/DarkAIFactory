using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Stats;

public sealed record SolvedByProject(string Project, int SolvedCount, int AiCount, int NonAiCount);

public sealed record SolvedIssue(string ProjectKey, bool IsAi);

public interface IJiraSolvedClient
{
    // Project key and AI flag (configured pickup or analyzed label) of every issue whose status category is Done.
    Task<IReadOnlyList<SolvedIssue>> GetSolvedIssuesAsync(CancellationToken cancellationToken);
}

public sealed class JiraSolvedClient : IJiraSolvedClient, IDisposable
{
    public const string SolvedJql = "statusCategory = Done";
    private static readonly (string Agent, string CodeKey)[] AiWorkLabelKeys =
    [
        ("aimanager", "PickupLabel"),
        ("aimanager", "AnalyzedLabel"),
        ("aiworker", "PickupLabel")
    ];

    private readonly Func<string, WorkflowLabels> _loadLabels;
    private readonly DashboardSettings _dashboard;
    private readonly HttpClient _httpClient;

    public JiraSolvedClient(
        IntegrationSettings settings,
        Func<string, WorkflowLabels> loadLabels,
        HttpMessageHandler? handler = null)
    {
        var jira = settings.Jira;
        _loadLabels = loadLabels;
        _dashboard = settings.Dashboard;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = new Uri(jira.JiraBaseUrl);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{jira.JiraEmail}:{jira.JiraApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-Dashboard/1.0");
    }

    public async Task<IReadOnlyList<SolvedIssue>> GetSolvedIssuesAsync(CancellationToken cancellationToken)
    {
        var issues = new List<SolvedIssue>();
        var configuredAiLabels = _dashboard.AiLabels;
        var aiLabels = configuredAiLabels.Count > 0
            ? configuredAiLabels.ToHashSet(StringComparer.Ordinal)
            : AiWorkLabelKeys
                .Select(item => _loadLabels(item.Agent).Get(item.CodeKey))
                .ToHashSet(StringComparer.Ordinal);
        var query = $"jql={Uri.EscapeDataString(SolvedJql)}&fields=project,labels&maxResults=100";
        string? nextPageToken = null;
        do
        {
            var pageQuery = string.IsNullOrEmpty(nextPageToken)
                ? query
                : $"{query}&nextPageToken={Uri.EscapeDataString(nextPageToken)}";
            using var response = await _httpClient.GetAsync($"/rest/api/3/search/jql?{pageQuery}", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var issue in document.RootElement.GetProperty("issues").EnumerateArray())
            {
                var fields = issue.GetProperty("fields");
                var key = fields.GetProperty("project").GetProperty("key").GetString();
                if (!string.IsNullOrEmpty(key))
                {
                    issues.Add(new SolvedIssue(key, HasAiLabel(fields, aiLabels)));
                }
            }

            nextPageToken = document.RootElement.TryGetProperty("nextPageToken", out var token)
                ? token.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(nextPageToken));

        return issues;
    }

    private static bool HasAiLabel(JsonElement fields, IReadOnlySet<string> aiLabels) =>
        fields.TryGetProperty("labels", out var labels)
        && labels.ValueKind == JsonValueKind.Array
        && labels.EnumerateArray().Any(label =>
            label.ValueKind == JsonValueKind.String
            && aiLabels.Contains(label.GetString() ?? ""));

    public void Dispose() => _httpClient.Dispose();
}

public static class SolvedByProjectEndpoint
{
    public static async Task<IResult> HandleAsync(IJiraSolvedClient client, CancellationToken cancellationToken)
    {
        try
        {
            var issues = await client.GetSolvedIssuesAsync(cancellationToken);
            var result = issues
                .GroupBy(issue => issue.ProjectKey, StringComparer.Ordinal)
                .Select(group => new SolvedByProject(group.Key, group.Count(), group.Count(i => i.IsAi), group.Count(i => !i.IsAi)))
                .OrderBy(item => item.Project, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            // Log only the exception type; messages and URLs could carry credential material.
            DFLogger.LogOutput(DFLogLevel.ERROR, "SolvedByProject",
                $"Failed to load solved issues from Jira ({ex.GetType().Name}).");
            return Results.Json(
                new { error = "Unable to load solved issue statistics from Jira." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
