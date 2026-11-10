using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

public static class AgentConfigPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "AgentConfig";
        patcher.Init(system);

        string[] statements =
        [
            """
            CREATE TABLE IF NOT EXISTS `agent_definitions` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(128) NOT NULL,
                `role` VARCHAR(255) NOT NULL,
                `instructions` LONGTEXT NOT NULL,
                `language_model_key` VARCHAR(128) NOT NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agent_definitions_name` (`name`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_capabilities` (
                `agent_id` BIGINT NOT NULL,
                `capability` VARCHAR(128) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`agent_id`, `capability`),
                CONSTRAINT `fk_agent_capabilities_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`) ON DELETE CASCADE
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `language_models` (
                `model_key` VARCHAR(128) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`model_key`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_metrics` (
                `agent_id` BIGINT NOT NULL,
                `input_tokens` BIGINT NOT NULL DEFAULT 0,
                `output_tokens` BIGINT NOT NULL DEFAULT 0,
                `tool_calls` BIGINT NOT NULL DEFAULT 0,
                `duration_ms` BIGINT NOT NULL DEFAULT 0,
                `success_count` BIGINT NOT NULL DEFAULT 0,
                `failure_count` BIGINT NOT NULL DEFAULT 0,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`agent_id`),
                CONSTRAINT `fk_agent_metrics_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`) ON DELETE CASCADE
            )
            """,
            """
            INSERT INTO `language_models` (`model_key`, `enabled`)
            VALUES ('gpt-5-mini', TRUE)
            ON DUPLICATE KEY UPDATE `model_key` = `language_models`.`model_key`
            """,
            """
            INSERT INTO `agent_definitions` (`name`, `role`, `instructions`, `language_model_key`)
            VALUES ('default', 'General purpose assistant',
                'You are a helpful assistant. Follow the task description and use the enabled tools when needed.',
                'gpt-5-mini')
            ON DUPLICATE KEY UPDATE `name` = `agent_definitions`.`name`
            """,
            """
            INSERT INTO `agent_metrics` (`agent_id`)
            SELECT `id` FROM `agent_definitions` WHERE `name` = 'default'
            ON DUPLICATE KEY UPDATE `agent_id` = `agent_metrics`.`agent_id`
            """
        ];

        for (var i = 0; i < statements.Length; i++)
        {
            patcher.Patch(system, i + 1, statements[i]);
            if (!patcher.Successful())
            {
                return false;
            }
        }

        return true;
    }
}
