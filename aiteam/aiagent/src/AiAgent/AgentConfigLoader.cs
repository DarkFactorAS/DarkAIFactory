using AiAgent.Repositories;

namespace AiAgent;

/// <summary>
/// Loads an agent definition from the repository. Re-reads on each call unless a TTL is configured,
/// so DB changes apply without rebuild or restart. Falls back to defaults when no row exists.
/// </summary>
public sealed class AgentConfigLoader(
    IAgentConfigRepository repository, AgentConfigOptions? options = null, Func<DateTimeOffset>? clock = null)
{
    private readonly TimeSpan _ttl = options?.CacheTtl ?? TimeSpan.Zero;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, (DateTimeOffset At, AgentDefinition Value)> _cache = [];
    private readonly object _gate = new();

    public async Task<AgentDefinition> LoadAsync(
        string name, AgentDefinition defaults, CancellationToken cancellationToken = default)
    {
        if (_ttl > TimeSpan.Zero)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(name, out var hit) && _clock() - hit.At < _ttl) return hit.Value;
            }
        }
        var loaded = await repository.GetByNameAsync(name, cancellationToken) ?? defaults;
        if (_ttl > TimeSpan.Zero)
        {
            lock (_gate) _cache[name] = (_clock(), loaded);
        }
        return loaded;
    }
}

public sealed class AgentConfigOptions
{
    /// <summary>Zero (default) means the database is read on every load.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.Zero;
}
