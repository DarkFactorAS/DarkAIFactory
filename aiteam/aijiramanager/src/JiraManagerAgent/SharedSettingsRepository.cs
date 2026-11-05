using DFCommonLib.DataAccess;

namespace JiraManagerAgent;

// Jira credentials live in the shared database; this agent only needs the 'shared' rows.
internal sealed class SharedSettingsRepository(IDbConnectionFactory connectionFactory)
{
    public AgentConfiguration Load(int pollSeconds)
    {
        using var command = connectionFactory.CreateCommand(
            "SELECT `name`, `value` FROM `settings` WHERE `client` = 'shared'");
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return AgentConfiguration.FromValues(values, pollSeconds);
    }
}
