using AiAgent.Core;
using DFCommonLib.DataAccess;

namespace AiAgent.Repositories;

/// <summary>Reads agent_definitions, agent_capabilities and agent_projects.</summary>
public sealed class AgentRepository(IDbConnectionFactory connectionFactory) : IAgentRepository
{
    private const string Columns =
        "`id`, `name`, `role`, `behavior`, `llm_provider`, `llm_model`, `enabled`, `deleted_at`";

    public IReadOnlyList<AgentDefinition> GetEnabledAgents()
    {
        using var command = connectionFactory.CreateCommand(
            $"SELECT {Columns} FROM `agent_definitions` WHERE `enabled` = TRUE AND `deleted_at` IS NULL ORDER BY `id`");
        using var reader = command.ExecuteReader();
        var agents = new List<AgentDefinition>();
        while (reader.Read())
        {
            agents.Add(Map(reader));
        }

        return agents;
    }

    public IReadOnlyList<AgentDefinition> GetAllAgents()
    {
        using var command = connectionFactory.CreateCommand(
            $"SELECT {Columns} FROM `agent_definitions` ORDER BY `id`");
        using var reader = command.ExecuteReader();
        var agents = new List<AgentDefinition>();
        while (reader.Read())
        {
            agents.Add(Map(reader));
        }

        return agents;
    }

    public AgentDefinition? GetAgent(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            $"SELECT {Columns} FROM `agent_definitions` WHERE `id` = @id");
        command.AddParameter("@id", agentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlySet<AgentCapabilityKind> GetEnabledCapabilities(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `capability` FROM `agent_capabilities` WHERE `agent_id` = @id AND `enabled` = TRUE");
        command.AddParameter("@id", agentId);
        using var reader = command.ExecuteReader();
        var result = new HashSet<AgentCapabilityKind>();
        while (reader.Read())
        {
            if (Enum.TryParse<AgentCapabilityKind>(reader.GetString(0), ignoreCase: true, out var kind))
            {
                result.Add(kind);
            }
        }

        return result;
    }

    public IReadOnlyList<string> GetProjectKeys(long agentId)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `project_key` FROM `agent_projects` WHERE `agent_id` = @id ORDER BY `project_key`");
        command.AddParameter("@id", agentId);
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private static AgentDefinition Map(System.Data.IDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("name")),
        reader.GetString(reader.GetOrdinal("role")),
        reader.GetString(reader.GetOrdinal("behavior")),
        reader.GetString(reader.GetOrdinal("llm_provider")),
        reader.IsDBNull(reader.GetOrdinal("llm_model")) ? null : reader.GetString(reader.GetOrdinal("llm_model")),
        reader.GetBoolean(reader.GetOrdinal("enabled")),
        !reader.IsDBNull(reader.GetOrdinal("deleted_at")));
}
