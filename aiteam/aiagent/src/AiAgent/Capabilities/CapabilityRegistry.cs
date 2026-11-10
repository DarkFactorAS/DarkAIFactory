namespace AiAgent.Capabilities;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, ICapability> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public CapabilityRegistry(IEnumerable<ICapability> capabilities)
    {
        foreach (var capability in capabilities)
        {
            if (!_capabilities.TryAdd(capability.Name, capability))
            {
                throw new InvalidOperationException($"Duplicate capability '{capability.Name}'.");
            }
        }
    }

    public IReadOnlyCollection<ICapability> All => _capabilities.Values;

    public ICapability? Find(string name) => _capabilities.GetValueOrDefault(name);

    public IReadOnlyList<ICapability> Resolve(IEnumerable<string> enabledNames) =>
        enabledNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => Find(name) ?? throw new InvalidOperationException($"Unknown capability '{name}'."))
            .ToList();
}
