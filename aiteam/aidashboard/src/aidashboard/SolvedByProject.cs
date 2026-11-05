using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Stats;

public sealed record SolvedByProject(string Project, int SolvedCount);

public interface IJiraSolvedClient
{
    // Project key of every issue whose status category is Done.
    Task<IReadOnlyList<string>> GetSolvedIssueProjectKeysAsync(CancellationToken cancellationToken);
}

public sealed class JiraSolvedClient : IJiraSolvedClient, IDisposable
{
    public const string SolvedJql = "statusCategory = Done";

    private readonly HttpClient _httpClient;

    public JiraSolvedClient(IntegrationSettings settings, HttpMessageHandler? handler = null)
    {
        var jira = settings.Jira;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = new Uri(jira.JiraBaseUrl);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{jira.JiraEmail}:{jira.JiraApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-Dashboard/1.0");
    }

    public async Task<IReadOnlyList<string>> GetSolvedIssueProjectKeysAsync(CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        var query = $"jql={Uri.EscapeDataString(SolvedJql)}&fields=project&maxResults=100";
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
                var key = issue.GetProperty("fields").GetProperty("project").GetProperty("key").GetString();
                if (!string.IsNullOrEmpty(key))
                {
                    keys.Add(key);
                }
            }

            nextPageToken = document.RootElement.TryGetProperty("nextPageToken", out var token)
                ? token.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(nextPageToken));

        return keys;
    }

    public void Dispose() => _httpClient.Dispose();
}

public static class SolvedByProjectEndpoint
{
    public static async Task<IResult> HandleAsync(IJiraSolvedClient client, CancellationToken cancellationToken)
    {
        try
        {
            var keys = await client.GetSolvedIssueProjectKeysAsync(cancellationToken);
            var result = keys
                .GroupBy(key => key, StringComparer.Ordinal)
                .Select(group => new SolvedByProject(group.Key, group.Count()))
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
