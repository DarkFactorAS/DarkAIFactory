using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

public static class MigrationHistory
{
    public static void Initialize(IDbConnectionFactory connectionFactory)
    {
        using (var command = connectionFactory.CreateCommand(
            """
            CREATE TABLE IF NOT EXISTS `version` (
                `system` VARCHAR(50) NOT NULL DEFAULT '',
                `patchId` INT NOT NULL,
                `created` DATETIME NOT NULL,
                PRIMARY KEY (`system`, `patchId`)
            )
            """))
        {
            command.ExecuteNonQuery();
        }

        var columns = new List<string>();
        using (var command = connectionFactory.CreateCommand(
            """
            SELECT COLUMN_NAME FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'version' AND INDEX_NAME = 'PRIMARY'
            ORDER BY SEQ_IN_INDEX
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
        }

        if (columns.SequenceEqual(["system", "patchId"]))
        {
            return;
        }

        if (!columns.SequenceEqual(["patchId"]))
        {
            throw new InvalidOperationException("Unexpected migration history primary key; database migration stopped.");
        }

        // The library keys history only by patchId, but each system starts at 1.
        using var upgrade = connectionFactory.CreateCommand(
            "ALTER TABLE `version` DROP PRIMARY KEY, ADD PRIMARY KEY (`system`, `patchId`)");
        upgrade.ExecuteNonQuery();
    }
}
