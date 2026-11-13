using DFCommonLib.DataAccess;

namespace AiAgent.Core;

public sealed class WorkflowLabels
{
    private readonly IReadOnlyDictionary<string, string> _labels;

    public WorkflowLabels(string agentName, IReadOnlyDictionary<string, string> labels)
    {
        AgentName = agentName;
        _labels = labels;
    }

    public string AgentName { get; }

    /// <summary>Returns the configured label or <paramref name="defaultLabel"/> when none is configured.</summary>
    public string GetOrDefault(string codeKey, string defaultLabel) =>
        _labels.TryGetValue(codeKey, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim()
            : defaultLabel;

    public string Get(string codeKey) =>
        _labels.TryGetValue(codeKey, out var label) && !string.IsNullOrWhiteSpace(label)
            ? label.Trim()
            : throw new InvalidOperationException(
                $"Required workflow label workflow_labels[{AgentName}, {codeKey}] is missing.");
}

public sealed class WorkflowLabelRepository(IDbConnectionFactory connectionFactory)
{
    public WorkflowLabels Load(string agentName)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `code_key`, `label_name` FROM `workflow_labels` WHERE `agent_name` = @agent");
        command.AddParameter("@agent", agentName);
        using var reader = command.ExecuteReader();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            labels[reader.GetString(0)] = reader.GetString(1);
        }

        return new WorkflowLabels(agentName, labels);
    }
}
