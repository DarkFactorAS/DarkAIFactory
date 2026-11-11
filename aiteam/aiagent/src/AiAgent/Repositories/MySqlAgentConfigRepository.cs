using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>Reads agent configuration from new_agents, new_language_models and new_agent_capabilities.</summary>
public sealed class MySqlAgentConfigRepository(IDbConnectionFactory connectionFactory) : IAgentConfigRepository
{
    private const string AgentSql =
        "SELECT a.name, a.role, a.instructions, a.language_model_key FROM new_agents a " +
        "JOIN new_language_models m ON m.model_key = a.language_model_key AND m.enabled = 1";

    private const string CapabilitySql =
        "SELECT a.name, c.capability FROM new_agent_capabilities c " +
        "JOIN new_agents a ON a.id = c.agent_id " +
        "JOIN new_language_models m ON m.model_key = a.language_model_key AND m.enabled = 1 " +
        "WHERE c.enabled = 1";

    public Task<AgentDefinition?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var all = Query(name);
        return Task.FromResult(all.Count == 0 ? null : all[0]);
    }

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentDefinition>>(Query(null));

    private List<AgentDefinition> Query(string? name)
    {
        var filter = name is null ? string.Empty : " WHERE a.name = @name";
        var capabilityFilter = name is null ? string.Empty : " AND a.name = @name";

        var capabilities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var command = connectionFactory.CreateCommand(CapabilitySql + capabilityFilter + " ORDER BY c.capability"))
        {
            if (name is not null) command.AddParameter("@name", name);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var agent = reader.GetString(0);
                if (!capabilities.TryGetValue(agent, out var list)) capabilities[agent] = list = [];
                list.Add(reader.GetString(1));
            }
        }

        var result = new List<AgentDefinition>();
        using (var command = connectionFactory.CreateCommand(AgentSql + filter + " ORDER BY a.name"))
        {
            if (name is not null) command.AddParameter("@name", name);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var agent = reader.GetString(0);
                result.Add(new AgentDefinition(
                    agent, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    capabilities.TryGetValue(agent, out var list) ? list : []));
            }
        }
        return result;
    }
}
