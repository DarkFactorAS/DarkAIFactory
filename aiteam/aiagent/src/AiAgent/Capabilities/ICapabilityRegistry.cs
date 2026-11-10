namespace AiAgent.Capabilities;

public interface ICapabilityRegistry
{
    IReadOnlyCollection<ICapability> All { get; }

    ICapability? Find(string name);

    /// <summary>Resolves the configured names; throws when a name is unknown.</summary>
    IReadOnlyList<ICapability> Resolve(IEnumerable<string> enabledNames);
}
