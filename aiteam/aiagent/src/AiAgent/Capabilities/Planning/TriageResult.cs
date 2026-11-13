using System.Text.Json;

namespace AiAgent.Capabilities.Planning;

internal enum TriageDecision
{
    Clarify,
    AnalysisOnly,
    Small,
    NeedsApproval,
    Split
}

internal sealed record TriageSubtask(string Title, string Description, IReadOnlyList<string> DependsOn);

internal sealed record TriageResult(
    TriageDecision Decision,
    IReadOnlyList<string> Questions,
    string Analysis,
    string Plan,
    IReadOnlyList<TriageSubtask> Subtasks,
    IReadOnlyList<string> RiskAreas)
{
    public const int MaximumSubtasks = 10;

    private static readonly (TriageDecision Decision, string Name)[] DecisionNames =
    [
        (TriageDecision.Clarify, "clarify"),
        (TriageDecision.AnalysisOnly, "analysis_only"),
        (TriageDecision.Small, "small"),
        (TriageDecision.NeedsApproval, "needs_approval"),
        (TriageDecision.Split, "split")
    ];

    public static string MarkerFor(TriageDecision decision) =>
        $"[triage:{DecisionNames.First(entry => entry.Decision == decision).Name}]";

    public static TriageDecision? ReadMarker(string storedPlan)
    {
        foreach (var (decision, _) in DecisionNames)
        {
            if (storedPlan.StartsWith(MarkerFor(decision), StringComparison.Ordinal))
            {
                return decision;
            }
        }

        return null;
    }

    public static string StripMarker(string storedPlan)
    {
        var decision = ReadMarker(storedPlan);
        return decision is null
            ? storedPlan
            : storedPlan[MarkerFor(decision.Value).Length..].TrimStart('\r', '\n');
    }

    /// <summary>
    /// Returns false when the output is plain text rather than a JSON triage document, so that
    /// plain implementation plans keep working. Output that looks like JSON but is invalid throws.
    /// </summary>
    public static bool TryParse(string output, out TriageResult? result)
    {
        result = null;
        var json = ExtractJson(output);
        if (json is null)
        {
            return false;
        }

        result = Parse(json);
        return true;
    }

    public static TriageResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The agent returned malformed triage JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The triage result must be a JSON object.");
            }

            var decisionName = ReadString(root, "decision").Trim().ToLowerInvariant();
            var entry = DecisionNames.FirstOrDefault(candidate => candidate.Name == decisionName);
            if (entry.Name is null)
            {
                throw new InvalidDataException($"Unknown triage decision \"{decisionName}\".");
            }

            var subtasks = ReadSubtasks(root);
            var result = new TriageResult(
                entry.Decision,
                ReadStrings(root, "questions"),
                ReadString(root, "analysis").Trim(),
                ReadString(root, "plan").Trim(),
                subtasks,
                ReadStrings(root, "riskAreas"));
            result.Validate();
            return result;
        }
    }

    /// <summary>Returns subtask indexes ordered so every subtask comes after its dependencies.</summary>
    public IReadOnlyList<int> ExecutionOrder()
    {
        var indexByTitle = IndexByTitle(Subtasks);
        var remaining = Enumerable.Range(0, Subtasks.Count).ToHashSet();
        var order = new List<int>();
        while (remaining.Count > 0)
        {
            var next = remaining
                .Order()
                .Where(index => Subtasks[index].DependsOn.All(title => !remaining.Contains(indexByTitle[Normalize(title)])))
                .Cast<int?>()
                .FirstOrDefault();
            if (next is null)
            {
                throw new InvalidDataException("The subtask dependencies contain a cycle.");
            }

            order.Add(next.Value);
            remaining.Remove(next.Value);
        }

        return order;
    }

    private void Validate()
    {
        switch (Decision)
        {
            case TriageDecision.Clarify when Questions.Count == 0:
                throw new InvalidDataException("A clarify decision needs at least one question.");
            case TriageDecision.AnalysisOnly when Analysis.Length == 0:
                throw new InvalidDataException("An analysis_only decision needs an analysis.");
            case TriageDecision.Small or TriageDecision.NeedsApproval when Plan.Length == 0:
                throw new InvalidDataException($"A {Decision} decision needs a plan.");
            case TriageDecision.Split when Plan.Length == 0:
                throw new InvalidDataException("A split decision needs an overall plan.");
            case TriageDecision.Split when Subtasks.Count == 0:
                throw new InvalidDataException("A split decision needs at least one subtask.");
        }

        if (Subtasks.Count > MaximumSubtasks)
        {
            throw new InvalidDataException($"A triage result may contain at most {MaximumSubtasks} subtasks.");
        }

        var indexByTitle = IndexByTitle(Subtasks);
        for (var index = 0; index < Subtasks.Count; index++)
        {
            foreach (var dependency in Subtasks[index].DependsOn)
            {
                if (!indexByTitle.TryGetValue(Normalize(dependency), out var target))
                {
                    throw new InvalidDataException(
                        $"Subtask \"{Subtasks[index].Title}\" depends on unknown subtask \"{dependency}\".");
                }

                if (target == index)
                {
                    throw new InvalidDataException($"Subtask \"{Subtasks[index].Title}\" depends on itself.");
                }
            }
        }

        if (Subtasks.Count > 0)
        {
            ExecutionOrder();
        }
    }

    private static Dictionary<string, int> IndexByTitle(IReadOnlyList<TriageSubtask> subtasks)
    {
        var indexes = new Dictionary<string, int>();
        for (var index = 0; index < subtasks.Count; index++)
        {
            if (!indexes.TryAdd(Normalize(subtasks[index].Title), index))
            {
                throw new InvalidDataException($"Duplicate subtask title \"{subtasks[index].Title}\".");
            }
        }

        return indexes;
    }

    private static string Normalize(string title) => title.Trim().ToLowerInvariant();

    private static IReadOnlyList<TriageSubtask> ReadSubtasks(JsonElement root)
    {
        if (!root.TryGetProperty("subtasks", out var array) || array.ValueKind is JsonValueKind.Null)
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The triage field \"subtasks\" must be an array.");
        }

        var subtasks = new List<TriageSubtask>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Every subtask must be a JSON object.");
            }

            var title = ReadString(element, "title").Trim();
            var description = ReadString(element, "description").Trim();
            if (title.Length == 0 || description.Length == 0)
            {
                throw new InvalidDataException("Every subtask needs a title and a description.");
            }

            subtasks.Add(new TriageSubtask(title, description, ReadStrings(element, "dependsOn")));
        }

        return subtasks;
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStrings(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The triage field \"{name}\" must be an array of strings.");
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim() ?? string.Empty
                : throw new InvalidDataException($"The triage field \"{name}\" must contain only strings."))
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static string? ExtractJson(string output)
    {
        var text = output.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = text.IndexOf('\n');
            var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine < 0 || closingFence <= firstNewLine)
            {
                return text.Contains('{') ? text : null;
            }

            text = text[(firstNewLine + 1)..closingFence].Trim();
        }

        return text.StartsWith('{') || text.StartsWith('[') ? text : null;
    }
}
