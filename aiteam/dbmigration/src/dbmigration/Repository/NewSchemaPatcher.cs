using DFCommonLib.DataAccess;

namespace dbmigration.Repository;

// All tables created here are prefixed new_ and never touch the existing tables.
public static class NewSchemaPatcher
{
    public static bool Run(IDBPatcher patcher)
    {
        const string system = "NewSchema";
        patcher.Init(system);
        patcher.Patch(system, 1,
            """
            CREATE TABLE IF NOT EXISTS `new_language_models` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(128) NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT TRUE,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_new_language_models_name` (`name`)
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 2,
            """
            CREATE TABLE IF NOT EXISTS `new_agent_config` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `name` VARCHAR(128) NOT NULL,
                `role` VARCHAR(255) NOT NULL,
                `task_instruction` TEXT NOT NULL,
                `behavior_instruction` TEXT NOT NULL,
                `language_model_id` BIGINT NULL,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `updated` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                UNIQUE KEY `uq_new_agent_config_name` (`name`),
                CONSTRAINT `fk_new_agent_config_language_model` FOREIGN KEY (`language_model_id`)
                    REFERENCES `new_language_models` (`id`)
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 3,
            """
            CREATE TABLE IF NOT EXISTS `new_agent_capabilities` (
                `agent_id` BIGINT NOT NULL,
                `capability` VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                `enabled` BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY (`agent_id`, `capability`),
                CONSTRAINT `fk_new_agent_capabilities_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `new_agent_config` (`id`) ON DELETE CASCADE
            )
            """);
        if (!patcher.Successful())
        {
            return false;
        }

        patcher.Patch(system, 4,
            """
            CREATE TABLE IF NOT EXISTS `new_agent_metrics` (
                `id` BIGINT NOT NULL AUTO_INCREMENT,
                `agent_id` BIGINT NOT NULL,
                `jira_key` VARCHAR(255) NULL,
                `input_tokens` BIGINT NOT NULL DEFAULT 0,
                `output_tokens` BIGINT NOT NULL DEFAULT 0,
                `tool_calls` BIGINT NOT NULL DEFAULT 0,
                `task_duration_ms` BIGINT NOT NULL DEFAULT 0,
                `success_count` BIGINT NOT NULL DEFAULT 0,
                `failure_count` BIGINT NOT NULL DEFAULT 0,
                `created` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (`id`),
                KEY `ix_new_agent_metrics_agent` (`agent_id`, `created`),
                CONSTRAINT `fk_new_agent_metrics_agent` FOREIGN KEY (`agent_id`)
                    REFERENCES `new_agent_config` (`id`) ON DELETE CASCADE
            )
            """);
        return patcher.Successful();
    }
}
