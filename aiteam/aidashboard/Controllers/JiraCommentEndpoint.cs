using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

public sealed record JiraCommentRequest(string? Text);

public sealed record JiraCommentDto(string Author, DateTimeOffset Created, string Body);

public interface IJiraCommentClient
{
    Task<IReadOnlyList<JiraCommentDto>> GetLatestCommentsAsync(
        string issueKey, int count, CancellationToken cancellationToken);

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

    // Converts an Atlassian Document Format node to plain text; tolerates missing or unknown nodes.
    public static string AdfToPlainText(JsonElement node)
    {
        var builder = new StringBuilder();
        AppendAdf(node, builder);
        return builder.ToString().Trim('\n');
    }

    private static void AppendAdf(JsonElement node, StringBuilder builder)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = node.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        if (type == "text" && node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            builder.Append(text.GetString());
        }
        else if (type == "hardBreak")
        {
            builder.Append('\n');
        }

        if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                AppendAdf(child, builder);
            }
        }

        if (type is "paragraph" or "heading" or "codeBlock" or "listItem" or "blockquote")
        {
            builder.Append('\n');
        }
    }

    public async Task<IReadOnlyList<JiraCommentDto>> GetLatestCommentsAsync(
        string issueKey, int count, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment?orderBy=-created&maxResults={count}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var comments = new List<JiraCommentDto>();
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("comments", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(count))
            {
                var author = item.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object &&
                    a.TryGetProperty("displayName", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : "Unknown";
                var created = item.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(c.GetString(), out var parsed) ? parsed : default;
                var body = item.TryGetProperty("body", out var b) ? AdfToPlainText(b) : "";
                comments.Add(new JiraCommentDto(author, created, body));
            }
        }

        return comments;
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

    public const int HistoryCount = 3;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tasks/{key}/comments", HandleAsync).RequireAuthorization();
        app.MapGet("/api/tasks/{key}/comments", HandleGetAsync);
    }

    public static async Task<IResult> HandleGetAsync(
        IJiraCommentClient client, IActiveAiTaskService activeTasks,
        string key, CancellationToken cancellationToken)
    {
        if (!KeyPattern().IsMatch(key))
        {
            return Results.BadRequest(new { error = "key must be a valid Jira issue key." });
        }

        try
        {
            if (!(await activeTasks.GetActiveAsync(cancellationToken))
                    .Any(task => string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var comments = await client.GetLatestCommentsAsync(key, HistoryCount, cancellationToken);
            return Results.Ok(comments.Take(HistoryCount).Select(c => new { author = c.Author, created = c.Created, body = c.Body }));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or InvalidDataException or
                JsonException or KeyNotFoundException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraComment", ex.Message);
            return Results.Problem("Unable to load the comments from Jira.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

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
        IJiraCommentClient client, IActiveAiTaskService activeTasks,
        string key, JiraCommentRequest? request, CancellationToken cancellationToken)
    {
        var error = Validate(key, request);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        try
        {
            // Active tasks are limited to the manager_projects Jira projects.
            if (!(await activeTasks.GetActiveAsync(cancellationToken))
                    .Any(task => string.Equals(task.Key, key, StringComparison.OrdinalIgnoreCase)))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await client.AddCommentAsync(key, request!.Text!.Trim(), cancellationToken);
            return Results.Created($"/api/tasks/{key}/comments", new { key });
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or InvalidDataException or
                JsonException or KeyNotFoundException ||
            ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "JiraComment", ex.Message);
            return Results.Problem("Unable to add the comment to Jira.", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
