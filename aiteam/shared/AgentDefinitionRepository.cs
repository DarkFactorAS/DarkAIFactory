using DFCommonLib.DataAccess;

namespace AiTeam.Configuration;

public sealed class AgentDefinitionRepository(IDbConnectionFactory connectionFactory)
{
    public IReadOnlyList<AgentDefinition> GetAgents(bool enabledOnly = false)
    {
        var capabilities = LoadCapabilities();
        using var command = connectionFactory.CreateCommand(
            "SELECT `id`, `name`, `role`, `behavior_prompt`, `language_model`, `enabled` FROM `agents`"
            + (enabledOnly ? " WHERE `enabled` = TRUE" : string.Empty) + " ORDER BY `id`");
        using var reader = command.ExecuteReader();
        var agents = new List<AgentDefinition>();
        while (reader.Read())
        {
            var id = reader.GetInt64(reader.GetOrdinal("id"));
            agents.Add(new AgentDefinition(
                id,
                reader.GetString(reader.GetOrdinal("name")),
                reader.GetString(reader.GetOrdinal("role")),
                reader.GetString(reader.GetOrdinal("behavior_prompt")),
                reader.GetString(reader.GetOrdinal("language_model")),
                reader.GetBoolean(reader.GetOrdinal("enabled")),
                capabilities.TryGetValue(id, out var list) ? list : []));
        }

        return agents;
    }

    public AgentDefinition? FindByName(string name) =>
        GetAgents().FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public long Add(string name, string role, string behaviorPrompt, string languageModel, bool enabled = true)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agents` (`name`, `role`, `behavior_prompt`, `language_model`, `enabled`) "
            + "VALUES (@name, @role, @behavior_prompt, @language_model, @enabled); SELECT LAST_INSERT_ID()");
        command.AddParameter("@name", name);
        command.AddParameter("@role", role);
        command.AddParameter("@behavior_prompt", behaviorPrompt);
        command.AddParameter("@language_model", languageModel);
        command.AddParameter("@enabled", enabled);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool SetCapability(long agentId, string capability, string languageModel, bool enabled)
    {
        using var command = connectionFactory.CreateCommand(
            "INSERT INTO `agent_capabilities` (`agent_id`, `capability`, `language_model`, `enabled`) "
            + "VALUES (@agent_id, @capability, @language_model, @enabled) "
            + "ON DUPLICATE KEY UPDATE `enabled` = VALUES(`enabled`)");
        command.AddParameter("@agent_id", agentId);
        command.AddParameter("@capability", capability);
        command.AddParameter("@language_model", languageModel);
        command.AddParameter("@enabled", enabled);
        return command.ExecuteNonQuery() > 0;
    }

    private Dictionary<long, List<AgentCapability>> LoadCapabilities()
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `agent_id`, `capability`, `language_model`, `enabled` FROM `agent_capabilities` "
            + "ORDER BY `agent_id`, `capability`, `language_model`");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, List<AgentCapability>>();
        while (reader.Read())
        {
            var agentId = reader.GetInt64(reader.GetOrdinal("agent_id"));
            if (!result.TryGetValue(agentId, out var list))
            {
                result[agentId] = list = [];
            }

            list.Add(new AgentCapability(
                reader.GetString(reader.GetOrdinal("capability")),
                reader.GetString(reader.GetOrdinal("language_model")),
                reader.GetBoolean(reader.GetOrdinal("enabled"))));
        }

        return result;
    }
}
