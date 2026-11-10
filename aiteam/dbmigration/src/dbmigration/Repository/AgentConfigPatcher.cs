using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

/// <summary>
/// Agent configuration and metrics schema. Only creates new tables (and seeds one default agent);
/// no existing table is altered. Every statement is idempotent.
/// </summary>
public static class AgentConfigPatcher
{
    public const string System = "AgentConfig";
    public const string DefaultModelId = "claude-sonnet-5-5";
    public const string DefaultAgentName = "default";

    public static bool Run(IDBPatcher patcher)
    {
        patcher.Init(System);
        string[] statements =
        [
            """
            CREATE TABLE IF NOT EXISTS `language_models` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `model_id` VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `display_name` VARCHAR(255) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_language_models_model_id` (`model_id`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_definitions` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `role` VARCHAR(255) NOT NULL,
                `instructions` TEXT NOT NULL,
                `language_model_id` BIGINT NOT NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agent_definitions_name` (`name`),
                CONSTRAINT `fk_agent_definitions_language_model` FOREIGN KEY (`language_model_id`)
                    REFERENCES `language_models` (`id`)
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `agent_capabilities` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `name` VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_agent_capabilities_agent_name` (`agent_id`, `name`),
                CONSTRAINT `fk_agent_capabilities_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`) ON DELETE CASCADE
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS `task_metrics` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `task_key` VARCHAR(255) NOT NULL,
                `tokens_in` BIGINT NULL,
                `tokens_out` BIGINT NULL,
                `tool_call_count` INT NOT NULL DEFAULT 0,
                `duration_ms` BIGINT NOT NULL DEFAULT 0,
                `status` VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `started` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `completed` DATETIME NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                KEY `ix_task_metrics_task_key` (`task_key`),
                KEY `ix_task_metrics_agent_started` (`agent_id`, `started`),
                CONSTRAINT `fk_task_metrics_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `agent_definitions` (`id`),
                CONSTRAINT `ck_task_metrics_status` CHECK (`status` IN ('success', 'failed'))
            )
            """,
            $"""
            INSERT INTO `language_models` (`model_id`, `display_name`, `enabled`)
            SELECT '{DefaultModelId}', 'Claude Sonnet 5.5', TRUE
            WHERE NOT EXISTS (SELECT 1 FROM `language_models` WHERE `model_id` = '{DefaultModelId}')
            """,
            $"""
            INSERT INTO `agent_definitions` (`name`, `role`, `instructions`, `language_model_id`)
            SELECT '{DefaultAgentName}', 'Software developer',
                'Implement the assigned task in the target repository following its coding standards, and validate the change with the repository tests.',
                `id`
            FROM `language_models` WHERE `model_id` = '{DefaultModelId}'
                AND NOT EXISTS (SELECT 1 FROM `agent_definitions` WHERE `name` = '{DefaultAgentName}')
            """
        ];

        for (var i = 0; i < statements.Length; i++)
        {
            patcher.Patch(System, i + 1, statements[i]);
            if (!patcher.Successful())
            {
                return false;
            }
        }

        return true;
    }
}
