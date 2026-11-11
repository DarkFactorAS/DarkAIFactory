using AiAgent.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace AiAgent;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the DB-backed agent config services and an <see cref="AgentHost"/> for one agent.</summary>
    public static IServiceCollection AddAgentConfig(
        this IServiceCollection services, AgentDefinition defaults, TimeSpan? cacheTtl = null)
    {
        services.AddSingleton(new AgentConfigOptions { CacheTtl = cacheTtl ?? TimeSpan.Zero });
        services.AddScoped<MySqlAgentConfigRepository>();
        services.AddScoped<IAgentConfigRepository>(p => p.GetRequiredService<MySqlAgentConfigRepository>());
        services.AddSingleton<AgentConfigLoader>(provider => new AgentConfigLoader(
            new ScopedRepository(provider), provider.GetRequiredService<AgentConfigOptions>()));
        services.AddScoped(provider => new AgentHost(
            provider.GetRequiredService<IAgentConfigRepository>(),
            provider.GetRequiredService<AgentConfigLoader>(), defaults));
        return services;
    }

    // The loader is a singleton (for the TTL cache) but repositories are scoped; resolve per call.
    private sealed class ScopedRepository(IServiceProvider root) : IAgentConfigRepository
    {
        public async Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken ct = default)
        {
            using var scope = root.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MySqlAgentConfigRepository>()
                .GetByNameAsync(name, ct);
        }

        public async Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default)
        {
            using var scope = root.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<MySqlAgentConfigRepository>().ListAsync(ct);
        }
    }
}
