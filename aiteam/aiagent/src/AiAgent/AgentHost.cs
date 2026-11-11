using AiAgent.Repositories;
using DFCommonLib.Logger;

namespace AiAgent;

/// <summary>Exposes the current settings of one named agent, refreshed from the DB on demand.</summary>
public sealed class AgentHost(
    IAgentConfigRepository repository, AgentConfigLoader loader, AgentDefinition defaults)
{
    public IAgentConfigRepository Repository { get; } = repository;
    public AgentSettings Settings { get; private set; } = new(defaults);
    public string SystemPrompt => Settings.SystemPrompt;

    public async Task<AgentSettings> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Settings = new AgentSettings(await loader.LoadAsync(defaults.Name, defaults, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the previous settings (initially the defaults) when the DB is unavailable or not migrated.
            DFLogger.LogOutput(DFLogLevel.WARNING, "AgentConfig",
                $"Could not load agent config for '{defaults.Name}': {exception.Message}. Using previous settings.");
        }
        return Settings;
    }
}
