namespace AiAgent;

/// <summary>Typed settings loaded from Config/appsettings.{environment}.json.</summary>
public sealed class AgentSettings
{
    public string AppName { get; set; } = "AiAgent";

    /// <summary>Names of capabilities to enable. Empty by default.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Name of the language model to use. Optional while no capability needs one.</summary>
    public string? LanguageModel { get; set; }
}
