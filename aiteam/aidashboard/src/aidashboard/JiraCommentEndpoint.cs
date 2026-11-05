using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

public sealed record JiraCommentRequest(string? Text);

public interface IJiraCommentClient
{
    Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken);
}

// Appends extra user text to an existing Jira issue as a new comment (the description is never modified).
public sealed class JiraCommentClient : IJiraCommentClient
{
    private readonly HttpClient _httpClient;

    public JiraCommentClient(IntegrationSettings settings, HttpClient httpClient)
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

    public static string BuildBody(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Length == 0
                ? new { type = "paragraph", content = Array.Empty<object>() }
                : new { type = "paragraph", content = new object[] { new { type = "text", text = line } } });
        return JsonSerializer.Serialize(new
        {
            body = new { type = "doc", version = 1, content = paragraphs }
        });
    }

    public async Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken)
    {
        using var content = new StringContent(BuildBody(text), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(
            $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public static partial class JiraCommentEndpoint
{
    public const int MaxTextLength = 10000;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*-[0-9]+$")]
    private static partial Regex KeyPattern();

    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/api/tasks/{key}/comments", HandleAsync);

    public static string? Validate(string key, JiraCommentRequest? request)
    {
        if (!KeyPattern().IsMatch(key))
        {
            return "key must be a valid Jira issue key.";
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Text) ||
            request.Text.Trim().Length > MaxTextLength)
        {
            return $"text is required and must be at most {MaxTextLength} characters.";
        }

        return null;
    }

    public static async Task<IResult> HandleAsync(
        IJiraCommentClient client, string key, JiraCommentRequest? request, CancellationToken cancellationToken)
    {
        var error = Validate(key, request);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        try
        {
            await client.AddCommentAsync(key, request!.Text!.Trim(), cancellationToken);
            return Results.Created($"/api/tasks/{key}/comments", new { key });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraComment", ex.Message);
            return Results.Problem("Unable to add the comment to Jira.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
