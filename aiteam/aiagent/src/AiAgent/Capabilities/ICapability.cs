namespace AiAgent.Capabilities;

/// <summary>A pluggable module that gives the agent an ability (for example polling a task source).</summary>
public interface ICapability
{
    /// <summary>Unique, case-insensitive name used in configuration.</summary>
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
