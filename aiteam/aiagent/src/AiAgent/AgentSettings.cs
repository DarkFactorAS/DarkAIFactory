namespace AiAgent;

/// <summary>Immutable view of an agent definition used by the running agent.</summary>
public sealed class AgentSettings(AgentDefinition definition)
{
    public string Name => definition.Name;
    public string Role => definition.Role;
    public string Instructions => definition.Instructions;
    public string LanguageModel => definition.LanguageModelKey;
    public IReadOnlyList<string> Capabilities => definition.Capabilities;

    public bool IsEnabled(string capability) => definition.HasCapability(capability);

    /// <summary>System prompt (behavior text); empty when none is configured.</summary>
    public string SystemPrompt => string.IsNullOrWhiteSpace(definition.Instructions)
        ? string.Empty
        : $"Role: {definition.Role}\n{definition.Instructions}";
}
