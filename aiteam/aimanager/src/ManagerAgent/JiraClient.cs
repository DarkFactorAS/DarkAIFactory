using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManagerAgent;

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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DarkFactor-ManagerAgent/1.0");
    }

    public async Task<IReadOnlyList<JiraIssue>> FindWorkAsync(CancellationToken cancellationToken)
    {
        var query =
            $"jql={Uri.EscapeDataString(_configuration.JiraJql)}" +
            "&fields=summary,description,status,updated,labels&maxResults=20";
        var issues = new List<JiraIssue>();
        string? nextPageToken = null;
        do
        {
            var pageQuery = string.IsNullOrEmpty(nextPageToken)
                ? query
                : $"{query}&nextPageToken={Uri.EscapeDataString(nextPageToken)}";
            using var response = await _httpClient.GetAsync(
                $"/rest/api/3/search/jql?{pageQuery}",
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var issue in document.RootElement.GetProperty("issues").EnumerateArray())
            {
                issues.Add(ReadIssue(issue));
            }

            nextPageToken = document.RootElement.TryGetProperty("nextPageToken", out var token)
                ? token.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(nextPageToken));

        return issues;
    }

    public async Task<JiraIssue> GetIssueAsync(string issueKey, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields=summary,description,status,updated,labels",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadIssue(document.RootElement);
    }

    private static JiraIssue ReadIssue(JsonElement issue)
    {
        var fields = issue.GetProperty("fields");
        return new JiraIssue(
            issue.GetProperty("key").GetString() ?? throw new InvalidDataException("A Jira result has no issue key."),
            ReadString(fields, "summary"), ReadAdfText(fields, "description"),
            fields.TryGetProperty("status", out var status) ? ReadString(status, "name") : string.Empty)
        {
            UpdatedAt = ReadString(fields, "updated"),
            Labels = fields.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                ? labels.EnumerateArray().Select(label => label.GetString()
                    ?? throw new InvalidDataException("A Jira label is null.")).ToArray()
                : []
        };
    }

    public Task AddLabelAsync(string issueKey, string label, CancellationToken cancellationToken) =>
        UpdateLabelAsync(issueKey, label, "add", cancellationToken);

    public Task RemoveLabelAsync(string issueKey, string label, CancellationToken cancellationToken) =>
        UpdateLabelAsync(issueKey, label, "remove", cancellationToken);

    private async Task UpdateLabelAsync(string issueKey, string label, string operation, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["update"] = new JsonObject
            {
                ["labels"] = new JsonArray(new JsonObject { [operation] = label })
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
                    if (string.IsNullOrEmpty(destinationTransitionId) &&
                        string.Equals(target, statusName, StringComparison.OrdinalIgnoreCase))
                    {
                        destinationTransitionId = ReadString(transition, "id");
                    }

                    if (string.IsNullOrEmpty(nameTransitionId) &&
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

        var body = new { transition = new { id = transitionId } };
        using var transitionResponse = await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
        transitionResponse.EnsureSuccessStatusCode();
    }

    public async Task<string> CreateSubtaskAsync(
        string parentKey, string summary, string description, CancellationToken cancellationToken)
    {
        var dash = parentKey.LastIndexOf('-');
        if (dash <= 0)
        {
            throw new InvalidDataException($"Cannot derive the Jira project from issue key \"{parentKey}\".");
        }

        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = parentKey[..dash] },
                ["parent"] = new JsonObject { ["key"] = parentKey },
                ["issuetype"] = new JsonObject { ["name"] = _configuration.SubtaskIssueType },
                ["summary"] = summary,
                ["description"] = CreateAdfDocument(description)
            }
        };
        using var response = await _httpClient.PostAsJsonAsync("/rest/api/3/issue", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ReadString(document.RootElement, "key") is { Length: > 0 } key
            ? key
            : throw new InvalidDataException("Jira did not return a key for the created sub-task.");
    }

    public async Task LinkIssuesAsync(string blockerKey, string blockedKey, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["type"] = new JsonObject { ["name"] = "Blocks" },
            ["outwardIssue"] = new JsonObject { ["key"] = blockerKey },
            ["inwardIssue"] = new JsonObject { ["key"] = blockedKey }
        };
        using var response = await _httpClient.PostAsJsonAsync("/rest/api/3/issueLink", body, cancellationToken);
        response.EnsureSuccessStatusCode();
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
            if (element.TryGetProperty("marks", out var marks) && marks.ValueKind == JsonValueKind.Array)
            {
                foreach (var mark in marks.EnumerateArray())
                {
                    if (ReadString(mark, "type") == "link" &&
                        mark.TryGetProperty("attrs", out var attributes))
                    {
                        var href = ReadString(attributes, "href");
                        if (href.Length > 0)
                        {
                            output.Append(" (").Append(href).Append(')');
                        }
                    }
                }
            }

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
