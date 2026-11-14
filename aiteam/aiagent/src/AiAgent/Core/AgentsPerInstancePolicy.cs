namespace AiAgent.Core;

/// <summary>Resolves how many agents one container runs (1..<see cref="AgentSettings.MaxAgentsPerInstance"/>).</summary>
public static class AgentsPerInstancePolicy
{
    /// <summary>The environment value wins when it parses as an int; the result is clamped to 1..3.</summary>
    public static int Resolve(int configured, string? envValue, out string? warning)
    {
        const string name = "AIAGENT_AGENTS_PER_INSTANCE";
        var messages = new List<string>();
        var value = configured;
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (int.TryParse(envValue.Trim(), out var parsed))
            {
                value = parsed;
            }
            else
            {
                messages.Add($"{name} '{envValue}' is not a number; using AgentsPerInstance {configured}.");
            }
        }

        var clamped = Math.Clamp(value, 1, AgentSettings.MaxAgentsPerInstance);
        if (clamped != value)
        {
            messages.Add($"Agents per instance {value} is outside 1-{AgentSettings.MaxAgentsPerInstance}; using {clamped}.");
        }

        warning = messages.Count == 0 ? null : string.Join(" ", messages);
        return clamped;
    }
}
