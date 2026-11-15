using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiTeam.Configuration;

public sealed record ActiveAiTask(string Key, string Summary, string Status, string Description,
    IReadOnlyList<string> Labels);

public interface IActiveAiTaskService
{
    Task<IReadOnlyList<ActiveAiTask>> GetActiveAsync(CancellationToken cancellationToken);
}

// Returns at most 5 issues of the configured project, regardless of labels, filtered only by
// status (Dashboard.ActiveStatuses; empty = every non-Done status), ordered by most recently
// updated first (ties broken by issue key ascending).
public sealed class ActiveAiTaskService : IActiveAiTaskService, IDisposable
{
    public const int MaxTasks = 5;

    private readonly IntegrationSettings _settings;
    private readonly HttpClient _httpClient;

    public ActiveAiTaskService(IntegrationSettings settings, HttpClient httpClient)
    {
        _settings = settings;
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

    public static string BuildJql(string projectKey, string doneStatus, IEnumerable<string>? statuses = null)
    {
        var statusList = (statuses ?? []).ToArray();
        var statusClause = statusList.Length > 0
            ? $"AND status in ({string.Join(", ", statusList.Select(s => $"\"{JiraQuery.EscapeJql(s)}\""))}) "
            : string.Empty;
        return $"project = \"{JiraQuery.EscapeJql(projectKey)}\" " +
               $"AND status != \"{JiraQuery.EscapeJql(doneStatus)}\" AND statusCategory != Done " + statusClause +
               "ORDER BY updated DESC, key ASC";
    }

    public async Task<IReadOnlyList<ActiveAiTask>> GetActiveAsync(CancellationToken cancellationToken)
    {
        var doneStatus = _settings.Jira.JiraDoneStatus;
        var statuses = _settings.Dashboard.ActiveStatuses;
        var jql = BuildJql(_settings.Jira.JiraProjectKey, doneStatus, statuses);

        using var response = await _httpClient.GetAsync(
            $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}" +
            $"&fields=summary,description,status,updated,labels&maxResults={MaxTasks}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var issues = new List<(ActiveAiTask Task, string Updated)>();
        foreach (var issue in document.RootElement.GetProperty("issues").EnumerateArray())
        {
            var fields = issue.GetProperty("fields");
            var status = fields.TryGetProperty("status", out var statusElement)
                ? ReadString(statusElement, "name")
                : string.Empty;
            var labels = fields.TryGetProperty("labels", out var labelsElement) &&
                         labelsElement.ValueKind == JsonValueKind.Array
                ? labelsElement.EnumerateArray().Select(label => label.GetString() ?? string.Empty).ToArray()
                : [];
            if (string.Equals(status, doneStatus, StringComparison.OrdinalIgnoreCase) ||
                (statuses.Count > 0 && !statuses.Contains(status, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            issues.Add((
                new ActiveAiTask(
                    ReadString(issue, "key"),
                    ReadString(fields, "summary"),
                    status,
                    ReadDescription(fields),
                    labels),
                ReadString(fields, "updated")));
        }

        return issues
            .OrderByDescending(item => ParseUpdated(item.Updated))
            .ThenBy(item => item.Task.Key, StringComparer.Ordinal)
            .Select(item => item.Task)
            .Take(MaxTasks)
            .ToArray();
    }

    public void Dispose() => _httpClient.Dispose();

    private static DateTimeOffset ParseUpdated(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : DateTimeOffset.MinValue;

    private static string ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadDescription(JsonElement fields)
    {
        if (!fields.TryGetProperty("description", out var description))
        {
            return string.Empty;
        }

        if (description.ValueKind == JsonValueKind.String)
        {
            return description.GetString() ?? string.Empty;
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
            if (type is "paragraph" or "heading" or "listItem" or "codeBlock" or "blockquote")
            {
                output.Append('\n');
            }
        }
    }
}
