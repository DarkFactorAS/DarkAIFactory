using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodingAgent;

internal sealed class JiraClient : IDisposable
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-CodingAgent/1.0");
    }

    public async Task<IReadOnlyList<JiraIssue>> FindWorkAsync(CancellationToken cancellationToken)
    {
        var query =
            $"jql={Uri.EscapeDataString(_configuration.JiraJql)}" +
            "&fields=summary,description&maxResults=20";
        using var response = await _httpClient.GetAsync(
            $"/rest/api/3/search/jql?{query}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var issues = new List<JiraIssue>();
        foreach (var issue in document.RootElement.GetProperty("issues").EnumerateArray())
        {
            var fields = issue.GetProperty("fields");
            issues.Add(new JiraIssue(
                issue.GetProperty("key").GetString() ?? throw new InvalidDataException("A Jira result has no issue key."),
                ReadString(fields, "summary"),
                ReadAdfText(fields, "description")));
        }

        return issues;
    }

    public async Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken)
    {
        var body = new
        {
            update = new
            {
                labels = new[] { new { add = label } }
            }
        };
        using var response = await _httpClient.PutAsJsonAsync(
            $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}",
            body,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task AddCommentAsync(string issueKey, string text, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["body"] = CreateAdfDocument(text)
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/comment",
            body,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task TransitionToAsync(string issueKey, string statusName, CancellationToken cancellationToken)
    {
        var path = $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}/transitions";
        string? transitionId = null;
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
                    if (transitionId is null &&
                        (string.Equals(target, statusName, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, statusName, StringComparison.OrdinalIgnoreCase)))
                    {
                        transitionId = ReadString(transition, "id");
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(transitionId))
        {
            throw new InvalidOperationException(
                $"Jira issue {issueKey} has no transition to \"{statusName}\". " +
                $"Available transitions: {(available.Count == 0 ? "none" : string.Join(", ", available))}.");
        }

        var body = new { transition = new { id = transitionId } };
        using var transitionResponse = await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
        transitionResponse.EnsureSuccessStatusCode();
    }

    public void Dispose() => _httpClient.Dispose();

    private static string ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadAdfText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var description) ||
            description.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        AppendAdfText(description, text);
        return string.Join(
            Environment.NewLine,
            text.ToString().Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0));
    }

    private static void AppendAdfText(JsonElement element, StringBuilder output)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                AppendAdfText(child, output);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = ReadString(element, "type");
        if (type == "text")
        {
            output.Append(ReadString(element, "text"));
            return;
        }

        if (type == "hardBreak")
        {
            output.Append('\n');
            return;
        }

        if (type is "inlineCard" or "blockCard" or "embedCard")
        {
            if (element.TryGetProperty("attrs", out var attributes))
            {
                output.Append(ReadString(attributes, "url"));
            }

            if (type is "blockCard" or "embedCard")
            {
                output.Append('\n');
            }

            return;
        }

        if (element.TryGetProperty("content", out var content))
        {
            if (type == "listItem")
            {
                output.Append("- ");
            }

            AppendAdfText(content, output);
        }

        if (type is "paragraph" or "heading" or "listItem" or "codeBlock")
        {
            output.Append('\n');
        }
    }

    private static JsonObject CreateAdfDocument(string text)
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
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = lines[index]
                    });
                }
            }

            paragraphs.Add(new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = content
            });
        }

        return new JsonObject
        {
            ["type"] = "doc",
            ["version"] = 1,
            ["content"] = paragraphs
        };
    }
}
