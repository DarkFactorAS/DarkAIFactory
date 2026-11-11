namespace AiAgent;

/// <summary>Agent configuration as stored in the database (new_agents and related tables).</summary>
public sealed record AgentDefinition(
    string Name,
    string Role,
    string Instructions,
    string LanguageModelKey,
    IReadOnlyList<string> Capabilities)
{
    public bool HasCapability(string capability) =>
        Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}
