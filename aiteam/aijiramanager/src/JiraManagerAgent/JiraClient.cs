using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JiraManagerAgent;

internal sealed class JiraClient : IJiraClient, IDisposable
{
    private readonly AgentConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public JiraClient(AgentConfiguration configuration, HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = configuration.JiraBaseUrl;
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{configuration.JiraEmail}:{configuration.JiraApiToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-JiraManagerAgent/1.0");
    }

    public async Task<string> CreateIssueAsync(
        string projectKey, string title, string description, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = projectKey },
                ["issuetype"] = new JsonObject { ["name"] = _configuration.IssueType },
                ["summary"] = title,
                ["description"] = CreateAdfDocument(description)
            }
        };
        using var response = await _httpClient.PostAsJsonAsync("/rest/api/3/issue", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("key", out var key) &&
            key.ValueKind == JsonValueKind.String && key.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException("Jira did not return a key for the created issue.");
    }

    public async Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken)
    {
        var path = $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/transitions";
        string? destinationTransitionId = null;
        string? nameTransitionId = null;
        var available = new List<string>();
        using (var response = await _httpClient.GetAsync(path, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("transitions", out var transitions))
            {
                foreach (var transition in transitions.EnumerateArray())
                {
                    var name = ReadString(transition, "name");
                    var target = transition.TryGetProperty("to", out var to) ? ReadString(to, "name") : string.Empty;
                    available.Add(target.Length > 0 ? target : name);
                    if (destinationTransitionId is null &&
                        string.Equals(target, statusName, StringComparison.OrdinalIgnoreCase))
                    {
                        destinationTransitionId = ReadString(transition, "id");
                    }

                    if (nameTransitionId is null &&
                        string.Equals(name, statusName, StringComparison.OrdinalIgnoreCase))
                    {
                        nameTransitionId = ReadString(transition, "id");
                    }
                }
            }
        }

        var transitionId = destinationTransitionId ?? nameTransitionId;
        if (string.IsNullOrEmpty(transitionId))
        {
            throw new InvalidOperationException(
                $"Jira issue {issueKey} has no transition to \"{statusName}\". " +
                $"Available transitions: {(available.Count == 0 ? "none" : string.Join(", ", available))}.");
        }

        using var transitionResponse = await _httpClient.PostAsJsonAsync(
            path, new { transition = new { id = transitionId } }, cancellationToken);
        transitionResponse.EnsureSuccessStatusCode();
    }

    public void Dispose() => _httpClient.Dispose();

    private static string ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    internal static JsonObject CreateAdfDocument(string text)
    {
        var paragraphs = new JsonArray();
        foreach (var paragraphText in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var content = new JsonArray();
            var lines = paragraphText.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    content.Add(new JsonObject { ["type"] = "hardBreak" });
                }

                if (lines[index].Length > 0)
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = lines[index] });
                }
            }

            paragraphs.Add(new JsonObject { ["type"] = "paragraph", ["content"] = content });
        }

        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = paragraphs };
    }
}
